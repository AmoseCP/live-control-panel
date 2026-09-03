using LiveControlPanel.Core;
using LiveControlPanel.Translate;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace LiveControlPanel.Tests;

/// <summary>
/// The translator's lifecycle against fake audio hardware and a fake session.
///
/// The assertions worth reading are the ones about failure. Every one of them exists to hold the
/// same line: this component may go wrong in any way it likes, and the primary broadcast — which is
/// what a congregation is watching — must not notice.
/// </summary>
public sealed class TranslationServiceTests : IAsyncLifetime
{
    private TestHost _host = null!;
    private FakeGeminiSessionFactory _sessions = null!;
    private TranslationService _service = null!;

    public Task InitializeAsync()
    {
        _host = new TestHost();
        _host.EnableTranslation();
        _sessions = new FakeGeminiSessionFactory();
        _service = new TranslationService(
            _host.Config, _host.State, _host.Audio, _sessions, NullLogger<TranslationService>.Instance)
        {
            // Peaks and transcripts reach the panel on this tick. At the production five seconds the
            // assertions below would be racing it rather than testing anything.
            HealthInterval = TimeSpan.FromMilliseconds(100),
        };

        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        await _service.DisposeAsync();
        _host.Dispose();
    }

    /// <summary>Polls a condition the background loops satisfy, rather than sleeping a fixed time.</summary>
    private static async Task<bool> Eventually(Func<bool> condition, int timeoutMs = 5000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < deadline)
        {
            if (condition()) return true;
            await Task.Delay(20);
        }
        return condition();
    }

    private static byte[] Frame(byte value = 7) => Enumerable.Repeat(value, GeminiProtocol.ChunkBytes).ToArray();

    // ---------------------------------------------------------------- start

    [Fact]
    public async Task Starting_opens_the_configured_devices_and_connects()
    {
        var problem = await _service.StartAsync("en");

        Assert.Null(problem);
        Assert.True(_service.IsRunning);

        // The exact endpoints from settings — never the system default, which for the sink would put
        // the translated voice into the house PA.
        Assert.Equal("mixer-1", _host.Audio.CaptureRequests.Single());
        Assert.Equal("cable-1", _host.Audio.SinkRequests.Single());

        Assert.True(_host.Audio.LastCapture!.Started);
        Assert.True(_host.Audio.LastSink!.Started);
        Assert.Single(_sessions.Sessions);
        Assert.True(_sessions.Sessions[0].Connected);
    }

    [Fact]
    public async Task The_session_is_asked_for_the_language_the_service_wants()
    {
        await _service.StartAsync("zh-CN");

        var options = _sessions.Sessions.Single().Options;

        Assert.Equal("zh-CN", options.TargetLanguage);
        Assert.True(options.EchoTargetLanguage);
        Assert.Equal("test-key", _sessions.Sessions.Single().ApiKey);
    }

    [Fact]
    public async Task A_blank_target_language_falls_back_to_the_configured_one()
    {
        _host.Config.UpdateSettings(s => s.Translation.TargetLanguage = "ko");

        await _service.StartAsync("");

        Assert.Equal("ko", _sessions.Sessions.Single().Options.TargetLanguage);
    }

    [Fact]
    public async Task Starting_twice_replaces_the_first_run_rather_than_stacking_it()
    {
        await _service.StartAsync("en");
        await _service.StartAsync("en");

        Assert.Equal(2, _host.Audio.CaptureRequests.Count);
        Assert.True(await Eventually(() => _sessions.Sessions[0].Disposed));
        Assert.True(_service.IsRunning);
    }

    // ---------------------------------------------------------------- audio flow

    [Fact]
    public async Task Captured_frames_reach_the_session()
    {
        await _service.StartAsync("en");
        _host.Audio.LastCapture!.Emit(Frame());

        Assert.True(await Eventually(() => _sessions.Sessions[0].Sent.Count == 1));
        Assert.Equal(GeminiProtocol.ChunkBytes, _sessions.Sessions[0].Sent[0].Length);
    }

    [Fact]
    public async Task Translated_audio_reaches_the_virtual_cable()
    {
        await _service.StartAsync("en");

        var audio = new byte[] { 1, 2, 3, 4 };
        _sessions.Sessions[0].Push(new GeminiMessage { Audio = audio });

        Assert.True(await Eventually(() => _host.Audio.LastSink!.Written.Count == 1));
        Assert.Equal(audio, _host.Audio.LastSink!.Written[0]);
    }

    /// <summary>
    /// "Connected" is not the same as "there is sound". The panel reports the last time audio was
    /// actually written, because a session can be up and producing nothing at all.
    /// </summary>
    [Fact]
    public async Task The_time_of_the_last_translated_audio_is_reported()
    {
        await _service.StartAsync("en");

        Assert.Null(_host.State.Snapshot().Translation.LastAudioAt);

        _sessions.Sessions[0].Push(new GeminiMessage { Audio = new byte[] { 1, 2 } });

        Assert.True(await Eventually(() => _host.State.Snapshot().Translation.LastAudioAt is not null));
    }

    [Fact]
    public async Task The_latest_translated_line_is_reported()
    {
        await _service.StartAsync("en");
        _sessions.Sessions[0].Push(new GeminiMessage { OutputTranscript = "Hello everyone" });

        Assert.True(await Eventually(
            () => _host.State.Snapshot().Translation.LastTranscript == "Hello everyone"));
    }

    // ---------------------------------------------------------------- misconfiguration

    [Fact]
    public async Task A_missing_api_key_is_reported_without_touching_the_sound_card()
    {
        _host.Config.UpdateSettings(s => s.Translation.ApiKey = "");

        var problem = await _service.StartAsync("en");

        Assert.NotNull(problem);
        Assert.Empty(_host.Audio.CaptureRequests);
        Assert.Empty(_sessions.Sessions);
        Assert.False(_service.IsRunning);
    }

    /// <summary>
    /// An unset playback device would fall through to the system default — which on this PC is the
    /// mixer, putting the translation into the PA and back into the primary mix. Refuse instead.
    /// </summary>
    [Fact]
    public async Task A_missing_playback_device_refuses_to_start()
    {
        _host.Config.UpdateSettings(s => s.Translation.PlaybackDeviceId = "");

        var problem = await _service.StartAsync("en");

        Assert.NotNull(problem);
        Assert.Empty(_host.Audio.SinkRequests);
        Assert.False(_service.IsRunning);
    }

    [Fact]
    public async Task A_vanished_device_is_reported_in_both_languages_and_nothing_is_left_open()
    {
        _host.Audio.OpenSinkThrows = new AudioDeviceMissingException("cable-1", AudioDeviceMissingException.PlaybackRole);

        var problem = await _service.StartAsync("en");

        Assert.NotNull(problem);
        Assert.False(string.IsNullOrWhiteSpace(problem!.Zh));
        Assert.False(string.IsNullOrWhiteSpace(problem.En));

        // The capture device opened before the sink failed; it must not be left holding the mixer.
        Assert.True(_host.Audio.LastCapture!.Disposed);
    }

    [Fact]
    public async Task A_refused_session_is_reported_as_a_problem_not_an_exception()
    {
        _sessions.ConnectFailure = _ => new GeminiSessionException("quota exceeded");

        var problem = await _service.StartAsync("en");

        Assert.NotNull(problem);
        Assert.Contains("quota exceeded", problem!.En);
        Assert.False(_host.State.Snapshot().Translation.Connected);
    }

    /// <summary>
    /// The devices stay open through a failed connection, because the session is what retries — and
    /// re-acquiring the mixer endpoint on every attempt is a good way to lose it to OBS.
    /// </summary>
    [Fact]
    public async Task A_refused_session_leaves_the_devices_open_for_the_retry()
    {
        _sessions.ConnectFailure = _ => new GeminiSessionException("nope");

        await _service.StartAsync("en");

        Assert.False(_host.Audio.LastCapture!.Disposed);
        Assert.True(_service.IsRunning);
    }

    // ---------------------------------------------------------------- reconnection

    [Fact]
    public async Task A_failed_first_attempt_is_retried()
    {
        _sessions.ConnectFailure = attempt => attempt == 0 ? new GeminiSessionException("first") : null;

        var problem = await _service.StartAsync("en");

        Assert.NotNull(problem);
        Assert.True(await Eventually(() => _sessions.Sessions.Count >= 2));
        Assert.True(await Eventually(() => _host.State.Snapshot().Translation.Connected));
        Assert.Null(_host.State.Snapshot().Translation.LastError);
    }

    /// <summary>
    /// goAway means the server is retiring this session, and the next one is opened at once rather
    /// than after a backoff — the second broadcast would otherwise fall silent for no reason.
    /// </summary>
    [Fact]
    public async Task A_go_away_reconnects_immediately_and_is_not_an_error()
    {
        await _service.StartAsync("en");
        _sessions.Sessions[0].Push(new GeminiMessage { GoAway = true });

        Assert.True(await Eventually(() => _sessions.Sessions.Count >= 2, 2000));
        Assert.True(await Eventually(() => _host.State.Snapshot().Translation.Connected));
        Assert.Null(_host.State.Snapshot().Translation.LastError);
    }

    [Fact]
    public async Task A_dropped_session_reconnects_and_audio_flows_again()
    {
        await _service.StartAsync("en");
        _sessions.Sessions[0].Close();

        Assert.True(await Eventually(() => _sessions.Sessions.Count >= 2));

        _host.Audio.LastCapture!.Emit(Frame());

        Assert.True(await Eventually(() => _sessions.Sessions[^1].Sent.Count >= 1));
    }

    [Fact]
    public async Task A_server_error_is_surfaced_to_the_operator()
    {
        await _service.StartAsync("en");
        _sessions.Sessions[0].Push(new GeminiMessage { Error = "internal" });

        Assert.True(await Eventually(() => _host.State.Snapshot().Translation.LastError is not null));
        Assert.Contains("internal", _host.State.Snapshot().Translation.LastError!.En);
    }

    [Fact]
    public async Task A_capture_device_that_faults_mid_service_is_surfaced()
    {
        await _service.StartAsync("en");
        _host.Audio.LastCapture!.Fault(new InvalidOperationException("unplugged"));

        Assert.True(await Eventually(() => _host.State.Snapshot().Translation.LastError is not null));
    }

    // ---------------------------------------------------------------- stop

    [Fact]
    public async Task Stopping_closes_the_devices_and_the_session()
    {
        await _service.StartAsync("en");
        var capture = _host.Audio.LastCapture!;
        var sink = _host.Audio.LastSink!;

        await _service.StopAsync();

        Assert.False(_service.IsRunning);
        Assert.True(capture.Disposed);
        Assert.True(sink.Disposed);
        Assert.True(await Eventually(() => _sessions.Sessions[0].Disposed));

        var state = _host.State.Snapshot().Translation;
        Assert.False(state.Running);
        Assert.False(state.Connected);
    }

    [Fact]
    public async Task Stopping_a_service_that_never_started_is_harmless()
    {
        await _service.StopAsync();

        Assert.False(_service.IsRunning);
        Assert.Empty(_sessions.Sessions);
    }

    [Fact]
    public async Task No_further_sessions_are_opened_after_a_stop()
    {
        _sessions.ConnectFailure = _ => new GeminiSessionException("keeps failing");

        await _service.StartAsync("en");
        await _service.StopAsync();

        var attempts = _sessions.Sessions.Count;
        await Task.Delay(1500);

        Assert.Equal(attempts, _sessions.Sessions.Count);
    }

    // ---------------------------------------------------------------- settings-page test

    [Fact]
    public async Task The_smoke_test_reports_a_working_path()
    {
        _host.Audio.OpenCaptureThrows = null;

        var test = _service.TestAsync(TimeSpan.FromSeconds(3));

        Assert.True(await Eventually(() => _sessions.Sessions.Count == 1));
        _host.Audio.LastCapture!.LastPeak = 0.4;
        _sessions.Sessions[0].Push(new GeminiMessage
        {
            Audio = new byte[] { 1, 2 },
            OutputTranscript = "Hello",
        });

        var report = await test;

        Assert.True(report.Ok, report.Message.En);
        Assert.True(report.Connected);
        Assert.True(report.HeardInput);
        Assert.True(report.ProducedAudio);
        Assert.Equal("Hello", report.OutputTranscript);

        // A test must never leave the translator running against a service that is not on air.
        Assert.False(_service.IsRunning);
    }

    [Fact]
    public async Task The_smoke_test_says_so_when_the_mixer_is_silent()
    {
        var report = await _service.TestAsync(TimeSpan.FromMilliseconds(500));

        Assert.False(report.Ok);
        Assert.True(report.Connected);
        Assert.False(report.HeardInput);
        Assert.False(string.IsNullOrWhiteSpace(report.Message.Zh));
    }

    [Fact]
    public async Task The_smoke_test_refuses_to_interrupt_a_running_service()
    {
        await _service.StartAsync("en");

        var report = await _service.TestAsync(TimeSpan.FromMilliseconds(200));

        Assert.False(report.Ok);
        Assert.True(_service.IsRunning);
    }
}
