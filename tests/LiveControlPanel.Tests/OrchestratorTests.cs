using LiveControlPanel.Core;
using LiveControlPanel.Obs;
using LiveControlPanel.Youtube;
using Xunit;

namespace LiveControlPanel.Tests;

/// <summary>
/// Covers the plan's T-05, T-06, T-03 and T-26 cases: idempotency under repeated taps, resumable
/// failure, two services in one day, and the confirmed stop path.
/// </summary>
public class OrchestratorTests
{
    [Fact]
    public async Task Start_runs_all_six_steps_and_reports_success()
    {
        using var host = new TestHost();
        host.WithThumbnail();
        host.SetToday();
        // Start away from the camera scene so step 4 actually has work to do.
        host.Obs.CurrentScene = "PPT";

        var outcome = await host.Orchestrator.StartTodayAsync();

        Assert.True(outcome.Ok);
        Assert.Null(outcome.FailedStep);
        Assert.Equal(1, host.YouTube.CreateCalls);
        Assert.Equal(1, host.YouTube.BindCalls);
        Assert.Equal(1, host.YouTube.ThumbnailCalls);
        Assert.Equal(new[] { "摄像机" }, host.Obs.ScenesSet);
        Assert.Equal(1, host.Obs.StartStreamCalls);

        var state = host.State.Snapshot();
        Assert.Equal(Phase.Live, state.Phase);
        Assert.Equal(BroadcastStatus.Live, state.Broadcast!.Status);
        Assert.Equal("https://www.youtube.com/live/bcast1", state.Broadcast.WatchUrl);
        Assert.All(state.Steps, step => Assert.Contains(step.Status, new[] { "done", "skipped" }));
    }

    [Fact]
    public async Task Watch_url_has_no_feature_share_suffix()
    {
        using var host = new TestHost();
        host.SetToday();

        await host.Orchestrator.StartTodayAsync();

        var url = host.State.Snapshot().Broadcast!.WatchUrl!;
        Assert.StartsWith("https://www.youtube.com/live/", url);
        Assert.DoesNotContain("feature", url);
        Assert.DoesNotContain("?", url);
    }

    [Fact]
    public async Task Broadcast_is_created_with_the_parameters_the_requirements_mandate()
    {
        using var host = new TestHost();
        host.SetToday();

        await host.Orchestrator.StartTodayAsync();

        var request = host.YouTube.LastCreateRequest!;
        Assert.Equal("8/5/2026 Wednesday Service", request.Title);
        Assert.Equal("unlisted", request.PrivacyStatus);
        Assert.False(request.MadeForKids);
        Assert.Equal("ultraLow", request.LatencyPreference);
        Assert.Equal("God Bless You!", request.Description);
    }

    /// <summary>
    /// Operators run early or late, so YouTube is told when the stream really started rather than the
    /// service's announced time — otherwise the watch page advertises a time that already passed.
    /// </summary>
    [Fact]
    public async Task Youtube_is_given_the_real_start_moment_not_the_announced_time()
    {
        using var host = new TestHost();
        // Nominal start is 18:00 on 2026-08-05, which is neither now nor even this year.
        host.SetToday();

        var before = DateTime.Now;
        await host.Orchestrator.StartTodayAsync();
        var after = DateTime.Now;

        var reported = host.YouTube.LastCreateRequest!.ScheduledStart;

        Assert.InRange(reported, before.AddSeconds(-1), after.AddSeconds(1));
        Assert.NotEqual(new DateTime(2026, 8, 5, 18, 0, 0), reported);
    }

    [Fact]
    public async Task The_announced_time_is_still_what_the_panel_displays()
    {
        using var host = new TestHost();
        host.SetToday();

        await host.Orchestrator.StartTodayAsync();

        // The operator sees "预定开始 18:00" regardless of when they actually pressed start.
        Assert.Equal(new DateTime(2026, 8, 5, 18, 0, 0), host.State.Snapshot().Today!.ScheduledStart);
    }

    // ---------------------------------------------------------------- T-05 idempotency

    [Fact]
    public async Task Five_sequential_taps_create_one_broadcast_and_start_one_stream()
    {
        using var host = new TestHost();
        host.WithThumbnail();
        host.SetToday();

        for (var i = 0; i < 5; i++)
        {
            var outcome = await host.Orchestrator.StartTodayAsync();
            Assert.True(outcome.Ok);
        }

        Assert.Equal(1, host.YouTube.CreateCalls);
        Assert.Equal(1, host.YouTube.BindCalls);
        Assert.Equal(1, host.YouTube.ThumbnailCalls);
        Assert.Equal(1, host.Obs.StartStreamCalls);
    }

    [Fact]
    public async Task Five_concurrent_taps_create_one_broadcast_and_start_one_stream()
    {
        using var host = new TestHost();
        host.SetToday();

        var results = await Task.WhenAll(Enumerable.Range(0, 5)
            .Select(_ => host.Orchestrator.StartTodayAsync()));

        Assert.All(results, outcome => Assert.True(outcome.Ok));
        Assert.Equal(1, host.YouTube.CreateCalls);
        Assert.Equal(1, host.Obs.StartStreamCalls);
    }

    // ---------------------------------------------------------------- T-06 resumable failure

    [Fact]
    public async Task A_failure_reports_its_step_and_leaves_earlier_work_intact()
    {
        using var host = new TestHost();
        host.SetToday();
        host.Obs.FailOnce[nameof(FakeObsClient.StartStreamAsync)] = new ObsUnavailableException();

        var outcome = await host.Orchestrator.StartTodayAsync();

        Assert.False(outcome.Ok);
        Assert.Equal(Orchestrator.StepStream, outcome.FailedStep);
        Assert.Contains("OBS", outcome.Message.Zh);

        // The broadcast exists and is bound; only the stream failed.
        Assert.Equal(1, host.YouTube.CreateCalls);
        Assert.Equal(1, host.YouTube.BindCalls);
        Assert.Equal(0, host.Obs.StartStreamCalls);

        var failed = host.State.Snapshot().Steps.Single(s => s.Status == "failed");
        Assert.Equal(Orchestrator.StepStream, failed.Step);
    }

    [Fact]
    public async Task Retrying_from_the_failed_step_does_not_create_a_second_broadcast()
    {
        using var host = new TestHost();
        host.SetToday();
        host.Obs.FailOnce[nameof(FakeObsClient.StartStreamAsync)] = new ObsUnavailableException();

        var first = await host.Orchestrator.StartTodayAsync();
        Assert.Equal(Orchestrator.StepStream, first.FailedStep);

        var retry = await host.Orchestrator.StartTodayAsync(first.FailedStep!.Value);

        Assert.True(retry.Ok);
        Assert.Equal(1, host.YouTube.CreateCalls);
        Assert.Equal(1, host.YouTube.BindCalls);
        Assert.Equal(1, host.Obs.StartStreamCalls);
    }

    [Fact]
    public async Task Restarting_from_step_one_after_a_failure_still_creates_only_one_broadcast()
    {
        // Even the wrong recovery move must not double-book: step 1 is guarded by existing state.
        using var host = new TestHost();
        host.SetToday();
        host.Obs.FailOnce[nameof(FakeObsClient.StartStreamAsync)] = new ObsUnavailableException();

        await host.Orchestrator.StartTodayAsync();
        var retry = await host.Orchestrator.StartTodayAsync(Orchestrator.StepCreate);

        Assert.True(retry.Ok);
        Assert.Equal(1, host.YouTube.CreateCalls);
    }

    [Fact]
    public async Task A_failure_message_never_leaks_technical_detail()
    {
        using var host = new TestHost();
        host.SetToday();
        host.YouTube.FailOnce[nameof(FakeYouTubeClient.CreateBroadcastAsync)] = new NotAuthorizedException();

        var outcome = await host.Orchestrator.StartTodayAsync();

        Assert.False(outcome.Ok);
        Assert.Equal(Orchestrator.StepCreate, outcome.FailedStep);
        Assert.DoesNotContain("Exception", outcome.Message.Zh);
        Assert.DoesNotContain("403", outcome.Message.Zh);
        Assert.Contains("重新授权", outcome.Message.Zh);
    }

    // ---------------------------------------------------------------- step guards

    [Fact]
    public async Task Start_refuses_when_nothing_is_scheduled()
    {
        using var host = new TestHost();

        var outcome = await host.Orchestrator.StartTodayAsync();

        Assert.False(outcome.Ok);
        Assert.Equal(0, host.YouTube.CreateCalls);
        Assert.Contains("没有排期", outcome.Message.Zh);
    }

    [Fact]
    public async Task Bind_fails_with_an_actionable_message_when_no_stream_key_exists()
    {
        using var host = new TestHost();
        host.Config.UpdateSettings(s => s.StreamId = "");
        host.SetToday();

        var outcome = await host.Orchestrator.StartTodayAsync();

        Assert.False(outcome.Ok);
        Assert.Equal(Orchestrator.StepBind, outcome.FailedStep);
        Assert.Contains("推流密钥", outcome.Message.Zh);
    }

    [Fact]
    public async Task A_missing_thumbnail_file_is_skipped_rather_than_blocking_the_stream()
    {
        using var host = new TestHost();
        host.Config.UpdateSettings(s => s.DefaultThumbnail = "thumbnails/not-there.jpg");
        host.SetToday();

        var outcome = await host.Orchestrator.StartTodayAsync();

        Assert.True(outcome.Ok);
        Assert.Equal(0, host.YouTube.ThumbnailCalls);

        var step = host.State.Snapshot().Steps.Single(s => s.Step == Orchestrator.StepThumbnail);
        Assert.Equal("skipped", step.Status);
    }

    [Fact]
    public async Task Scene_switching_is_skipped_when_obs_is_already_on_the_starting_scene()
    {
        using var host = new TestHost();
        host.Obs.CurrentScene = "摄像机";
        host.SetToday();

        await host.Orchestrator.StartTodayAsync();

        Assert.Empty(host.Obs.ScenesSet);
    }

    [Fact]
    public async Task Stream_start_is_skipped_when_obs_is_already_streaming()
    {
        using var host = new TestHost();
        host.Obs.Streaming = true;
        host.SetToday();

        await host.Orchestrator.StartTodayAsync();

        Assert.Equal(0, host.Obs.StartStreamCalls);
    }

    [Fact]
    public async Task Await_live_times_out_with_an_actionable_message_when_youtube_never_goes_live()
    {
        using var host = new TestHost();
        host.SetToday();
        host.YouTube.LifeCycleStatus = "ready";

        // Kept short: the production timeout is 60s and the test only needs the failure shape.
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));

        var outcome = await host.Orchestrator.StartTodayAsync(ct: cts.Token);

        Assert.False(outcome.Ok);
        Assert.Equal(Orchestrator.StepAwaitLive, outcome.FailedStep);
    }

    // ---------------------------------------------------------------- T-26 stop

    [Fact]
    public async Task Stop_stops_obs_first_then_completes_the_broadcast()
    {
        using var host = new TestHost();
        host.SetToday();
        await host.Orchestrator.StartTodayAsync();

        var outcome = await host.Orchestrator.StopAsync();

        Assert.True(outcome.Ok);
        Assert.Equal(1, host.Obs.StopStreamCalls);
        Assert.Equal(1, host.YouTube.TransitionCalls);
        Assert.False(host.Obs.Streaming);

        var state = host.State.Snapshot();
        Assert.Equal(Phase.Ended, state.Phase);
        Assert.Equal(BroadcastStatus.Complete, state.Broadcast!.Status);
    }

    [Fact]
    public async Task Stop_tells_the_operator_to_use_obs_directly_when_obs_refuses()
    {
        using var host = new TestHost();
        host.SetToday();
        await host.Orchestrator.StartTodayAsync();
        host.Obs.FailOnce[nameof(FakeObsClient.StopStreamAsync)] = new ObsUnavailableException();

        var outcome = await host.Orchestrator.StopAsync();

        Assert.False(outcome.Ok);
        Assert.Contains("OBS", outcome.Message.Zh);
        Assert.Equal(0, host.YouTube.TransitionCalls);
    }

    [Fact]
    public async Task Stop_says_so_plainly_when_youtube_will_not_confirm_the_end()
    {
        using var host = new TestHost();
        host.SetToday();
        await host.Orchestrator.StartTodayAsync();
        host.YouTube.FailOnce[nameof(FakeYouTubeClient.TransitionToCompleteAsync)] =
            new HttpRequestException("network down");

        var outcome = await host.Orchestrator.StopAsync();

        Assert.False(outcome.Ok);
        Assert.Contains("推流已停止", outcome.Message.Zh);

        // The stream really has stopped, so the panel must not still claim to be live.
        Assert.Equal(Phase.Ended, host.State.Snapshot().Phase);
    }

    // ---------------------------------------------------------------- T-04 leftover broadcast

    /// <summary>
    /// Only a broadcast that went on air can transition to complete; YouTube rejects the same call on
    /// a created/ready one as invalidTransition — yet those still hold the shared stream key, so the
    /// one-click fix must delete them instead of erroring out on the highest-risk pre-flight path.
    /// </summary>
    [Fact]
    public async Task End_previous_ends_live_broadcasts_and_deletes_never_started_ones()
    {
        using var host = new TestHost();
        host.YouTube.Unfinished = new List<BroadcastInfo>
        {
            new("old1", "8/5/2026 Morning Service", "live", "https://www.youtube.com/live/old1"),
            new("old2", "8/4/2026 Morning Service", "ready", "https://www.youtube.com/live/old2"),
            new("old3", "8/3/2026 Morning Service", "created", "https://www.youtube.com/live/old3"),
        };

        var outcome = await host.Orchestrator.EndPreviousAsync();

        Assert.True(outcome.Ok);
        Assert.Equal(new[] { "old1" }, host.YouTube.TransitionedIds);
        Assert.Equal(new[] { "old2", "old3" }, host.YouTube.DeletedIds);
    }

    /// <summary>
    /// A channel with years of leftovers can have entries in the unfinished list that are already
    /// gone by the time they are deleted (removed in Studio, or stale in the listing). YouTube
    /// answers 404 for those; aborting the whole batch on it left every broadcast after the dead one
    /// uncleaned, and re-clicking just hit the next 404 — the cleanup could never finish.
    /// </summary>
    [Fact]
    public async Task End_previous_skips_broadcasts_already_gone_instead_of_aborting_the_batch()
    {
        using var host = new TestHost();
        host.YouTube.Unfinished = new List<BroadcastInfo>
        {
            new("gone1", "0729 Morning Service", "created", "https://www.youtube.com/live/gone1"),
            new("old2", "8/4/2026 Morning Service", "ready", "https://www.youtube.com/live/old2"),
            new("old3", "8/5/2026 Morning Service", "live", "https://www.youtube.com/live/old3"),
        };
        host.YouTube.FailOnce[nameof(FakeYouTubeClient.DeleteBroadcastAsync)] =
            new Google.GoogleApiException("youtube", "Live broadcast not found")
            {
                HttpStatusCode = System.Net.HttpStatusCode.NotFound,
            };

        var outcome = await host.Orchestrator.EndPreviousAsync();

        Assert.True(outcome.Ok);
        Assert.Equal(new[] { "old2" }, host.YouTube.DeletedIds);
        Assert.Equal(new[] { "old3" }, host.YouTube.TransitionedIds);
        Assert.Contains("2", outcome.Message.Zh);
    }

    /// <summary>
    /// An insert can succeed server-side while the response is lost to a timeout. The retry must
    /// adopt the broadcast that already exists under today's title instead of creating a second
    /// one — duplicate broadcasts pile up as "leftovers" and scare the next operator's pre-flight.
    /// </summary>
    [Fact]
    public async Task Retrying_a_lost_create_adopts_the_existing_broadcast_instead_of_duplicating()
    {
        using var host = new TestHost();
        host.SetToday();
        host.YouTube.FailOnce[nameof(FakeYouTubeClient.CreateBroadcastAsync)] = new TaskCanceledException();

        var first = await host.Orchestrator.StartTodayAsync();
        Assert.False(first.Ok);
        Assert.Equal(Orchestrator.StepCreate, first.FailedStep);

        // The "lost" create actually landed on YouTube's side.
        host.YouTube.Unfinished = new List<BroadcastInfo>
        {
            new("lost1", "8/5/2026 Wednesday Service", "created", "https://www.youtube.com/live/lost1"),
        };

        var second = await host.Orchestrator.StartTodayAsync();

        Assert.True(second.Ok);
        Assert.Equal(0, host.YouTube.CreateCalls);
        Assert.Equal("lost1", host.State.Read(s => s.Broadcast!.Id));
    }

    /// <summary>
    /// The broadcast can end while step 6 waits — stopped from this panel in a race, or in YouTube
    /// Studio. Polling a finished broadcast for the remaining minute produced a "retry" that could
    /// never succeed; it must fail immediately with the real explanation.
    /// </summary>
    [Fact]
    public async Task Await_live_fails_immediately_when_the_broadcast_already_ended()
    {
        using var host = new TestHost();
        host.SetToday();
        host.YouTube.LifeCycleStatus = "complete";

        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        var outcome = await host.Orchestrator.StartTodayAsync();
        stopwatch.Stop();

        Assert.False(outcome.Ok);
        Assert.Equal(Orchestrator.StepAwaitLive, outcome.FailedStep);
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(30), "must not sit out the 60s poll");
        Assert.Contains("已经结束", outcome.Message.Zh);
        Assert.Equal(Phase.Ended, host.State.Snapshot().Phase);
    }

    [Fact]
    public async Task End_previous_never_ends_the_broadcast_this_session_created()
    {
        using var host = new TestHost();
        host.SetToday();
        await host.Orchestrator.StartTodayAsync();

        var current = host.State.Snapshot().Broadcast!.Id!;
        host.YouTube.Unfinished = new List<BroadcastInfo>
        {
            new(current, "today", "live", "url"),
            new("old1", "yesterday", "live", "url"),
        };

        await host.Orchestrator.EndPreviousAsync();

        Assert.Equal(new[] { "old1" }, host.YouTube.TransitionedIds);
    }

    // ---------------------------------------------------------------- T-03 two services in one day

    [Fact]
    public async Task Start_another_clears_state_so_a_second_service_can_run_the_same_day()
    {
        using var host = new TestHost();
        host.SetToday("8/5/2026 Morning Service", "morning-service");
        await host.Orchestrator.StartTodayAsync();
        await host.Notifications.SendCurrentAsync();
        await host.Orchestrator.StopAsync();

        Assert.True(host.Orchestrator.StartAnother());

        var state = host.State.Snapshot();
        Assert.Null(state.Broadcast);
        Assert.Null(state.Telegram.SentAt);
        Assert.Empty(state.Steps);

        host.SetToday("8/5/2026 Wednesday Service", "wednesday-service");
        var second = await host.Orchestrator.StartTodayAsync();

        Assert.True(second.Ok);
        Assert.Equal(2, host.YouTube.CreateCalls);
        Assert.Equal("8/5/2026 Wednesday Service", host.YouTube.LastCreateRequest!.Title);
    }

    [Fact]
    public async Task Start_another_is_refused_while_the_current_service_is_still_live()
    {
        using var host = new TestHost();
        host.SetToday();
        await host.Orchestrator.StartTodayAsync();

        Assert.False(host.Orchestrator.StartAnother());
        Assert.NotNull(host.State.Snapshot().Broadcast);
    }
}
