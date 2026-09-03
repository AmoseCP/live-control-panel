using LiveControlPanel.Core;
using LiveControlPanel.Devices;
using Xunit;

namespace LiveControlPanel.Tests;

/// <summary>
/// Re-enumerating the USB capture card from the panel.
///
/// The failure cases carry the weight here. This feature reaches outside the panel and switches a
/// piece of hardware off, so the tests that matter are the ones proving it refuses to do that
/// without an administrator's explicit choice — and the one proving it never reports "done" while
/// the card is still switched off.
/// </summary>
public sealed class CaptureRecoveryTests
{
    // ---------------------------------------------------------------- refusals

    [Fact]
    public async Task It_is_off_until_switched_on()
    {
        using var host = new TestHost();

        var result = await host.CaptureRecovery.ResetAsync();

        Assert.False(result.Ok);
        Assert.Empty(host.Devices.Disabled);
        Assert.Contains("还没有启用", result.Message.Zh);
    }

    [Fact]
    public async Task Switched_on_but_with_no_device_chosen_touches_nothing()
    {
        using var host = new TestHost();
        host.Config.UpdateSettings(s => s.CaptureReset.Enabled = true);

        var result = await host.CaptureRecovery.ResetAsync();

        Assert.False(result.Ok);
        Assert.Empty(host.Devices.Disabled);
    }

    /// <summary>
    /// A capture card moved to another USB port comes back with a different instance id. Acting on
    /// the stale one would be acting on nothing — or, worse, on whatever now holds that id.
    /// </summary>
    [Fact]
    public async Task A_configured_device_that_is_not_present_is_reported_not_guessed_at()
    {
        using var host = new TestHost();
        host.EnableCaptureReset();
        host.Devices.Devices.Clear();

        var result = await host.CaptureRecovery.ResetAsync();

        Assert.False(result.Ok);
        Assert.Empty(host.Devices.Disabled);
        Assert.Contains("USB", result.Message.Zh);
        Assert.Contains("port", result.Message.En);
    }

    /// <summary>
    /// The settings file gets hand-edited. Resetting a hub takes the keyboard and the mixer down
    /// with it, so the class guard runs at reset time and not only in the picker.
    /// </summary>
    [Theory]
    [InlineData("USB")]
    [InlineData("HIDClass")]
    [InlineData("Keyboard")]
    [InlineData("Net")]
    [InlineData("DiskDrive")]
    public async Task A_dangerous_device_class_is_refused_even_when_configured(string deviceClass)
    {
        using var host = new TestHost();
        host.EnableCaptureReset();
        host.Devices.Devices = new List<PnpDeviceInfo>
        {
            new(FakeDeviceResetter.CaptureCardId, "USB Root Hub", deviceClass, "OK"),
        };

        var result = await host.CaptureRecovery.ResetAsync();

        Assert.False(result.Ok);
        Assert.Empty(host.Devices.Disabled);
        Assert.Contains(deviceClass, result.Message.En);
    }

    // ---------------------------------------------------------------- the happy path

    [Fact]
    public async Task It_disables_then_re_enables_the_configured_device()
    {
        using var host = new TestHost();
        host.EnableCaptureReset();

        var result = await host.CaptureRecovery.ResetAsync();

        Assert.True(result.Ok, result.Message.En);
        Assert.Equal(new[] { FakeDeviceResetter.CaptureCardId }, host.Devices.Disabled);
        Assert.Equal(new[] { FakeDeviceResetter.CaptureCardId }, host.Devices.Enabled);
    }

    /// <summary>
    /// The device coming back is not enough: the OBS source is still holding the handle it opened
    /// before, so the picture does not return until the source is re-initialised.
    /// </summary>
    [Fact]
    public async Task It_makes_OBS_re_open_the_source()
    {
        using var host = new TestHost();
        host.EnableCaptureReset("主摄像机");

        await host.CaptureRecovery.ResetAsync();

        Assert.Equal(new[] { "主摄像机" }, host.Obs.RefreshedInputs);
    }

    [Fact]
    public async Task The_reset_is_recorded_so_the_operator_can_see_they_already_tried_it()
    {
        using var host = new TestHost();
        host.EnableCaptureReset();

        await host.CaptureRecovery.ResetAsync();

        var state = host.State.Snapshot();
        Assert.NotNull(state.CaptureReset.LastResetAt);
        Assert.NotNull(state.LastAction);
        Assert.Contains("采集卡", state.LastAction!.What.Zh);
    }

    /// <summary>OBS being closed is not a reason to withhold the device reset.</summary>
    [Fact]
    public async Task It_still_succeeds_when_OBS_is_not_connected()
    {
        using var host = new TestHost();
        host.EnableCaptureReset();
        host.Obs.Connected = false;

        var result = await host.CaptureRecovery.ResetAsync();

        Assert.True(result.Ok);
        Assert.Empty(host.Obs.RefreshedInputs);
        Assert.Contains("眼睛", result.Message.Zh);
    }

    [Fact]
    public async Task A_failed_OBS_refresh_still_counts_as_a_successful_reset()
    {
        using var host = new TestHost();
        host.EnableCaptureReset();
        host.Obs.FailOnce[nameof(host.Obs.RefreshInputAsync)] = new InvalidOperationException("nope");

        var result = await host.CaptureRecovery.ResetAsync();

        Assert.True(result.Ok);
        Assert.Single(host.Devices.Enabled);
        // The manual fallback is named, because that is what is left to do.
        Assert.Contains("eye icon", result.Message.En);
    }

    [Fact]
    public async Task With_no_OBS_source_configured_the_device_is_still_reset()
    {
        using var host = new TestHost();
        host.EnableCaptureReset(obsInputName: "");

        var result = await host.CaptureRecovery.ResetAsync();

        Assert.True(result.Ok);
        Assert.Single(host.Devices.Enabled);
        Assert.Empty(host.Obs.RefreshedInputs);
    }

    // ---------------------------------------------------------------- failure handling

    /// <summary>
    /// Disable needs an elevated process. Answering with an access-denied code would send an
    /// operator nowhere; the message names the scheduled-task setting that is missing.
    /// </summary>
    [Fact]
    public async Task A_refused_disable_names_elevation_and_the_manual_fallback()
    {
        using var host = new TestHost();
        host.EnableCaptureReset();
        host.Devices.DisableThrows = new DeviceResetFailedException("Disable needs an elevated process.");

        var result = await host.CaptureRecovery.ResetAsync();

        Assert.False(result.Ok);
        Assert.Contains("RunLevel Highest", result.Message.Zh);
        Assert.Contains("unplug", result.Message.En);
        Assert.Empty(host.Devices.Enabled);
    }

    /// <summary>
    /// A transient refusal must not leave the card switched off, so enable is retried rather than
    /// reported on the first failure.
    /// </summary>
    [Fact]
    public async Task Enable_is_retried_before_giving_up()
    {
        using var host = new TestHost();
        host.EnableCaptureReset();
        host.Devices.EnableFailure = attempt =>
            attempt == 1 ? new DeviceResetFailedException("busy") : null;

        var result = await host.CaptureRecovery.ResetAsync();

        Assert.True(result.Ok, result.Message.En);
        Assert.Equal(2, host.Devices.EnableAttempts);
        Assert.Single(host.Devices.Enabled);
    }

    /// <summary>
    /// The worst outcome this feature has: the card is off and stayed off. It must never be reported
    /// as success, and the message has to say exactly how to switch it back on by hand — this is not
    /// something to work out mid-service.
    /// </summary>
    [Fact]
    public async Task A_device_left_disabled_is_reported_unmistakably()
    {
        using var host = new TestHost();
        host.EnableCaptureReset();
        host.Devices.EnableFailure = _ => new DeviceResetFailedException("always fails");

        var result = await host.CaptureRecovery.ResetAsync();

        Assert.False(result.Ok);
        Assert.Empty(host.Devices.Enabled);
        Assert.Equal(3, host.Devices.EnableAttempts);
        Assert.Contains("设备管理器", result.Message.Zh);
        Assert.Contains("Device Manager", result.Message.En);
        Assert.Contains("AVerMedia HDMI Capture", result.Message.En);

        // Nothing may claim the reset happened.
        Assert.Null(host.State.Snapshot().CaptureReset.LastResetAt);
    }

    /// <summary>
    /// Two taps must not disable a device that the first run is about to re-enable, so a concurrent
    /// request is refused rather than queued — and refused as a non-error, because tapping twice on
    /// a slow morning is not a mistake.
    /// </summary>
    [Fact]
    public async Task A_second_tap_during_a_reset_is_refused_not_queued()
    {
        using var host = new TestHost();
        host.EnableCaptureReset();
        host.CaptureRecovery.SettleDelay = TimeSpan.FromMilliseconds(400);

        var first = host.CaptureRecovery.ResetAsync();
        await Task.Delay(50);
        var second = await host.CaptureRecovery.ResetAsync();

        Assert.True(second.Ok);
        Assert.Contains("请稍候", second.Message.Zh);

        Assert.True((await first).Ok);
        Assert.Single(host.Devices.Disabled);
        Assert.Single(host.Devices.Enabled);
    }

    // ---------------------------------------------------------------- state

    [Fact]
    public void The_button_is_hidden_until_a_device_is_actually_chosen()
    {
        using var host = new TestHost();

        Assert.False(host.State.Snapshot().CaptureReset.Enabled);

        host.Config.UpdateSettings(s => s.CaptureReset.Enabled = true);
        Assert.False(host.State.Snapshot().CaptureReset.Enabled);

        host.EnableCaptureReset();
        var state = host.State.Snapshot().CaptureReset;
        Assert.True(state.Enabled);
        Assert.Equal("AVerMedia HDMI Capture", state.DeviceName);
    }
}
