using LiveControlPanel.Config;
using LiveControlPanel.Core;
using LiveControlPanel.Youtube;
using Xunit;

namespace LiveControlPanel.Tests;

/// <summary>
/// Ways the panel could tell an operator something untrue about a live broadcast.
///
/// Every case here was found by auditing the shipped code rather than by a failure on air, and they
/// share a shape: the panel reported a confident, wrong answer — the service had ended, OBS was
/// fine, there was nothing left to do — at the moment the operator most needed a true one.
/// </summary>
public sealed class BroadcastReliabilityTests
{
    // ---------------------------------------------------------------- stopping

    /// <summary>
    /// ObsClient publishes Streaming = false whenever the websocket drops, so "not streaming" was
    /// indistinguishable from "cannot see OBS". Stop then skipped OBS entirely, completed the
    /// broadcast on YouTube and answered "the broadcast has ended" — while OBS carried on encoding
    /// and uploading to the ingest for the rest of the day.
    /// </summary>
    [Fact]
    public async Task Stopping_while_OBS_is_unreachable_does_not_claim_the_broadcast_ended()
    {
        using var host = new TestHost();
        host.SetToday();
        await host.Orchestrator.StartTodayAsync();

        host.Obs.Connected = false;

        var outcome = await host.Orchestrator.StopAsync();

        Assert.False(outcome.Ok);
        Assert.Contains("OBS", outcome.Message.En);

        // Nothing was told to YouTube either: the service really has not finished.
        Assert.Empty(host.YouTube.TransitionedIds);
        Assert.NotEqual(BroadcastStatus.Complete, host.State.Snapshot().Broadcast!.Status);
    }

    [Fact]
    public async Task Stopping_normally_still_stops_both_ends()
    {
        using var host = new TestHost();
        host.SetToday();
        await host.Orchestrator.StartTodayAsync();

        var outcome = await host.Orchestrator.StopAsync();

        Assert.True(outcome.Ok);
        Assert.Equal(1, host.Obs.StopStreamCalls);
        Assert.Equal(BroadcastStatus.Complete, host.State.Snapshot().Broadcast!.Status);
    }

    // ---------------------------------------------------------------- adopting a live broadcast

    /// <summary>
    /// Runtime state is memory-only by design, so a panel restart mid-service loses the broadcast.
    /// The phase then reads Ready, the operator taps start, and this adopts a broadcast that is
    /// already live. Recording it as "created" sent step 2 on to bind a live broadcast — which
    /// YouTube rejects, and every retry of that step failed identically, with no way forward that
    /// did not end the service.
    /// </summary>
    [Fact]
    public async Task Adopting_a_live_broadcast_does_not_try_to_bind_it_again()
    {
        using var host = new TestHost();
        host.SetToday();

        host.YouTube.Unfinished.Add(new BroadcastInfo(
            "already-live", "8/5/2026 Wednesday Service", "live",
            IYouTubeClient.WatchUrl("already-live")));

        var outcome = await host.Orchestrator.StartTodayAsync();

        Assert.True(outcome.Ok, outcome.Message.En);
        Assert.Equal("already-live", host.State.Snapshot().Broadcast!.Id);
        Assert.Equal(0, host.YouTube.BindCalls);
    }

    [Theory]
    [InlineData("live", BroadcastStatus.Live)]
    [InlineData("testing", BroadcastStatus.Testing)]
    [InlineData("ready", BroadcastStatus.Bound)]
    [InlineData("created", BroadcastStatus.Created)]
    public async Task An_adopted_broadcast_keeps_the_lifecycle_YouTube_reported(
        string lifeCycle, string expected)
    {
        using var host = new TestHost();
        host.SetToday();
        host.YouTube.Unfinished.Add(new BroadcastInfo(
            "leftover", "8/5/2026 Wednesday Service", lifeCycle, "url"));

        // Only step 1, so the assertion is about what adoption recorded and nothing else.
        await host.Orchestrator.StartTodayAsync();

        Assert.Equal("leftover", host.State.Snapshot().Broadcast!.Id);
        if (expected != BroadcastStatus.Created) Assert.NotEqual(BroadcastStatus.Created, host.State.Snapshot().Broadcast!.Status);
    }

    // ---------------------------------------------------------------- the checks

    /// <summary>
    /// A start that failed partway leaves a created-but-not-live broadcast, which keeps the phase on
    /// Ready and the checks on screen. The check announced "an earlier broadcast is still running"
    /// about this service's own — and the one-click fix it offered answered "there was nothing to
    /// end", because EndPreviousAsync already excluded it. The operator was left with a warning they
    /// could not clear.
    /// </summary>
    [Fact]
    public async Task The_checks_do_not_report_this_services_own_broadcast_as_a_leftover()
    {
        using var host = new TestHost();
        host.SetToday();
        await host.Orchestrator.StartTodayAsync();

        var mine = host.State.Snapshot().Broadcast!.Id!;
        host.YouTube.Unfinished.Add(new BroadcastInfo(mine, "8/5/2026 Wednesday Service", "live", "url"));

        var item = (await host.Preflight.RunAsync()).Single(i => i.Key == "previousBroadcast");

        Assert.True(item.Ok, item.Message.En);
    }

    [Fact]
    public async Task A_genuine_leftover_is_still_reported()
    {
        using var host = new TestHost();
        host.SetToday();
        await host.Orchestrator.StartTodayAsync();

        host.YouTube.Unfinished.Add(new BroadcastInfo("yesterday", "8/4/2026 Service", "live", "url"));

        var item = (await host.Preflight.RunAsync()).Single(i => i.Key == "previousBroadcast");

        Assert.False(item.Ok);
        Assert.Equal("end-previous", item.Action);
    }

    // ---------------------------------------------------------------- retrying

    /// <summary>
    /// A retry resumes at its step and never runs the ones before it, so rebuilding the step list
    /// left those "pending" for good. The progress card stays on screen for the whole Live phase,
    /// so a successful recovery left the operator watching grey dots for the rest of the service —
    /// on precisely the path where they most need to believe the display.
    /// </summary>
    [Fact]
    public async Task Retrying_a_step_keeps_what_the_earlier_steps_already_reported()
    {
        using var host = new TestHost();
        host.SetToday();

        host.Obs.FailOnce[nameof(host.Obs.StartStreamAsync)] = new InvalidOperationException("not yet");
        var first = await host.Orchestrator.StartTodayAsync();
        Assert.Equal(Orchestrator.StepStream, first.FailedStep);

        var retry = await host.Orchestrator.StartTodayAsync(first.FailedStep!.Value);
        Assert.True(retry.Ok, retry.Message.En);

        var steps = host.State.Snapshot().Steps;

        // Nothing is left looking like it never ran.
        Assert.DoesNotContain(steps, s => s.Status == "pending");
        Assert.All(steps, s => Assert.Contains(s.Status, new[] { "done", "skipped" }));
    }

    // ---------------------------------------------------------------- settings that must survive

    /// <summary>
    /// The same data-loss class SettingsPatch's own comment says was fixed, one nesting level down:
    /// the record's sections are nullable, but the objects inside them carry initialisers, so a PUT
    /// with a partial obs block used to reset the password, the scene names and the check lists to
    /// their defaults. The failure would be a panel that cannot reach OBS at 04:40 with nobody able
    /// to say why.
    /// </summary>
    [Fact]
    public void A_partial_obs_save_does_not_reset_the_rest_of_the_section()
    {
        using var host = new TestHost();
        host.Config.UpdateSettings(s =>
        {
            s.Obs.Password = "secret";
            s.Obs.SceneCamera = "摄像机";
            s.Obs.AudioInputName = "ProFX";
            s.Obs.VideoSourceNames = new List<string> { "主摄像机" };
        });

        // What a partial post looks like: only the URL carries a value.
        var patch = new ObsSettings
        {
            Url = "ws://localhost:4455",
            Password = "",
            SceneCamera = "",
            SceneSlides = "",
            AudioInputName = "",
            VideoSourceNames = new List<string>(),
        };

        ApplyObsPatch(host, patch);

        var obs = host.Config.Settings.Obs;
        Assert.Equal("secret", obs.Password);
        Assert.Equal("摄像机", obs.SceneCamera);
        Assert.Equal("ProFX", obs.AudioInputName);
        Assert.Equal(new[] { "主摄像机" }, obs.VideoSourceNames);
    }

    /// <summary>Mirrors what the settings endpoint does, so the rule is pinned without a TestServer.</summary>
    private static void ApplyObsPatch(TestHost host, ObsSettings obs) =>
        host.Config.UpdateSettings(current =>
        {
            if (!string.IsNullOrWhiteSpace(obs.Url)) current.Obs.Url = obs.Url;
            if (!string.IsNullOrWhiteSpace(obs.Password)) current.Obs.Password = obs.Password;
            if (!string.IsNullOrWhiteSpace(obs.SceneCamera)) current.Obs.SceneCamera = obs.SceneCamera;
            if (!string.IsNullOrWhiteSpace(obs.SceneSlides)) current.Obs.SceneSlides = obs.SceneSlides;
            if (!string.IsNullOrWhiteSpace(obs.AudioInputName)) current.Obs.AudioInputName = obs.AudioInputName;
            if (obs.VideoSourceNames.Count > 0) current.Obs.VideoSourceNames = obs.VideoSourceNames;
        });

    // ---------------------------------------------------------------- a wedged presentation program

    /// <summary>
    /// Every COM call into the presentation program is unbounded and blocking, so one that stops
    /// pumping used to block the whole background loop — which also carries the OBS status refresh
    /// and the authorization countdown. The bounded wait cannot un-block that thread, but it keeps
    /// the panel running and saying something true instead of freezing everything behind the one
    /// feature that is explicitly optional.
    /// </summary>
    [Fact]
    public void A_presentation_program_that_stops_answering_does_not_freeze_the_panel()
    {
        using var host = new TestHost();
        host.Slides.BlockGetState = true;

        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        host.State.RefreshSlides();
        stopwatch.Stop();

        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(10), $"took {stopwatch.Elapsed}");
        Assert.False(host.State.Snapshot().Slides.Available);
    }

    // ---------------------------------------------------------------- reconnecting to the ingest

    /// <summary>
    /// outputActive stays true while OBS is re-establishing the connection to YouTube, so the live
    /// card kept showing a running timer and a healthy bitrate while nothing at all was reaching
    /// viewers. It is the most broadcast-critical signal obs-websocket offers and it was not read.
    /// </summary>
    [Fact]
    public void OBS_reconnecting_to_the_ingest_reaches_the_pushed_state()
    {
        using var host = new TestHost();
        host.Obs.Streaming = true;
        host.Obs.Reconnecting = true;

        host.State.ApplyObsStatus(host.Obs.Status);

        var obs = host.State.Snapshot().Obs;

        Assert.True(obs.Reconnecting);
        // Everything else still reads perfectly healthy — which is the whole problem.
        Assert.True(obs.Streaming);
        Assert.True(obs.KbitsPerSec > 0);
    }
}
