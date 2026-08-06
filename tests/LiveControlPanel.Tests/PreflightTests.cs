using LiveControlPanel.Youtube;
using Xunit;

namespace LiveControlPanel.Tests;

/// <summary>
/// FR 4.4. The previousBroadcast item gets the most attention because it is the one that ruins an
/// evening service, and because it must never surface as a raw API error.
/// </summary>
public class PreflightTests
{
    private static LiveControlPanel.Core.PreflightItem Item(
        IEnumerable<LiveControlPanel.Core.PreflightItem> items, string key) =>
        items.Single(i => i.Key == key);

    [Fact]
    public async Task All_five_checks_are_always_reported()
    {
        using var host = new TestHost();

        var items = await host.Preflight.RunAsync();

        Assert.Equal(
            new[] { "obs", "audio", "previousBroadcast", "auth", "video" },
            items.Select(i => i.Key).ToArray());
    }

    [Fact]
    public async Task Every_failing_check_carries_a_non_technical_message()
    {
        using var host = new TestHost();
        host.Obs.Connected = false;
        host.YouTube.Auth = new AuthInfo(false, null, null, new LiveControlPanel.Core.Msg("尚未授权 YouTube 账号。", "Not authorized yet."));
        host.YouTube.Unfinished = new List<BroadcastInfo>
        {
            new("old1", "8/5/2026 Morning Service", "live", "url"),
        };

        var items = await host.Preflight.RunAsync();

        foreach (var item in items.Where(i => !i.Ok))
        {
            Assert.False(string.IsNullOrWhiteSpace(item.Message.Zh));
            Assert.DoesNotContain("Exception", item.Message.Zh);
            Assert.DoesNotContain("null", item.Message.Zh);
            Assert.DoesNotContain("403", item.Message.Zh);
        }
    }

    // ---------------------------------------------------------------- obs

    [Fact]
    public async Task Obs_check_tells_the_operator_to_open_obs()
    {
        using var host = new TestHost();
        host.Obs.Connected = false;

        var item = Item(await host.Preflight.RunAsync(), "obs");

        Assert.False(item.Ok);
        Assert.Contains("OBS", item.Message.Zh);
    }

    /// <summary>
    /// The most common real cause is a running OBS with its WebSocket server switched off, which looks
    /// identical to a closed OBS. Telling that operator to "open OBS" points at the one thing that is
    /// already correct, so each cause must name its own fix.
    /// </summary>
    [Fact]
    public async Task Obs_check_names_the_websocket_switch_when_nothing_is_listening()
    {
        using var host = new TestHost();
        host.Obs.Connected = false;
        host.Obs.ProblemToReport = LiveControlPanel.Obs.ObsProblem.NotListening;

        var item = Item(await host.Preflight.RunAsync(), "obs");

        Assert.False(item.Ok);
        Assert.Contains("WebSocket", item.Message.Zh);
        Assert.Contains("启用", item.Message.Zh);
        Assert.Contains("Enable WebSocket server", item.Message.En);
    }

    [Fact]
    public async Task Obs_check_names_the_password_when_obs_rejects_it()
    {
        using var host = new TestHost();
        host.Obs.Connected = false;
        host.Obs.ProblemToReport = LiveControlPanel.Obs.ObsProblem.AuthenticationFailed;

        var item = Item(await host.Preflight.RunAsync(), "obs");

        Assert.False(item.Ok);
        Assert.Contains("密码", item.Message.Zh);
        Assert.Contains("password", item.Message.En);
        // Must not send them looking at whether OBS is open — it demonstrably is.
        Assert.DoesNotContain("启用 WebSocket 服务器", item.Message.Zh);
    }

    [Fact]
    public async Task Obs_check_names_the_address_when_it_is_unusable()
    {
        using var host = new TestHost();
        host.Obs.Connected = false;
        host.Obs.ProblemToReport = LiveControlPanel.Obs.ObsProblem.BadUrl;
        host.Config.UpdateSettings(s => s.Obs.Url = "not-a-url");

        var item = Item(await host.Preflight.RunAsync(), "obs");

        Assert.False(item.Ok);
        Assert.Contains("not-a-url", item.Message.Zh);
        Assert.Contains("ws://localhost:4455", item.Message.Zh);
    }

    [Fact]
    public async Task Obs_check_passes_when_connected()
    {
        using var host = new TestHost();

        Assert.True(Item(await host.Preflight.RunAsync(), "obs").Ok);
    }

    // ---------------------------------------------------------------- audio

    [Fact]
    public async Task Audio_check_passes_when_the_input_exists_and_has_level()
    {
        using var host = new TestHost();
        host.Obs.Inputs = new List<string> { "ProFX" };
        host.Obs.AudioPeak = 0.35;

        Assert.True(Item(await host.Preflight.RunAsync(), "audio").Ok);
    }

    [Fact]
    public async Task Audio_check_points_at_the_mixer_when_the_input_is_missing()
    {
        using var host = new TestHost();
        host.Obs.Inputs = new List<string> { "Mic/Aux" };

        var item = Item(await host.Preflight.RunAsync(), "audio");

        Assert.False(item.Ok);
        Assert.Contains("调音台", item.Message.Zh);
        Assert.Contains("USB", item.Message.Zh);
    }

    [Fact]
    public async Task Audio_check_fails_when_the_input_exists_but_is_silent()
    {
        using var host = new TestHost();
        host.Obs.AudioPeak = 0.0;

        var item = Item(await host.Preflight.RunAsync(), "audio");

        Assert.False(item.Ok);
        Assert.Contains("没有声音", item.Message.Zh);
    }

    [Fact]
    public async Task Audio_check_fails_when_no_level_data_has_arrived()
    {
        using var host = new TestHost();
        host.Obs.AudioPeak = null;

        var item = Item(await host.Preflight.RunAsync(), "audio");

        Assert.False(item.Ok);
        Assert.Contains("读不到音量", item.Message.Zh);
    }

    [Fact]
    public async Task Audio_check_is_skipped_when_no_input_name_is_configured()
    {
        using var host = new TestHost();
        host.Config.UpdateSettings(s => s.Obs.AudioInputName = "");

        Assert.True(Item(await host.Preflight.RunAsync(), "audio").Ok);
    }

    // ---------------------------------------------------------------- previousBroadcast (highest risk)

    [Fact]
    public async Task Previous_broadcast_check_passes_when_nothing_is_unfinished()
    {
        using var host = new TestHost();

        var item = Item(await host.Preflight.RunAsync(), "previousBroadcast");

        Assert.True(item.Ok);
        Assert.Null(item.Action);
    }

    [Fact]
    public async Task Previous_broadcast_check_offers_a_one_click_fix()
    {
        using var host = new TestHost();
        host.YouTube.Unfinished = new List<BroadcastInfo>
        {
            new("old1", "8/5/2026 Morning Service", "live", "url"),
        };

        var item = Item(await host.Preflight.RunAsync(), "previousBroadcast");

        Assert.False(item.Ok);
        Assert.Equal("end-previous", item.Action);
        Assert.Contains("仍在进行", item.Message.Zh);
        Assert.Contains("8/5/2026 Morning Service", item.Message.Zh);
    }

    [Fact]
    public async Task Previous_broadcast_check_reports_an_api_failure_as_plain_advice()
    {
        using var host = new TestHost();
        host.YouTube.FailOnce[nameof(FakeYouTubeClient.ListUnfinishedBroadcastsAsync)] =
            new HttpRequestException("connection reset");

        var item = Item(await host.Preflight.RunAsync(), "previousBroadcast");

        Assert.False(item.Ok);
        Assert.Contains("网络", item.Message.Zh);
        Assert.DoesNotContain("connection reset", item.Message.Zh);
    }

    [Fact]
    public async Task Previous_broadcast_check_routes_an_unauthorized_state_to_reauthorization()
    {
        using var host = new TestHost();
        host.YouTube.FailOnce[nameof(FakeYouTubeClient.ListUnfinishedBroadcastsAsync)] =
            new NotAuthorizedException();

        var item = Item(await host.Preflight.RunAsync(), "previousBroadcast");

        Assert.False(item.Ok);
        Assert.Equal("reauthorize", item.Action);
    }

    // ---------------------------------------------------------------- auth

    [Fact]
    public async Task Auth_check_shows_the_remaining_validity()
    {
        using var host = new TestHost();
        host.YouTube.Auth = new AuthInfo(true, 173, DateTime.Now.AddDays(-7), null);

        var item = Item(await host.Preflight.RunAsync(), "auth");

        Assert.True(item.Ok);
        Assert.Contains("173", item.Message.Zh);
    }

    [Fact]
    public async Task Auth_check_warns_before_the_token_can_die_unattended()
    {
        using var host = new TestHost();
        host.YouTube.Auth = new AuthInfo(true, 10, DateTime.Now.AddDays(-170), null);

        var item = Item(await host.Preflight.RunAsync(), "auth");

        Assert.False(item.Ok);
        Assert.Equal("reauthorize", item.Action);
        Assert.Contains("10", item.Message.Zh);
    }

    [Fact]
    public async Task Auth_check_offers_reauthorization_when_invalid()
    {
        using var host = new TestHost();
        host.YouTube.Auth = new AuthInfo(false, null, null, new LiveControlPanel.Core.Msg("授权已失效，需要重新授权。", "Authorization expired."));

        var item = Item(await host.Preflight.RunAsync(), "auth");

        Assert.False(item.Ok);
        Assert.Equal("reauthorize", item.Action);
    }

    // ---------------------------------------------------------------- video

    [Fact]
    public async Task Video_check_is_skipped_when_no_sources_are_configured()
    {
        using var host = new TestHost();

        Assert.True(Item(await host.Preflight.RunAsync(), "video").Ok);
    }

    [Fact]
    public async Task Video_check_passes_when_every_configured_source_is_active()
    {
        using var host = new TestHost();
        host.Config.UpdateSettings(s => s.Obs.VideoSourceNames = new List<string> { "采集卡", "电视" });
        host.Obs.SourceActive["采集卡"] = true;
        host.Obs.SourceActive["电视"] = true;

        Assert.True(Item(await host.Preflight.RunAsync(), "video").Ok);
    }

    [Fact]
    public async Task Video_check_names_the_dead_source_and_what_to_check()
    {
        using var host = new TestHost();
        host.Config.UpdateSettings(s => s.Obs.VideoSourceNames = new List<string> { "采集卡", "电视" });
        host.Obs.SourceActive["采集卡"] = true;
        host.Obs.SourceActive["电视"] = false;

        var item = Item(await host.Preflight.RunAsync(), "video");

        Assert.False(item.Ok);
        Assert.Contains("电视", item.Message.Zh);
        Assert.Contains("摄像机", item.Message.Zh);
    }

    [Fact]
    public async Task Video_check_reports_a_source_obs_does_not_know_about()
    {
        using var host = new TestHost();
        host.Config.UpdateSettings(s => s.Obs.VideoSourceNames = new List<string> { "拼错的名字" });

        var item = Item(await host.Preflight.RunAsync(), "video");

        Assert.False(item.Ok);
        Assert.Contains("找不到", item.Message.Zh);
    }

    // ---------------------------------------------------------------- never blocking

    [Fact]
    public async Task A_failing_preflight_never_prevents_going_live()
    {
        using var host = new TestHost();
        host.Obs.Connected = false;
        host.Obs.Inputs = new List<string>();
        host.YouTube.Auth = new AuthInfo(false, null, null, new LiveControlPanel.Core.Msg("授权已失效。", "Authorization expired."));
        host.SetToday();

        var items = await host.Preflight.RunAsync();
        Assert.Contains(items, i => !i.Ok);

        // The emergency path: everything is complaining, and the stream still starts.
        var outcome = await host.Orchestrator.StartTodayAsync();

        Assert.True(outcome.Ok);
        Assert.Equal(1, host.YouTube.CreateCalls);
    }
}
