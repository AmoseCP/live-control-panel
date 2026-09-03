using System.Runtime.Versioning;
using System.Threading.Channels;
using LiveControlPanel.Config;
using LiveControlPanel.Core;

namespace LiveControlPanel.Translate;

/// <summary>What a settings-page smoke test observed.</summary>
public sealed record TranslationTestReport(
    bool Ok,
    Msg Message,
    bool Connected,
    bool HeardInput,
    bool ProducedAudio,
    string? InputTranscript,
    string? OutputTranscript);

/// <summary>
/// The AI-translated audio track's lifecycle.
///
/// The contract that matters more than anything it does: <b>it can never take the primary broadcast
/// down</b>. Nothing here throws into the orchestrator, nothing here touches OBS, and every failure
/// path ends in a warning plus a silent second stream. The camera feed and the mixer audio reach
/// YouTube exactly as they did before this feature existed.
/// </summary>
public interface ITranslationService
{
    bool IsRunning { get; }

    /// <summary>
    /// True while a settings-page smoke test owns the translator.
    ///
    /// The test deliberately runs with no translated broadcast in state, which is exactly what the
    /// background reconciler treats as an orphaned translator — so without this it stopped the test
    /// within five seconds and the test then reported "cannot reach Gemini" on a perfectly healthy
    /// setup, sending an administrator off to fix configuration that was already correct.
    /// </summary>
    bool IsTesting { get; }

    /// <summary>Starts translating into <paramref name="targetLanguage"/>. Null means it came up.</summary>
    Task<Msg?> StartAsync(string targetLanguage, CancellationToken ct = default);

    Task StopAsync();

    /// <summary>Settings-page verification: run briefly, report what actually happened.</summary>
    Task<TranslationTestReport> TestAsync(TimeSpan duration, CancellationToken ct = default);
}

[SupportedOSPlatform("windows")]
public sealed class TranslationService : ITranslationService, IAsyncDisposable
{
    /// <summary>How long a start waits for the first connection before reporting what it sees.</summary>
    private static readonly TimeSpan ConnectWait = TimeSpan.FromSeconds(15);

    private static readonly TimeSpan MaxBackoff = TimeSpan.FromSeconds(15);

    /// <summary>
    /// How often peaks, the last-audio timestamp and the latest transcript are pushed to the panel.
    ///
    /// Five seconds because this rides the same WebSocket as everything else and none of it is
    /// worth a faster push — the operator is reading "is there sound", not watching a meter. State
    /// changes that actually need saying (a connection coming up, an error arriving) are pushed the
    /// moment they happen rather than waiting for a tick. Settable so tests are not forced to wait
    /// out a real interval.
    /// </summary>
    internal TimeSpan HealthInterval { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Five seconds of audio in flight. Deeper would let the translation drift behind the picture
    /// after a network stall; the oldest frames are dropped instead, which is the right trade for
    /// speech that is already a few seconds behind the speaker.
    /// </summary>
    private const int FrameQueueDepth = 50;

    /// <summary>
    /// How many back-to-back immediate reconnects are allowed before the backoff takes over.
    ///
    /// Only a goAway earns an immediate reconnect — the server retiring a session it intends us to
    /// re-open. Any other clean close is indistinguishable from "accepted the setup, then hung up",
    /// which is what an exhausted quota or a retired preview model looks like; reconnecting on that
    /// with no delay hammered the API for the length of a whole service.
    /// </summary>
    private const int MaxImmediateReconnects = 3;

    private readonly ConfigStore _config;
    private readonly StateManager _state;
    private readonly IAudioEngine _audio;
    private readonly IGeminiSessionFactory _sessions;
    private readonly ILogger<TranslationService> _log;

    private readonly SemaphoreSlim _lifecycle = new(1, 1);

    private CancellationTokenSource? _run;
    private CancellationTokenSource? _session;
    private Task? _worker;
    private Task? _health;
    private IAudioCapture? _capture;
    private IAudioSink? _sink;
    private Channel<byte[]>? _frames;
    private TaskCompletionSource<Msg?>? _firstOutcome;

    private volatile bool _connected;

    /// <summary>Set by the receive loop when the server asks us to retire this session.</summary>
    private volatile bool _goAway;
    private Msg? _lastError;
    private DateTime? _lastAudioAt;
    private string? _lastTranscript;
    private string? _lastInputTranscript;
    private string _targetLanguage = "en";

    public TranslationService(
        ConfigStore config,
        StateManager state,
        IAudioEngine audio,
        IGeminiSessionFactory sessions,
        ILogger<TranslationService> log)
    {
        _config = config;
        _state = state;
        _audio = audio;
        _sessions = sessions;
        _log = log;
    }

    public bool IsRunning => _run is { IsCancellationRequested: false };

    private volatile bool _testing;

    public bool IsTesting => _testing;

    // ---------------------------------------------------------------- start / stop

    public async Task<Msg?> StartAsync(string targetLanguage, CancellationToken ct = default)
    {
        await _lifecycle.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await StopCoreAsync().ConfigureAwait(false);

            var settings = _config.Settings.Translation;
            _targetLanguage = string.IsNullOrWhiteSpace(targetLanguage) ? settings.TargetLanguage : targetLanguage;

            if (Misconfigured(settings) is { } problem)
            {
                _lastError = problem;
                Publish(running: false);
                return problem;
            }

            try
            {
                _capture = _audio.OpenCapture(settings.CaptureDeviceId);
                _sink = _audio.OpenSink(settings.PlaybackDeviceId);
            }
            catch (Exception ex)
            {
                await DisposeDevicesAsync().ConfigureAwait(false);
                var message = Describe(ex);
                _lastError = message;
                Publish(running: false);
                _log.LogWarning(ex, "Opening the translation audio devices failed");
                return message;
            }

            _frames = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(FrameQueueDepth)
            {
                FullMode = BoundedChannelFullMode.DropOldest,
                SingleReader = true,
                SingleWriter = true,
            });

            _capture.FrameReady += OnFrame;
            _capture.Faulted += OnCaptureFaulted;

            _lastError = null;
            _lastAudioAt = null;
            _lastTranscript = null;
            _lastInputTranscript = null;
            _firstOutcome = new TaskCompletionSource<Msg?>(TaskCreationOptions.RunContinuationsAsynchronously);
            _run = new CancellationTokenSource();

            try
            {
                _sink.Start();
                _capture.Start();
            }
            catch (Exception ex)
            {
                var message = Describe(ex);
                _lastError = message;
                await StopCoreAsync().ConfigureAwait(false);
                _log.LogWarning(ex, "Starting the translation audio devices failed");

                // Published, like every other failure path here. Without it the card kept the
                // previous run's green "translation is going into the second broadcast" — so a
                // mixer switched off mid-service, then a tapped "reconnect", read as healthy.
                Publish(running: false);
                return message;
            }

            var options = new GeminiSessionOptions(
                settings.Model, _targetLanguage, settings.EchoTargetLanguage);

            var token = _run.Token;
            _worker = Task.Run(() => RunAsync(settings.ApiKey, options, token), CancellationToken.None);
            _health = Task.Run(() => HealthAsync(token), CancellationToken.None);

            Publish(running: true);

            // Report what the operator would see, not what we hope: wait for the first attempt to
            // resolve, and if it has not by then say so rather than claiming success.
            var first = _firstOutcome.Task;
            var settled = await Task.WhenAny(first, Task.Delay(ConnectWait, ct)).ConfigureAwait(false);

            if (settled != first)
                return new Msg(
                    "翻译还在连接中，英文流可能先有画面无声。稍后看状态卡里的「AI 翻译」一行。",
                    "The translator is still connecting; the translated stream may start silent. Watch the " +
                    "\"AI translation\" line on the status card.");

            return await first.ConfigureAwait(false);
        }
        finally
        {
            _lifecycle.Release();
        }
    }

    public async Task StopAsync()
    {
        await _lifecycle.WaitAsync().ConfigureAwait(false);
        try
        {
            await StopCoreAsync().ConfigureAwait(false);
            Publish(running: false);
        }
        finally
        {
            _lifecycle.Release();
        }
    }

    private async Task StopCoreAsync()
    {
        _run?.Cancel();
        _session?.Cancel();

        _frames?.Writer.TryComplete();

        if (_capture is not null)
        {
            _capture.FrameReady -= OnFrame;
            _capture.Faulted -= OnCaptureFaulted;
            try { _capture.Stop(); } catch (Exception) { /* device already gone */ }
        }

        try { _sink?.Stop(); } catch (Exception) { /* device already gone */ }

        foreach (var task in new[] { _worker, _health })
        {
            if (task is null) continue;
            try { await task.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false); }
            catch (Exception) { /* a hung pump must not block ending a service */ }
        }

        _worker = null;
        _health = null;

        await DisposeDevicesAsync().ConfigureAwait(false);

        _run?.Dispose();
        _run = null;
        _session?.Dispose();
        _session = null;
        _frames = null;
        _connected = false;
    }

    private ValueTask DisposeDevicesAsync()
    {
        try { _capture?.Dispose(); } catch (Exception) { /* ignore */ }
        try { _sink?.Dispose(); } catch (Exception) { /* ignore */ }
        _capture = null;
        _sink = null;
        return ValueTask.CompletedTask;
    }

    // ---------------------------------------------------------------- session loop

    private async Task RunAsync(string apiKey, GeminiSessionOptions options, CancellationToken ct)
    {
        var backoff = TimeSpan.FromSeconds(1);
        var immediateReconnects = 0;

        while (!ct.IsCancellationRequested)
        {
            IGeminiSession? session = null;
            var reconnectImmediately = false;
            _goAway = false;

            try
            {
                session = _sessions.Create(apiKey, options);
                await session.ConnectAsync(ct).ConfigureAwait(false);

                _connected = true;
                _lastError = null;
                _firstOutcome?.TrySetResult(null);
                Publish(running: true);
                _log.LogInformation("Gemini translation session connected (target {Language})", options.TargetLanguage);

                backoff = TimeSpan.FromSeconds(1);

                using var scope = CancellationTokenSource.CreateLinkedTokenSource(ct);
                _session = scope;

                var receive = session.ReceiveLoopAsync(OnMessage, scope.Token);
                var send = PumpAsync(session, scope.Token);

                var finished = await Task.WhenAny(receive, send).ConfigureAwait(false);
                scope.Cancel();
                await Quiet(receive).ConfigureAwait(false);
                await Quiet(send).ConfigureAwait(false);

                // Only a goAway earns the immediate reconnect. A clean close on its own is what an
                // exhausted quota looks like — setup acknowledged, then hung up on the first audio
                // chunk — and treating that as "retired, re-open at once" produced a zero-delay
                // reconnect loop that ran for the whole service.
                reconnectImmediately = finished.IsCompletedSuccessfully && _goAway;
                await finished.ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (OperationCanceledException)
            {
                // The session scope was cancelled rather than the whole run — a goAway, or a session
                // we retired ourselves. That is a clean end, not something to report as an error and
                // certainly not something to sit out a backoff for.
                reconnectImmediately = true;
            }
            catch (Exception ex)
            {
                var message = Describe(ex);
                _connected = false;
                _lastError = message;
                _firstOutcome?.TrySetResult(message);
                Publish(running: true);
                _log.LogWarning(ex, "Gemini translation session ended");
            }
            finally
            {
                _session = null;
                if (session is not null)
                {
                    try { await session.DisposeAsync().ConfigureAwait(false); }
                    catch (Exception) { /* ignore */ }
                }
            }

            _connected = false;
            if (ct.IsCancellationRequested) break;

            // Even a genuine goAway is capped: a server stuck in a retire-immediately loop must not
            // become a hot loop here.
            if (reconnectImmediately && ++immediateReconnects <= MaxImmediateReconnects) continue;

            immediateReconnects = 0;

            try { await Task.Delay(backoff, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }

            backoff = TimeSpan.FromTicks(Math.Min(backoff.Ticks * 2, MaxBackoff.Ticks));
        }

        _connected = false;
        Publish(running: false);
    }

    private async Task PumpAsync(IGeminiSession session, CancellationToken ct)
    {
        var reader = _frames?.Reader;
        if (reader is null) return;

        while (await reader.WaitToReadAsync(ct).ConfigureAwait(false))
        {
            while (reader.TryRead(out var frame))
            {
                await session.SendAudioAsync(frame, ct).ConfigureAwait(false);
            }
        }
    }

    private void OnMessage(GeminiMessage message)
    {
        if (message.Audio is { Length: > 0 } audio)
        {
            _sink?.Write(audio);
            _lastAudioAt = DateTime.Now;
        }

        if (message.OutputTranscript is { } text) _lastTranscript = text;
        if (message.InputTranscript is { } heard) _lastInputTranscript = heard;

        if (message.Error is { } error)
        {
            _lastError = new Msg($"Gemini 报错：{error}", $"Gemini reported: {error}");
            _log.LogWarning("Gemini translation reported an error: {Error}", error);

            // Pushed at once rather than on the next health tick: this is the one thing on the card
            // the operator may need to act on, and up to five seconds of "everything is fine" after
            // it arrives is five seconds of the wrong answer.
            Publish(running: IsRunning);
        }

        // The server is retiring this session. Ending it here means the next one is already
        // connecting while this one still has audio queued, instead of after a silent drop.
        if (message.GoAway)
        {
            _goAway = true;
            _log.LogInformation("Gemini asked to end the session; reconnecting");
            try { _session?.Cancel(); } catch (ObjectDisposedException) { /* already gone */ }
        }
    }

    private void OnFrame(byte[] frame) => _frames?.Writer.TryWrite(frame);

    private void OnCaptureFaulted(Exception ex)
    {
        _lastError = Describe(ex);
        _log.LogWarning(ex, "The translation capture device faulted");
        Publish(running: IsRunning);
    }

    private async Task HealthAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try { await Task.Delay(HealthInterval, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }

            Publish(running: true);
        }
    }

    // ---------------------------------------------------------------- settings-page test

    public async Task<TranslationTestReport> TestAsync(TimeSpan duration, CancellationToken ct = default)
    {
        if (IsRunning)
            return new TranslationTestReport(false, new Msg(
                "翻译正在运行，测试会打断它。请先结束这场直播再测试。",
                "Translation is running and a test would interrupt it. End the broadcast first."),
                false, false, false, null, null);

        var settings = _config.Settings.Translation;

        _testing = true;
        var start = await StartAsync(settings.TargetLanguage, ct).ConfigureAwait(false);

        try
        {
            // Sampled throughout rather than read once at the end. LastPeak is the peak of the most
            // recent frame — about ten milliseconds — and the loop below exits the moment the
            // translated reply arrives, by which time the speaker has usually stopped. Reading it
            // there reported "the capture device is silent" on a working mixer.
            var heardInput = false;

            var deadline = DateTime.UtcNow + duration;
            while (DateTime.UtcNow < deadline && !ct.IsCancellationRequested)
            {
                heardInput |= (_capture?.LastPeak ?? 0) > 0.0005;

                if (_lastAudioAt is not null && _lastTranscript is not null) break;
                await Task.Delay(TimeSpan.FromMilliseconds(250), ct).ConfigureAwait(false);
            }

            heardInput |= (_capture?.LastPeak ?? 0) > 0.0005;
            var produced = _lastAudioAt is not null;
            var connected = _connected;

            var message = !connected
                ? _lastError ?? new Msg("连不上 Gemini。", "Cannot reach Gemini.")
                : !heardInput
                    ? new Msg(
                        "已连上 Gemini，但采集设备上听不到声音。请确认选的是调音台那个录音设备，" +
                        "并让人对着麦克风说句话再测一次。",
                        "Connected to Gemini, but the capture device is silent. Check that the selected " +
                        "recording device is the mixer, have someone speak into a microphone, and test again.")
                    : produced
                        ? new Msg("翻译链路正常。", "The translation path is working.")
                        : new Msg(
                            "已连上 Gemini 并听到了声音，但还没有收到翻译语音。请多说几句再测一次。",
                            "Connected and hearing audio, but no translated speech came back yet. Speak a " +
                            "little longer and test again.");

            return new TranslationTestReport(
                connected && heardInput && produced,
                start ?? message,
                connected,
                heardInput,
                produced,
                _lastInputTranscript,
                _lastTranscript);
        }
        finally
        {
            await StopAsync().ConfigureAwait(false);
            _testing = false;
        }
    }

    // ---------------------------------------------------------------- helpers

    private Msg? Misconfigured(TranslationSettings settings)
    {
        if (string.IsNullOrWhiteSpace(settings.ApiKey))
            return new Msg(
                "还没有填 Gemini API Key，无法翻译。请让管理员在设置页填写。",
                "No Gemini API key is configured, so nothing can be translated. Ask the administrator to " +
                "enter one on the settings page.");

        // Not left to the system default on purpose. An unset capture device would send whatever
        // microphone Windows currently prefers to the model — a webcam, a headset, the wrong input
        // on the mixer — and the panel would report a perfectly healthy translator the whole time.
        // Silence is diagnosable; confidently translating the wrong room is not.
        if (string.IsNullOrWhiteSpace(settings.CaptureDeviceId))
            return new Msg(
                "还没有选翻译要采集的声音设备（应选调音台那个录音设备）。请在设置页选择。",
                "No recording device is selected for the translator to listen to (it should be the mixer). " +
                "Choose one on the settings page.");

        if (string.IsNullOrWhiteSpace(settings.PlaybackDeviceId))
            return new Msg(
                "还没有选翻译语音的播放设备（应选虚拟声卡 CABLE Input）。请在设置页选择。",
                "No playback device is selected for the translated voice (it should be the virtual cable's " +
                "CABLE Input). Choose one on the settings page.");

        return null;
    }

    private static Msg Describe(Exception ex) => ex switch
    {
        AudioDeviceMissingException missing => new Msg(
            "找不到设置里选的声音设备。请确认调音台已开机、USB 线已插好、虚拟声卡还在，" +
            "然后在设置页重新选一次。",
            "An audio device selected in settings is missing. Check that the mixer is powered on, its USB " +
            "cable is connected and the virtual cable is still installed, then pick the devices again."),

        GeminiSessionException gemini => new Msg(
            $"连不上 Gemini 翻译服务：{gemini.Message}",
            $"Cannot reach the Gemini translation service: {gemini.Message}"),

        System.Net.WebSockets.WebSocketException or HttpRequestException => new Msg(
            "连不上 Gemini 翻译服务，请检查网络。",
            "Cannot reach the Gemini translation service. Check the network."),

        _ => new Msg(
            $"翻译出错：{ex.Message}",
            $"Translation failed: {ex.Message}"),
    };

    private static async Task Quiet(Task task)
    {
        try { await task.ConfigureAwait(false); }
        catch (Exception) { /* the caller already reported whichever one failed first */ }
    }

    private void Publish(bool running) => _state.Mutate(s => s.Translation = new TranslationState
    {
        Enabled = s.Translation.Enabled,
        Running = running,
        Connected = _connected,
        TargetLanguage = _targetLanguage,
        LastAudioAt = _lastAudioAt,
        InputPeak = _capture?.LastPeak ?? 0,
        OutputPeak = _sink?.LastPeak ?? 0,
        LastTranscript = _lastTranscript,
        LastError = _lastError,
    });

    public async ValueTask DisposeAsync()
    {
        await StopAsync().ConfigureAwait(false);
        _lifecycle.Dispose();
    }
}
