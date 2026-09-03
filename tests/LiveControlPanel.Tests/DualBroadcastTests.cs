using LiveControlPanel.Core;
using LiveControlPanel.Youtube;
using Xunit;

namespace LiveControlPanel.Tests;

/// <summary>
/// One camera feed, two broadcasts.
///
/// Nearly every assertion here is a variation on one rule: the translated broadcast is subordinate.
/// It is created, bound and ended alongside the primary one, and any way it can go wrong must end in
/// a warning on a service that is still on air — never in a failed step, never in a stream the
/// congregation loses.
/// </summary>
public sealed class DualBroadcastTests
{
    private const string Title = "8/5/2026 Wednesday Service";
    private const string TranslatedTitle = Title + " (English)";

    private static StepState Step(TestHost host, int step) =>
        host.State.Snapshot().Steps.Single(s => s.Step == step);

    // ---------------------------------------------------------------- off by default

    [Fact]
    public async Task With_translation_off_nothing_changes()
    {
        using var host = new TestHost();
        host.SetToday();

        var outcome = await host.Orchestrator.StartTodayAsync();

        Assert.True(outcome.Ok);
        Assert.Equal(1, host.YouTube.CreateCalls);
        Assert.Null(host.State.Snapshot().Translated);
        Assert.Empty(host.Translation.StartedLanguages);
        Assert.Equal("skipped", Step(host, Orchestrator.StepTranslate).Status);
    }

    [Fact]
    public void With_translation_off_the_state_says_so()
    {
        using var host = new TestHost();
        host.SetToday();

        Assert.False(host.State.Snapshot().Translation.Enabled);
        Assert.Null(host.State.Snapshot().Translation.TargetLanguage);
    }

    /// <summary>
    /// The panel says "bilingual" before anything starts, so an operator on Ready knows what this
    /// service will produce. The plan is recomputed on every state push, not only during a run.
    /// </summary>
    [Fact]
    public void With_translation_on_the_state_says_so_before_anything_starts()
    {
        using var host = new TestHost();
        host.EnableTranslation();
        host.SetToday();

        var translation = host.State.Snapshot().Translation;

        Assert.True(translation.Enabled);
        Assert.Equal("en", translation.TargetLanguage);
        Assert.False(translation.Running);
    }

    /// <summary>A service that opted out shows nothing about translation, even with the switch on.</summary>
    [Fact]
    public void A_service_that_opted_out_reports_translation_off()
    {
        using var host = new TestHost();
        host.EnableTranslation();

        var templates = host.Config.Templates.Select(t => t.Clone()).ToList();
        var target = templates.First(t => t.Id == "wednesday-service");
        target.Translate = false;
        host.Config.SaveTemplates(templates);

        host.SetToday();

        Assert.False(host.State.Snapshot().Translation.Enabled);
    }

    // ---------------------------------------------------------------- the happy path

    [Fact]
    public async Task Two_broadcasts_are_created_and_the_second_carries_the_suffix()
    {
        using var host = new TestHost();
        host.EnableTranslation();
        host.SetToday();

        var outcome = await host.Orchestrator.StartTodayAsync();

        Assert.True(outcome.Ok);
        Assert.Equal(new[] { Title, TranslatedTitle }, host.YouTube.CreatedTitles);

        var state = host.State.Snapshot();
        Assert.Equal(Title, state.Broadcast!.Title);
        Assert.Equal(TranslatedTitle, state.Translated!.Title);
        Assert.NotEqual(state.Broadcast.Id, state.Translated.Id);
    }

    /// <summary>
    /// Each broadcast gets its own key. Not a preference: one liveStream can only back one live
    /// broadcast at a time, so sharing would make the second bind fail every single service.
    /// </summary>
    [Fact]
    public async Task Each_broadcast_is_bound_to_its_own_stream_key()
    {
        using var host = new TestHost();
        host.EnableTranslation();
        host.SetToday();

        await host.Orchestrator.StartTodayAsync();

        var state = host.State.Snapshot();

        Assert.Contains((state.Broadcast!.Id!, "stream-1"), host.YouTube.Bindings);
        Assert.Contains((state.Translated!.Id!, "stream-2"), host.YouTube.Bindings);
        Assert.Equal(2, host.YouTube.Bindings.Count);
    }

    [Fact]
    public async Task Both_broadcasts_get_the_thumbnail()
    {
        using var host = new TestHost();
        host.EnableTranslation();
        host.WithThumbnail();
        host.SetToday();

        await host.Orchestrator.StartTodayAsync();

        var state = host.State.Snapshot();

        Assert.Equal(2, host.YouTube.ThumbnailIds.Count);
        Assert.Contains(state.Broadcast!.Id!, host.YouTube.ThumbnailIds);
        Assert.Contains(state.Translated!.Id!, host.YouTube.ThumbnailIds);
    }

    [Fact]
    public async Task The_translator_is_started_before_OBS_begins_sending()
    {
        using var host = new TestHost();
        host.EnableTranslation();
        host.SetToday();

        await host.Orchestrator.StartTodayAsync();

        Assert.Equal(new[] { "en" }, host.Translation.StartedLanguages);
        Assert.True(Orchestrator.StepTranslate < Orchestrator.StepStream);
    }

    [Fact]
    public async Task A_service_can_translate_into_chinese_instead()
    {
        using var host = new TestHost();
        host.EnableTranslation(targetLanguage: "zh-CN", titleSuffix: "（中文）");
        host.SetToday();

        await host.Orchestrator.StartTodayAsync();

        Assert.Equal(new[] { "zh-CN" }, host.Translation.StartedLanguages);
        Assert.Equal(Title + "（中文）", host.State.Snapshot().Translated!.Title);
    }

    /// <summary>
    /// The panel still starts exactly one OBS output. The second RTMP target is carried by the
    /// plugin's sync-start, off the same video encoder — there is deliberately no second output here
    /// to start, to get out of step, or to leave running.
    /// </summary>
    [Fact]
    public async Task Only_one_OBS_stream_is_ever_started()
    {
        using var host = new TestHost();
        host.EnableTranslation();
        host.SetToday();

        await host.Orchestrator.StartTodayAsync();

        Assert.Equal(1, host.Obs.StartStreamCalls);
    }

    [Fact]
    public async Task Both_broadcasts_reaching_live_is_reported_as_such()
    {
        using var host = new TestHost();
        host.EnableTranslation();
        host.SetToday();

        await host.Orchestrator.StartTodayAsync();

        var state = host.State.Snapshot();

        Assert.Equal(BroadcastStatus.Live, state.Broadcast!.Status);
        Assert.Equal(BroadcastStatus.Live, state.Translated!.Status);
        Assert.Equal("done", Step(host, Orchestrator.StepAwaitLive).Status);
    }

    // ---------------------------------------------------------------- idempotency

    [Fact]
    public async Task Running_twice_still_produces_exactly_two_broadcasts()
    {
        using var host = new TestHost();
        host.EnableTranslation();
        host.SetToday();

        await host.Orchestrator.StartTodayAsync();
        await host.Orchestrator.StartTodayAsync();

        Assert.Equal(2, host.YouTube.CreateCalls);
        Assert.Equal(2, host.YouTube.Bindings.Count);
    }

    /// <summary>
    /// A create whose response was lost leaves a broadcast on YouTube and nothing locally. Adoption
    /// matches on exact title, and the suffix is what keeps the two slots from adopting each other.
    /// </summary>
    [Fact]
    public async Task A_lost_translated_broadcast_is_adopted_into_the_right_slot()
    {
        using var host = new TestHost();
        host.EnableTranslation();
        host.SetToday();

        host.YouTube.Unfinished.Add(
            new BroadcastInfo("lost-translated", TranslatedTitle, "ready",
                IYouTubeClient.WatchUrl("lost-translated")));

        await host.Orchestrator.StartTodayAsync();

        var state = host.State.Snapshot();

        Assert.Equal("lost-translated", state.Translated!.Id);
        Assert.NotEqual("lost-translated", state.Broadcast!.Id);
        // Only the primary one had to be inserted.
        Assert.Equal(1, host.YouTube.CreateCalls);
    }

    // ---------------------------------------------------------------- the translated side failing

    [Fact]
    public async Task Translation_on_without_a_second_stream_key_warns_and_goes_live_anyway()
    {
        using var host = new TestHost();
        host.EnableTranslation();
        host.Config.UpdateSettings(s => s.Translation.StreamId = "");
        host.SetToday();

        var outcome = await host.Orchestrator.StartTodayAsync();

        Assert.True(outcome.Ok);
        Assert.Null(outcome.FailedStep);
        Assert.Equal("warn", Step(host, Orchestrator.StepCreate).Status);
        Assert.Equal(1, host.YouTube.CreateCalls);
        Assert.Null(host.State.Snapshot().Translated);
        Assert.Equal(BroadcastStatus.Live, host.State.Snapshot().Broadcast!.Status);
    }

    [Fact]
    public async Task A_failed_translated_create_leaves_the_primary_broadcast_alone()
    {
        using var host = new TestHost();
        host.EnableTranslation();
        host.SetToday();

        host.YouTube.CreateFailure = request =>
            request.Title == TranslatedTitle ? new InvalidOperationException("boom") : null;

        var outcome = await host.Orchestrator.StartTodayAsync();

        Assert.True(outcome.Ok);
        Assert.Equal("warn", Step(host, Orchestrator.StepCreate).Status);

        var state = host.State.Snapshot();
        Assert.Null(state.Translated);
        Assert.Equal(BroadcastStatus.Live, state.Broadcast!.Status);
        Assert.Equal(1, host.Obs.StartStreamCalls);
    }

    [Fact]
    public async Task A_failed_translated_bind_warns_and_the_service_still_goes_live()
    {
        using var host = new TestHost();
        host.EnableTranslation();
        host.SetToday();

        // bcast2 is the translated one — the primary is created first.
        host.YouTube.BindFailure = id => id == "bcast2" ? new InvalidOperationException("nope") : null;

        var outcome = await host.Orchestrator.StartTodayAsync();

        Assert.True(outcome.Ok);
        Assert.Equal("warn", Step(host, Orchestrator.StepBind).Status);
        Assert.Equal(BroadcastStatus.Live, host.State.Snapshot().Broadcast!.Status);
    }

    [Fact]
    public async Task A_failed_translated_thumbnail_is_only_a_warning()
    {
        using var host = new TestHost();
        host.EnableTranslation();
        host.WithThumbnail();
        host.SetToday();

        host.YouTube.ThumbnailFailure = id => id == "bcast2" ? new InvalidOperationException("nope") : null;

        var outcome = await host.Orchestrator.StartTodayAsync();

        Assert.True(outcome.Ok);
        Assert.Equal("warn", Step(host, Orchestrator.StepThumbnail).Status);
        Assert.True(host.State.Snapshot().Broadcast!.ThumbnailUploaded);
    }

    /// <summary>
    /// The single most important assertion in this file: a translator that will not start does not
    /// keep a service off the air.
    /// </summary>
    [Fact]
    public async Task A_translator_that_refuses_to_start_warns_and_the_service_goes_live()
    {
        using var host = new TestHost();
        host.EnableTranslation();
        host.SetToday();

        host.Translation.Problem = new Msg("连不上翻译服务。", "Cannot reach the translation service.");

        var outcome = await host.Orchestrator.StartTodayAsync();

        Assert.True(outcome.Ok);
        Assert.Null(outcome.FailedStep);
        Assert.Equal("warn", Step(host, Orchestrator.StepTranslate).Status);
        Assert.Equal(1, host.Obs.StartStreamCalls);
        Assert.Equal(BroadcastStatus.Live, host.State.Snapshot().Broadcast!.Status);
    }

    [Fact]
    public async Task A_translator_that_throws_warns_rather_than_failing_the_step()
    {
        using var host = new TestHost();
        host.EnableTranslation();
        host.SetToday();

        host.Translation.StartThrows = new InvalidOperationException("exploded");

        var outcome = await host.Orchestrator.StartTodayAsync();

        Assert.True(outcome.Ok);
        Assert.Equal("warn", Step(host, Orchestrator.StepTranslate).Status);
        Assert.Equal(BroadcastStatus.Live, host.State.Snapshot().Broadcast!.Status);
    }

    /// <summary>
    /// The translated broadcast never coming up almost always means the OBS plugin target is not
    /// configured — which this panel cannot see and cannot fix, so the warning names it.
    /// </summary>
    [Fact]
    public async Task A_translated_broadcast_that_never_goes_live_warns_and_names_the_OBS_target()
    {
        using var host = new TestHost();
        host.EnableTranslation();
        host.SetToday();

        host.YouTube.LifeCycleById["bcast2"] = "ready";
        host.Orchestrator.TranslatedLiveTimeout = TimeSpan.FromMilliseconds(300);

        var outcome = await host.Orchestrator.StartTodayAsync();

        Assert.True(outcome.Ok);

        var step = Step(host, Orchestrator.StepAwaitLive);
        Assert.Equal("warn", step.Status);
        Assert.Contains("多路推流", step.Message!.Zh);
        Assert.Contains("multi-RTMP", step.Message.En);
        Assert.Equal(BroadcastStatus.Live, host.State.Snapshot().Broadcast!.Status);
    }

    // ---------------------------------------------------------------- stopping

    [Fact]
    public async Task Stopping_ends_both_broadcasts_and_the_translator()
    {
        using var host = new TestHost();
        host.EnableTranslation();
        host.SetToday();

        await host.Orchestrator.StartTodayAsync();
        var state = host.State.Snapshot();

        var outcome = await host.Orchestrator.StopAsync();

        Assert.True(outcome.Ok);
        Assert.Equal(1, host.Translation.StopCalls);
        Assert.Contains(state.Broadcast!.Id!, host.YouTube.TransitionedIds);
        Assert.Contains(state.Translated!.Id!, host.YouTube.TransitionedIds);

        var after = host.State.Snapshot();
        Assert.Equal(BroadcastStatus.Complete, after.Broadcast!.Status);
        Assert.Equal(BroadcastStatus.Complete, after.Translated!.Status);
    }

    /// <summary>
    /// The translator is deliberately left running when OBS will not stop.
    ///
    /// This pinned the opposite until a review caught it. Stopping the translator first meant that
    /// when obs-websocket dropped mid-service, the panel returned "stop it directly in OBS" while
    /// OBS carried on pushing both RTMP targets — and the translated one now streamed pure silence
    /// for as long as the operator took to walk over. Nothing restarted it, either.
    /// </summary>
    [Fact]
    public async Task The_translator_keeps_running_when_OBS_refuses_to_stop()
    {
        using var host = new TestHost();
        host.EnableTranslation();
        host.SetToday();

        await host.Orchestrator.StartTodayAsync();
        host.Obs.FailOnce[nameof(host.Obs.StopStreamAsync)] = new InvalidOperationException("stuck");

        var outcome = await host.Orchestrator.StopAsync();

        Assert.False(outcome.Ok);
        Assert.Equal(0, host.Translation.StopCalls);
        Assert.True(host.Translation.IsRunning);
    }

    /// <summary>And on the normal path it is stopped, just after OBS rather than before it.</summary>
    [Fact]
    public async Task The_translator_is_stopped_once_OBS_has_actually_stopped()
    {
        using var host = new TestHost();
        host.EnableTranslation();
        host.SetToday();

        await host.Orchestrator.StartTodayAsync();
        var outcome = await host.Orchestrator.StopAsync();

        Assert.True(outcome.Ok);
        Assert.Equal(1, host.Obs.StopStreamCalls);
        Assert.Equal(1, host.Translation.StopCalls);
    }

    // ---------------------------------------------------------------- reconnecting the translator

    /// <summary>
    /// The button used to answer "translation reconnected" and then be undone by the background
    /// reconciler on its next pass — in the one state it is shown for. An explanation the operator
    /// can act on is worth more than a retry that cannot work.
    /// </summary>
    [Fact]
    public async Task Reconnecting_is_refused_when_YouTube_has_already_ended_the_translated_broadcast()
    {
        using var host = new TestHost();
        host.EnableTranslation();
        host.SetToday();
        await host.Orchestrator.StartTodayAsync();

        host.State.Mutate(s => s.Translated!.Status = BroadcastStatus.Complete);
        host.Translation.StartedLanguages.Clear();

        var outcome = await host.Orchestrator.RestartTranslationAsync();

        Assert.False(outcome.Ok);
        Assert.Empty(host.Translation.StartedLanguages);
        Assert.Contains("多路推流", outcome.Message.Zh);
        Assert.Contains("multi-RTMP", outcome.Message.En);
    }

    [Fact]
    public async Task Reconnecting_is_refused_when_no_translated_broadcast_was_ever_created()
    {
        using var host = new TestHost();
        host.EnableTranslation();
        host.SetToday();

        host.YouTube.CreateFailure = request =>
            request.Title == TranslatedTitle ? new InvalidOperationException("boom") : null;
        await host.Orchestrator.StartTodayAsync();

        Assert.Null(host.State.Snapshot().Translated);
        host.Translation.StartedLanguages.Clear();

        var outcome = await host.Orchestrator.RestartTranslationAsync();

        Assert.False(outcome.Ok);
        Assert.Empty(host.Translation.StartedLanguages);
        Assert.Contains("原声", outcome.Message.Zh);
    }

    [Fact]
    public async Task Reconnecting_works_while_the_translated_broadcast_is_still_live()
    {
        using var host = new TestHost();
        host.EnableTranslation();
        host.SetToday();
        await host.Orchestrator.StartTodayAsync();
        host.Translation.StartedLanguages.Clear();

        var outcome = await host.Orchestrator.RestartTranslationAsync();

        Assert.True(outcome.Ok, outcome.Message.En);
        Assert.Equal(new[] { "en" }, host.Translation.StartedLanguages);
    }

    [Fact]
    public async Task A_translated_broadcast_that_will_not_end_does_not_fail_the_stop()
    {
        using var host = new TestHost();
        host.EnableTranslation();
        host.SetToday();

        await host.Orchestrator.StartTodayAsync();
        host.YouTube.TransitionFailure = id => id == "bcast2" ? new InvalidOperationException("nope") : null;

        var outcome = await host.Orchestrator.StopAsync();

        // Reported honestly, but the primary broadcast is ended and the stop is not a failure the
        // operator has to act on mid-teardown.
        Assert.True(outcome.Ok);
        Assert.Contains("YouTube", outcome.Message.En);
        Assert.Equal(BroadcastStatus.Complete, host.State.Snapshot().Broadcast!.Status);
    }

    // ---------------------------------------------------------------- cleanup paths

    /// <summary>
    /// The leftover cleanup lists every unfinished broadcast on the channel — including the
    /// translated one this run created moments ago. Ending that would silence the second stream in
    /// the middle of the service it belongs to.
    /// </summary>
    [Fact]
    public async Task Ending_leftovers_never_ends_this_runs_own_broadcasts()
    {
        using var host = new TestHost();
        host.EnableTranslation();
        host.SetToday();

        await host.Orchestrator.StartTodayAsync();
        var state = host.State.Snapshot();

        host.YouTube.Unfinished.AddRange(new[]
        {
            new BroadcastInfo(state.Broadcast!.Id!, Title, "live", "u1"),
            new BroadcastInfo(state.Translated!.Id!, TranslatedTitle, "live", "u2"),
            new BroadcastInfo("yesterday", "8/4/2026 Service", "live", "u3"),
        });

        await host.Orchestrator.EndPreviousAsync();

        Assert.Contains("yesterday", host.YouTube.TransitionedIds);
        Assert.DoesNotContain(state.Broadcast.Id!, host.YouTube.TransitionedIds);
        Assert.DoesNotContain(state.Translated.Id!, host.YouTube.TransitionedIds);
    }

    [Fact]
    public async Task Starting_another_service_clears_both_broadcasts()
    {
        using var host = new TestHost();
        host.EnableTranslation();
        host.SetToday();

        await host.Orchestrator.StartTodayAsync();
        await host.Orchestrator.StopAsync();

        Assert.True(host.Orchestrator.StartAnother());

        var state = host.State.Snapshot();
        Assert.Null(state.Broadcast);
        Assert.Null(state.Translated);
        Assert.False(state.Translation.Running);
    }

    // ---------------------------------------------------------------- notification

    [Fact]
    public async Task Both_links_go_to_telegram()
    {
        using var host = new TestHost();
        host.EnableTranslation();
        host.SetToday();
        host.Config.UpdateSettings(s =>
        {
            s.TelegramBotToken = "token";
            s.TelegramChatId = "-100123";
        });

        await host.Orchestrator.StartTodayAsync();
        await host.Notifications.SendCurrentAsync();

        var state = host.State.Snapshot();
        var sent = host.Telegram.Sent.Single();

        Assert.Contains(state.Broadcast!.WatchUrl!, sent);
        Assert.Contains(state.Translated!.WatchUrl!, sent);
    }

    [Fact]
    public async Task A_template_that_places_the_second_link_itself_is_left_alone()
    {
        using var host = new TestHost();
        host.EnableTranslation();
        host.SetToday();
        host.Config.UpdateSettings(s =>
        {
            s.TelegramBotToken = "token";
            s.TelegramChatId = "-100123";
            s.TelegramMessageDefault = "EN: {url2}\n中文: {url}";
        });

        await host.Orchestrator.StartTodayAsync();
        await host.Notifications.SendCurrentAsync();

        var state = host.State.Snapshot();

        Assert.Equal($"EN: {state.Translated!.WatchUrl}\n中文: {state.Broadcast!.WatchUrl}",
            host.Telegram.Sent.Single());
    }

    [Fact]
    public void A_single_language_service_sends_one_link_as_before() =>
        Assert.Equal("Service\nhttps://x/1",
            NotificationService.Render("{title}\n{url}", "Service", "https://x/1"));
}
