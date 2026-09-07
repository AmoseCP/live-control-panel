using LiveControlPanel.Config;
using LiveControlPanel.Core;
using LiveControlPanel.Obs;
using LiveControlPanel.Translate;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace LiveControlPanel.Tests;

/// <summary>
/// Two things that decide whether a bilingual service degrades honestly: the translator never
/// guessing which microphone to listen to, and its lifetime being tied to the state it serves
/// rather than to one code path.
/// </summary>
public sealed class TranslationHealthTests
{
    private static PreflightItem Translation(IEnumerable<PreflightItem> items) =>
        items.Single(i => i.Key == "translation");

    private static PanelBackgroundService Service(TestHost host) => new(
        new ObsClient(host.Config, NullLogger<ObsClient>.Instance),
        host.State,
        host.YouTube,
        host.Translation,
        NullLogger<PanelBackgroundService>.Instance);

    // ------------------------------------------------- never guess the capture device

    /// <summary>
    /// An unset capture device used to fall through to the system default recording device, so the
    /// translator would carry on translating a webcam microphone while the panel reported a
    /// perfectly healthy session. Silence is diagnosable; confidently translating the wrong room is
    /// not — so it refuses instead.
    /// </summary>
    [Fact]
    public async Task The_translator_refuses_to_guess_which_microphone_to_translate()
    {
        using var host = new TestHost();
        host.EnableTranslation();
        host.Config.UpdateSettings(s => s.Translation.CaptureDeviceId = "");

        var sessions = new FakeGeminiSessionFactory();
        var service = new TranslationService(
            host.Config, host.State, host.Audio, sessions, NullLogger<TranslationService>.Instance);

        var problem = await service.StartAsync("en");

        Assert.NotNull(problem);
        Assert.Empty(host.Audio.CaptureRequests);
        Assert.Empty(sessions.Sessions);
        Assert.False(service.IsRunning);
    }

    [Fact]
    public async Task The_checks_say_so_before_anyone_presses_start()
    {
        using var host = new TestHost();
        host.EnableTranslation();
        host.Config.UpdateSettings(s => s.Translation.CaptureDeviceId = "");
        host.SetToday();

        var item = Translation(await host.Preflight.RunAsync(
            host.Config.FindTemplate("wednesday-service")));

        Assert.False(item.Ok);
        Assert.Contains("采集", item.Message.Zh);
        Assert.Contains("will not guess", item.Message.En);
    }

    [Fact]
    public async Task A_properly_configured_service_passes_the_translation_check()
    {
        using var host = new TestHost();
        host.EnableTranslation();
        host.SetToday();

        var item = Translation(await host.Preflight.RunAsync(
            host.Config.FindTemplate("wednesday-service")));

        Assert.True(item.Ok, item.Message.En);
    }

    // ------------------------------------------------- lifetime tied to state, not to a call site

    [Fact]
    public async Task A_translator_with_no_translated_broadcast_is_stopped()
    {
        using var host = new TestHost();
        await host.Translation.StartAsync("en");

        await Service(host).ReconcileTranslatorAsync();

        Assert.Equal(1, host.Translation.StopCalls);
        Assert.False(host.Translation.IsRunning);
    }

    [Fact]
    public async Task A_translator_serving_a_live_broadcast_is_left_alone()
    {
        using var host = new TestHost();
        host.EnableTranslation();
        host.SetToday();
        await host.Orchestrator.StartTodayAsync();

        await Service(host).ReconcileTranslatorAsync();

        Assert.Equal(0, host.Translation.StopCalls);
        Assert.True(host.Translation.IsRunning);
    }

    [Fact]
    public async Task A_translator_whose_broadcast_has_finished_is_stopped()
    {
        using var host = new TestHost();
        await host.Translation.StartAsync("en");
        host.State.Mutate(s => s.Translated = new BroadcastState
        {
            Id = "bcast2",
            Status = BroadcastStatus.Complete,
        });

        await Service(host).ReconcileTranslatorAsync();

        Assert.Equal(1, host.Translation.StopCalls);
    }

    [Fact]
    public async Task Nothing_happens_when_the_translator_is_not_running()
    {
        using var host = new TestHost();

        await Service(host).ReconcileTranslatorAsync();

        Assert.Equal(0, host.Translation.StopCalls);
    }

    /// <summary>
    /// A settings-page smoke test owns the translator and deliberately runs with no translated
    /// broadcast — the very condition the reconciler treats as orphaned. It used to stop the test
    /// within five seconds, and the test then reported "cannot reach Gemini" on a healthy setup.
    /// </summary>
    [Fact]
    public async Task A_smoke_test_in_flight_is_not_reconciled_away()
    {
        using var host = new TestHost();
        await host.Translation.StartAsync("en");
        host.Translation.IsTesting = true;

        await Service(host).ReconcileTranslatorAsync();

        Assert.Equal(0, host.Translation.StopCalls);
        Assert.True(host.Translation.IsRunning);
    }

    /// <summary>The real service reports it, and the reconciler leaves the test alone end to end.</summary>
    [Fact]
    public async Task The_real_service_reports_a_test_in_flight()
    {
        using var host = new TestHost();
        host.EnableTranslation();

        var sessions = new FakeGeminiSessionFactory();
        var service = new TranslationService(
            host.Config, host.State, host.Audio, sessions, NullLogger<TranslationService>.Instance);

        Assert.False(service.IsTesting);

        var test = service.TestAsync(TimeSpan.FromMilliseconds(600));

        // Observed while it runs, which is when the reconciler would otherwise fire.
        var seenTesting = false;
        for (var i = 0; i < 40 && !test.IsCompleted; i++)
        {
            seenTesting |= service.IsTesting;
            await Task.Delay(20);
        }

        await test;

        Assert.True(seenTesting);
        Assert.False(service.IsTesting);
    }

    /// <summary>
    /// The smoke test judges the microphone over the whole window. It used to read the peak of the
    /// most recent frame — about ten milliseconds — after breaking out the moment the translated
    /// reply arrived, by which time the speaker has usually stopped. A working mixer was reported
    /// silent.
    /// </summary>
    [Fact]
    public async Task The_smoke_test_remembers_that_it_heard_something()
    {
        using var host = new TestHost();
        host.EnableTranslation();

        var sessions = new FakeGeminiSessionFactory();
        var service = new TranslationService(
            host.Config, host.State, host.Audio, sessions, NullLogger<TranslationService>.Instance);

        var test = service.TestAsync(TimeSpan.FromSeconds(5));

        for (var i = 0; i < 100 && sessions.Sessions.Count == 0; i++) await Task.Delay(20);

        // Someone speaks…
        host.Audio.LastCapture!.LastPeak = 0.4;
        await Task.Delay(400);

        // …then stops, and only afterwards does the translated reply arrive.
        host.Audio.LastCapture.LastPeak = 0;
        sessions.Sessions[0].Push(new GeminiMessage { Audio = new byte[] { 1, 2 }, OutputTranscript = "Hi" });

        var report = await test;

        Assert.True(report.HeardInput);
        Assert.True(report.Ok, report.Message.En);
    }

    /// <summary>
    /// The scenario that made this necessary. The deployment guide's own fallback is to stop OBS
    /// directly and end the broadcast in YouTube Studio — which never reaches the panel's stop path.
    /// The day rollover then cleared the broadcast from state while the translator kept its Gemini
    /// session open and went on paying for it around the clock.
    /// </summary>
    [Fact]
    public async Task A_broadcast_ended_outside_the_panel_does_not_leave_the_translator_running()
    {
        using var host = new TestHost();
        host.EnableTranslation();
        host.SetToday();
        await host.Orchestrator.StartTodayAsync();

        Assert.True(host.Translation.IsRunning);
        Assert.NotNull(host.State.Snapshot().Translated);

        // Ended in YouTube Studio and stopped in OBS, so Orchestrator.StopAsync never ran.
        host.Obs.Streaming = false;
        host.State.Mutate(s =>
        {
            s.Broadcast!.Status = BroadcastStatus.Complete;
            s.Translated!.Status = BroadcastStatus.Complete;
        });

        // Next day: the rollover retires yesterday's work from state.
        host.State.Clock = () => new DateTime(2026, 8, 6, 3, 0, 0);
        Assert.Null(host.State.Snapshot().Translated);

        await Service(host).ReconcileTranslatorAsync();

        Assert.Equal(1, host.Translation.StopCalls);
        Assert.False(host.Translation.IsRunning);
    }
}
