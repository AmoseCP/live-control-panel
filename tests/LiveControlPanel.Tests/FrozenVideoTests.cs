using LiveControlPanel.Core;
using LiveControlPanel.Devices;
using Xunit;

namespace LiveControlPanel.Tests;

/// <summary>
/// The frozen-picture check, and the frame comparison under it.
///
/// This check exists because of a gap the deployment guide had to warn about in prose: the capture
/// card's own "No Signal" screen is, to OBS, a perfectly good picture — the source reports as active
/// and the pre-flight went green while the congregation would have seen a placeholder.
///
/// Half of these tests are about NOT firing. A check that cries wolf at 04:40 teaches the operator
/// to click past the whole checklist, which is worse than having no check.
/// </summary>
public sealed class FrozenVideoTests
{
    private static PreflightItem Video(IEnumerable<PreflightItem> items) =>
        items.Single(i => i.Key == "video");

    private static void WatchOnly(TestHost host, params string[] names) =>
        host.Config.UpdateSettings(s =>
        {
            // Only the freeze check is under test here; the active/inactive checks have their own.
            s.Obs.VideoSourceNames = new List<string>();
            s.Obs.FrozenFrameSourceNames = names.ToList();
        });

    // ---------------------------------------------------------------- the comparison itself

    [Fact]
    public void Identical_frames_are_frozen() =>
        Assert.True(FrameFreeze.LooksFrozen(new byte[] { 1, 2, 3 }, new byte[] { 1, 2, 3 }));

    [Fact]
    public void Different_frames_are_not_frozen() =>
        Assert.False(FrameFreeze.LooksFrozen(new byte[] { 1, 2, 3 }, new byte[] { 1, 2, 4 }));

    [Fact]
    public void Frames_of_different_length_are_not_frozen() =>
        Assert.False(FrameFreeze.LooksFrozen(new byte[] { 1, 2, 3 }, new byte[] { 1, 2, 3, 4 }));

    /// <summary>"Cannot tell" must never resolve to "no picture".</summary>
    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void A_missing_frame_is_never_frozen(bool firstMissing, bool secondMissing) =>
        Assert.False(FrameFreeze.LooksFrozen(
            firstMissing ? null : new byte[] { 1 },
            secondMissing ? null : new byte[] { 1 }));

    [Fact]
    public void An_empty_frame_is_never_frozen() =>
        Assert.False(FrameFreeze.LooksFrozen(Array.Empty<byte>(), Array.Empty<byte>()));

    // ---------------------------------------------------------------- the check firing

    [Fact]
    public async Task A_frozen_camera_fails_the_video_check()
    {
        using var host = new TestHost();
        WatchOnly(host, "主摄像机");
        host.Obs.WithFrozenSource("主摄像机");

        var item = Video(await host.Preflight.RunAsync());

        Assert.False(item.Ok);
        Assert.Contains("主摄像机", item.Message.Zh);
    }

    /// <summary>
    /// The message has to contradict the operator's instinct, because the instinct is wrong here:
    /// restarting the camera and restarting OBS both do nothing to a wedged USB device.
    /// </summary>
    [Fact]
    public async Task The_message_says_restarting_the_camera_and_OBS_will_not_help()
    {
        using var host = new TestHost();
        WatchOnly(host, "主摄像机");
        host.Obs.WithFrozenSource("主摄像机");

        var item = Video(await host.Preflight.RunAsync());

        Assert.Contains("重启摄像机和重启 OBS", item.Message.Zh);
        Assert.Contains("re-enumerate", item.Message.En);
    }

    [Fact]
    public async Task The_fix_is_offered_where_the_problem_is_reported()
    {
        using var host = new TestHost();
        WatchOnly(host, "主摄像机");
        host.Obs.WithFrozenSource("主摄像机");
        host.EnableCaptureReset();

        var item = Video(await host.Preflight.RunAsync());

        Assert.Equal("reset-capture", item.Action);
    }

    /// <summary>
    /// Without the reset configured there is no button to offer, so the message must carry the
    /// manual recovery instead of pointing at something that is not on screen.
    /// </summary>
    [Fact]
    public async Task Without_the_reset_configured_the_manual_recovery_is_named_instead()
    {
        using var host = new TestHost();
        WatchOnly(host, "主摄像机");
        host.Obs.WithFrozenSource("主摄像机");

        var item = Video(await host.Preflight.RunAsync());

        Assert.Null(item.Action);
        Assert.Contains("拔插", item.Message.Zh);
        Assert.Contains("unplug", item.Message.En);
    }

    [Fact]
    public async Task The_first_frozen_source_is_the_one_reported()
    {
        using var host = new TestHost();
        WatchOnly(host, "主摄像机", "PPT采集");
        host.Obs.WithMovingSource("主摄像机");
        host.Obs.WithFrozenSource("PPT采集");

        var item = Video(await host.Preflight.RunAsync());

        Assert.False(item.Ok);
        Assert.Contains("PPT采集", item.Message.Zh);
    }

    // ---------------------------------------------------------------- the check staying quiet

    [Fact]
    public async Task A_live_camera_passes()
    {
        using var host = new TestHost();
        WatchOnly(host, "主摄像机");
        host.Obs.WithMovingSource("主摄像机");

        Assert.True(Video(await host.Preflight.RunAsync()).Ok);
    }

    /// <summary>
    /// A source no scene is showing may be deactivated by OBS, which looks exactly like a freeze and
    /// has nothing to do with the camera. Not judged.
    /// </summary>
    [Fact]
    public async Task A_source_that_is_not_on_air_is_not_judged()
    {
        using var host = new TestHost();
        WatchOnly(host, "主摄像机");
        host.Obs.WithFrozenSource("主摄像机", active: false);

        Assert.True(Video(await host.Preflight.RunAsync()).Ok);
    }

    [Fact]
    public async Task A_source_OBS_will_not_render_is_not_judged()
    {
        using var host = new TestHost();
        WatchOnly(host, "主摄像机");
        host.Obs.WithUnrenderableSource("主摄像机");

        Assert.True(Video(await host.Preflight.RunAsync()).Ok);
    }

    [Fact]
    public async Task A_screenshot_request_that_errors_is_not_judged()
    {
        using var host = new TestHost();
        WatchOnly(host, "主摄像机");
        host.Obs.WithFrozenSource("主摄像机");
        host.Obs.FailOnce[nameof(host.Obs.GetSourceFrameAsync)] = new InvalidOperationException("no");

        Assert.True(Video(await host.Preflight.RunAsync()).Ok);
    }

    /// <summary>Nothing configured, nothing checked — the default must change no behaviour at all.</summary>
    [Fact]
    public async Task Nothing_is_checked_by_default()
    {
        using var host = new TestHost();
        host.Obs.WithFrozenSource("主摄像机");

        var item = Video(await host.Preflight.RunAsync());

        Assert.True(item.Ok);
        Assert.Empty(host.Config.Settings.Obs.FrozenFrameSourceNames);
    }

    [Fact]
    public async Task OBS_being_disconnected_reports_that_rather_than_a_frozen_picture()
    {
        using var host = new TestHost();
        WatchOnly(host, "主摄像机");
        host.Obs.WithFrozenSource("主摄像机");
        host.Obs.Connected = false;

        var item = Video(await host.Preflight.RunAsync());

        Assert.False(item.Ok);
        Assert.Contains("OBS", item.Message.En);
        Assert.Null(item.Action);
    }

    // ---------------------------------------------------------------- bad configuration

    /// <summary>
    /// The failure this check exists to catch, produced by a typo in the settings field: the name
    /// was skipped, nothing looked frozen, and the video item went green while the congregation
    /// would have been looking at a "No Signal" screen.
    /// </summary>
    [Fact]
    public async Task A_watched_source_OBS_does_not_know_fails_the_check_instead_of_passing_it()
    {
        using var host = new TestHost();
        WatchOnly(host, "主摄象机");            // 象 instead of 像
        host.Obs.WithFrozenSource("主摄像机");  // the real one, spelled correctly

        var item = Video(await host.Preflight.RunAsync());

        Assert.False(item.Ok);
        Assert.Contains("主摄象机", item.Message.Zh);
        Assert.Contains("不存在", item.Message.Zh);
        Assert.Contains("do not exist in OBS", item.Message.En);
    }

    /// <summary>A configuration error is not something "reset the capture card" can fix.</summary>
    [Fact]
    public async Task A_bad_name_offers_no_hardware_action()
    {
        using var host = new TestHost();
        WatchOnly(host, "打错了");
        host.EnableCaptureReset();

        var item = Video(await host.Preflight.RunAsync());

        Assert.False(item.Ok);
        Assert.Null(item.Action);
    }

    [Fact]
    public async Task Every_bad_name_is_listed_not_just_the_first()
    {
        using var host = new TestHost();
        WatchOnly(host, "打错了", "也打错了");

        var item = Video(await host.Preflight.RunAsync());

        Assert.Contains("打错了", item.Message.Zh);
        Assert.Contains("也打错了", item.Message.Zh);
    }

    /// <summary>
    /// An empty input list means the request failed, not that OBS has no inputs. Calling every
    /// watched name unknown on that basis would be a false alarm of exactly the kind this check
    /// must not raise — so the name check stands down and the freeze comparison still runs.
    /// </summary>
    [Fact]
    public async Task An_unavailable_input_list_does_not_condemn_every_name()
    {
        using var host = new TestHost();
        WatchOnly(host, "主摄像机");
        host.Obs.WithFrozenSource("主摄像机");
        host.Obs.Inputs.Clear();

        var item = Video(await host.Preflight.RunAsync());

        // Still judged on the frames, and still reported as frozen rather than as a bad name.
        Assert.False(item.Ok);
        Assert.Contains("没有变化", item.Message.Zh);
    }

    [Fact]
    public async Task A_failing_input_list_request_does_not_condemn_every_name()
    {
        using var host = new TestHost();
        WatchOnly(host, "主摄像机");
        host.Obs.WithMovingSource("主摄像机");
        host.Obs.FailOnce[nameof(host.Obs.GetInputNamesAsync)] = new InvalidOperationException("no");

        Assert.True(Video(await host.Preflight.RunAsync()).Ok);
    }

    // ---------------------------------------------------------------- the guard

    [Theory]
    [InlineData("USB", "USB Root Hub")]
    [InlineData("HIDClass", "USB Input Device")]
    [InlineData("Net", "Realtek Gaming GbE")]
    public void Devices_that_would_take_the_machine_down_are_not_offered(string deviceClass, string name) =>
        Assert.False(DeviceResetGuard.IsCandidate(new PnpDeviceInfo("id", name, deviceClass, "OK")));

    [Theory]
    [InlineData("Camera", "AVerMedia HDMI Capture")]
    [InlineData("Image", "USB Video Device")]
    [InlineData("Media", "Mackie ProFX")]
    public void Plausible_capture_hardware_is_offered(string deviceClass, string name) =>
        Assert.True(DeviceResetGuard.IsCandidate(new PnpDeviceInfo("id", name, deviceClass, "OK")));

    /// <summary>A vendor driver can invent its own class name, so the picker also matches on names.</summary>
    [Fact]
    public void A_vendor_class_is_offered_when_the_name_looks_like_capture_hardware() =>
        Assert.True(DeviceResetGuard.IsCandidate(
            new PnpDeviceInfo("id", "AVerMedia Live Gamer", "AVerMediaTechnologies", "OK")));

    [Fact]
    public void A_refused_class_stays_refused_even_when_its_name_looks_right() =>
        Assert.False(DeviceResetGuard.IsCandidate(
            new PnpDeviceInfo("id", "USB Video Capture Hub", "USB", "OK")));
}
