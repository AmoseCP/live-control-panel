using LiveControlPanel.Core;
using Xunit;

namespace LiveControlPanel.Tests;

/// <summary>
/// FR 6.1's phase machine. The rule under test throughout: the UI must never be able to show an
/// action that makes no sense — above all, no "stop streaming" when nothing is live (T-21).
/// </summary>
public class StateManagerTests
{
    [Fact]
    public void Phase_is_ready_when_a_service_matches_and_nothing_has_started()
    {
        using var host = new TestHost();
        host.SetToday();

        var state = host.State.Snapshot();

        Assert.Equal(Phase.Ready, state.Phase);
        Assert.Null(state.Broadcast);
    }

    [Fact]
    public async Task Phase_becomes_live_once_the_broadcast_is_live()
    {
        using var host = new TestHost();
        host.SetToday();

        await host.Orchestrator.StartTodayAsync();

        Assert.Equal(Phase.Live, host.State.Snapshot().Phase);
    }

    [Fact]
    public async Task Phase_becomes_live_when_obs_is_streaming_even_before_youtube_confirms()
    {
        using var host = new TestHost();
        host.SetToday();
        host.YouTube.LifeCycleStatus = "ready";
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));

        await host.Orchestrator.StartTodayAsync(ct: cts.Token);

        // Step 6 timed out, but OBS is sending. Push the OBS status the way the background service
        // does when StreamStateChanged arrives.
        Assert.True(host.Obs.Streaming);
        host.State.ApplyObsStatus(host.Obs.Status);

        Assert.Equal(Phase.Live, host.State.Snapshot().Phase);
    }

    [Fact]
    public async Task Phase_becomes_ended_after_stopping()
    {
        using var host = new TestHost();
        host.SetToday();
        await host.Orchestrator.StartTodayAsync();

        await host.Orchestrator.StopAsync();

        Assert.Equal(Phase.Ended, host.State.Snapshot().Phase);
    }

    [Fact]
    public void Phase_is_noschedule_with_a_next_service_when_nothing_matches()
    {
        using var host = new TestHost();
        // No SetToday call and the seeded schedule only matches inside its windows, so on most
        // wall-clock times this is NoSchedule; force the shape by clearing any incidental match.
        host.Config.SaveTemplates(host.Config.Templates
            .Select(t =>
            {
                var clone = t.Clone();
                // Move every service to a weekday that is not today.
                clone.Weekdays = new List<int> { ((int)DateTime.Now.DayOfWeek + 3) % 7 };
                return clone;
            })
            .ToList());

        var state = host.State.Snapshot();

        Assert.Equal(Phase.NoSchedule, state.Phase);
        Assert.Null(state.Today);
        Assert.NotNull(state.NextService);
        Assert.NotNull(state.NextService!.StartsAt);
    }

    /// <summary>
    /// The next service carries its template id so the "not time yet" screen can put the operator
    /// into Ready in one tap rather than sending them through the picker.
    /// </summary>
    [Fact]
    public void Next_service_reports_which_template_it_is()
    {
        using var host = new TestHost();
        host.Config.SaveTemplates(host.Config.Templates
            .Select(t =>
            {
                var clone = t.Clone();
                if (clone.Weekdays.Count > 0) clone.Weekdays = new List<int> { ((int)DateTime.Now.DayOfWeek + 3) % 7 };
                return clone;
            })
            .ToList());

        var next = host.State.Snapshot().NextService;

        Assert.NotNull(next);
        Assert.False(string.IsNullOrWhiteSpace(next!.TemplateId));
        Assert.NotNull(host.Config.FindTemplate(next.TemplateId!));
    }

    [Fact]
    public void Next_service_is_cleared_once_a_service_matches()
    {
        using var host = new TestHost();
        host.SetToday();

        Assert.Null(host.State.Snapshot().NextService);
    }

    [Fact]
    public async Task A_manually_chosen_service_survives_a_state_refresh()
    {
        using var host = new TestHost();
        host.SetToday("8/5/2026 特别聚会", "custom");

        await host.Orchestrator.StartTodayAsync();

        // Recomputing the schedule must not replace a title the operator picked on purpose.
        var state = host.State.Snapshot();
        Assert.Equal("8/5/2026 特别聚会", state.Today!.Title);
        Assert.Equal("8/5/2026 特别聚会", state.Broadcast!.Title);
    }

    [Fact]
    public void Obs_status_is_reflected_into_state()
    {
        using var host = new TestHost();
        host.Obs.Streaming = true;
        host.Obs.CurrentScene = "PPT";

        host.State.ApplyObsStatus(host.Obs.Status);

        var state = host.State.Snapshot();
        Assert.True(state.Obs.Connected);
        Assert.True(state.Obs.Streaming);
        Assert.Equal("PPT", state.Obs.CurrentScene);
        Assert.Equal(new[] { "摄像机", "PPT" }, state.Obs.Scenes.ToArray());
    }

    [Fact]
    public void Slide_state_is_refreshed_from_the_controller()
    {
        using var host = new TestHost();

        host.State.RefreshSlides();

        var slides = host.State.Snapshot().Slides;
        Assert.True(slides.Available);
        Assert.Equal(7, slides.Current);
        Assert.Equal(24, slides.Total);
    }

    [Fact]
    public void Last_action_records_time_and_service_for_cross_day_troubleshooting()
    {
        using var host = new TestHost();

        host.State.RecordAction("开始直播", "8/5/2026 Wednesday Service");

        var action = host.State.Snapshot().LastAction;
        Assert.NotNull(action);
        Assert.Equal("开始直播", action!.What);
        Assert.Equal("8/5/2026 Wednesday Service", action.Service);
        Assert.True((DateTime.Now - action.At).Duration() < TimeSpan.FromMinutes(1));
    }

    [Fact]
    public void Snapshots_are_deep_copies_so_callers_cannot_mutate_live_state()
    {
        using var host = new TestHost();
        host.SetToday();

        var snapshot = host.State.Snapshot();
        snapshot.Today!.Title = "tampered";
        snapshot.Obs.Scenes.Add("injected");

        var fresh = host.State.Snapshot();
        Assert.Equal("8/5/2026 Wednesday Service", fresh.Today!.Title);
        Assert.DoesNotContain("injected", fresh.Obs.Scenes);
    }

    [Fact]
    public void Concurrent_mutations_do_not_corrupt_state()
    {
        using var host = new TestHost();

        Parallel.For(0, 200, i => host.State.RecordAction($"action-{i}"));

        Assert.NotNull(host.State.Snapshot().LastAction);
    }
}
