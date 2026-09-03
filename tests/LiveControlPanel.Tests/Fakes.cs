using System.Threading.Channels;
using LiveControlPanel.Notify;
using LiveControlPanel.Obs;
using LiveControlPanel.Translate;
using LiveControlPanel.Youtube;

namespace LiveControlPanel.Tests;

/// <summary>
/// Recording doubles for the three external systems. No test may touch the real YouTube API, a real
/// OBS instance, or the real Telegram Bot API.
/// </summary>
public sealed class FakeYouTubeClient : IYouTubeClient
{
    private int _counter;

    public int CreateCalls { get; private set; }
    public int BindCalls { get; private set; }
    public int ThumbnailCalls { get; private set; }
    public int TransitionCalls { get; private set; }
    public List<string> TransitionedIds { get; } = new();

    /// <summary>Thrown on the next call to the named operation, then cleared.</summary>
    public Dictionary<string, Exception> FailOnce { get; } = new();

    /// <summary>
    /// Per-call failure hooks. FailOnce cannot express "fail the second create but not the first",
    /// which is exactly the bilingual case that matters: the translated broadcast failing must leave
    /// the primary one alone.
    /// </summary>
    public Func<CreateBroadcastRequest, Exception?>? CreateFailure { get; set; }

    public Func<string, Exception?>? BindFailure { get; set; }

    public Func<string, Exception?>? ThumbnailFailure { get; set; }

    public Func<string, Exception?>? TransitionFailure { get; set; }

    public List<BroadcastInfo> Unfinished { get; set; } = new();
    public string LifeCycleStatus { get; set; } = "live";

    /// <summary>
    /// Per-broadcast status, taking precedence over <see cref="LifeCycleStatus"/>. The bilingual
    /// cases need the two broadcasts to disagree — that is the whole point of the warning path.
    /// </summary>
    public Dictionary<string, string> LifeCycleById { get; } = new(StringComparer.Ordinal);

    /// <summary>Titles in creation order, so the suffixing of the translated one can be asserted.</summary>
    public List<string> CreatedTitles { get; } = new();

    /// <summary>Which broadcast was bound to which stream key.</summary>
    public List<(string BroadcastId, string StreamId)> Bindings { get; } = new();

    /// <summary>Broadcasts a thumbnail was uploaded for.</summary>
    public List<string> ThumbnailIds { get; } = new();
    public AuthInfo Auth { get; set; } = new(true, 173, DateTime.Now.AddDays(-7), null);
    public CreateBroadcastRequest? LastCreateRequest { get; private set; }

    public Task<AuthInfo> GetAuthInfoAsync(CancellationToken ct = default)
    {
        Throw(nameof(GetAuthInfoAsync));
        return Task.FromResult(Auth);
    }

    public Task<BroadcastInfo> CreateBroadcastAsync(CreateBroadcastRequest request, CancellationToken ct = default)
    {
        Throw(nameof(CreateBroadcastAsync));
        if (CreateFailure?.Invoke(request) is { } createFailure) throw createFailure;
        CreateCalls++;
        LastCreateRequest = request;
        CreatedTitles.Add(request.Title);

        var id = $"bcast{++_counter}";
        return Task.FromResult(new BroadcastInfo(id, request.Title, "created", IYouTubeClient.WatchUrl(id)));
    }

    public Task BindStreamAsync(string broadcastId, string streamId, CancellationToken ct = default)
    {
        Throw(nameof(BindStreamAsync));
        if (BindFailure?.Invoke(broadcastId) is { } bindFailure) throw bindFailure;
        BindCalls++;
        Bindings.Add((broadcastId, streamId));
        return Task.CompletedTask;
    }

    public Task SetThumbnailAsync(
        string broadcastId, Stream image, string contentType, CancellationToken ct = default)
    {
        Throw(nameof(SetThumbnailAsync));
        if (ThumbnailFailure?.Invoke(broadcastId) is { } thumbnailFailure) throw thumbnailFailure;
        ThumbnailCalls++;
        ThumbnailIds.Add(broadcastId);
        return Task.CompletedTask;
    }

    public Task<string?> GetLifeCycleStatusAsync(string broadcastId, CancellationToken ct = default)
    {
        Throw(nameof(GetLifeCycleStatusAsync));
        return Task.FromResult<string?>(
            LifeCycleById.TryGetValue(broadcastId, out var status) ? status : LifeCycleStatus);
    }

    public Task TransitionToCompleteAsync(string broadcastId, CancellationToken ct = default)
    {
        Throw(nameof(TransitionToCompleteAsync));
        if (TransitionFailure?.Invoke(broadcastId) is { } transitionFailure) throw transitionFailure;
        TransitionCalls++;
        TransitionedIds.Add(broadcastId);
        return Task.CompletedTask;
    }

    public List<string> DeletedIds { get; } = new();

    public Task DeleteBroadcastAsync(string broadcastId, CancellationToken ct = default)
    {
        Throw(nameof(DeleteBroadcastAsync));
        DeletedIds.Add(broadcastId);
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<BroadcastInfo>> ListUnfinishedBroadcastsAsync(CancellationToken ct = default)
    {
        Throw(nameof(ListUnfinishedBroadcastsAsync));
        return Task.FromResult<IReadOnlyList<BroadcastInfo>>(Unfinished);
    }

    /// <summary>Titles of the reusable streams that were created, so the two slots can be told apart.</summary>
    public List<string> CreatedStreamTitles { get; } = new();

    public Task<StreamKeyInfo> CreateReusableStreamAsync(string title, CancellationToken ct = default)
    {
        Throw(nameof(CreateReusableStreamAsync));
        CreatedStreamTitles.Add(title);
        var id = $"stream-{CreatedStreamTitles.Count}";
        return Task.FromResult(new StreamKeyInfo(id, $"key-{CreatedStreamTitles.Count}",
            "rtmp://a.rtmp.youtube.com/live2"));
    }

    private void Throw(string operation)
    {
        if (!FailOnce.Remove(operation, out var exception)) return;
        throw exception;
    }
}

public sealed class FakeObsClient : IObsClient
{
    public int StartStreamCalls { get; private set; }
    public int StopStreamCalls { get; private set; }
    public List<string> ScenesSet { get; } = new();

    public Dictionary<string, Exception> FailOnce { get; } = new();

    public bool Connected { get; set; } = true;
    public bool Streaming { get; set; }
    public string? CurrentScene { get; set; } = "摄像机";
    public List<string> AvailableScenes { get; set; } = new() { "摄像机", "PPT" };
    public List<string> Inputs { get; set; } = new() { "ProFX" };
    public double? AudioPeak { get; set; } = 0.4;
    public Dictionary<string, bool?> SourceActive { get; } = new();

    public ObsStatus Status => new(Connected, Streaming, Streaming ? 120 : 0, CurrentScene, 0, Streaming ? 5000 : 0,
        AvailableScenes);

    /// <summary>Lets a test choose which OBS misconfiguration the pre-flight should describe.</summary>
    public ObsProblem ProblemToReport { get; set; } = ObsProblem.NotListening;

    public ObsProblem Problem => Connected ? ObsProblem.None : ProblemToReport;

    public Task SetSceneAsync(string sceneName, CancellationToken ct = default)
    {
        Throw(nameof(SetSceneAsync));
        ScenesSet.Add(sceneName);
        CurrentScene = sceneName;
        return Task.CompletedTask;
    }

    public Task StartStreamAsync(CancellationToken ct = default)
    {
        Throw(nameof(StartStreamAsync));
        StartStreamCalls++;
        Streaming = true;
        return Task.CompletedTask;
    }

    public Task StopStreamAsync(CancellationToken ct = default)
    {
        Throw(nameof(StopStreamAsync));
        StopStreamCalls++;
        Streaming = false;
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<string>> GetInputNamesAsync(CancellationToken ct = default)
    {
        Throw(nameof(GetInputNamesAsync));
        return Task.FromResult<IReadOnlyList<string>>(Inputs);
    }

    public double? GetRecentAudioPeak(string inputName, TimeSpan window) =>
        Inputs.Contains(inputName, StringComparer.OrdinalIgnoreCase) ? AudioPeak : null;

    public Task<bool?> IsSourceActiveAsync(string sourceName, CancellationToken ct = default) =>
        Task.FromResult(SourceActive.TryGetValue(sourceName, out var active) ? active : null);

    private void Throw(string operation)
    {
        if (!FailOnce.Remove(operation, out var exception)) return;
        throw exception;
    }
}

public sealed class FakeTelegramClient : ITelegramClient
{
    public List<string> Sent { get; } = new();
    public bool ShouldFail { get; set; }
    public LiveControlPanel.Core.Msg FailureMessage { get; set; } =
        new("发送失败：找不到该群。", "Send failed: group not found.");

    public Task<TelegramResult> SendAsync(
        string botToken, string chatId, string text, CancellationToken ct = default)
    {
        if (ShouldFail) return Task.FromResult(new TelegramResult(false, FailureMessage));

        Sent.Add(text);
        return Task.FromResult(new TelegramResult(true, new LiveControlPanel.Core.Msg("已发送。", "Sent.")));
    }
}

// ---------------------------------------------------------------------------- AI translation

/// <summary>
/// Windows audio, faked. Lets a test drive the exact sequence a real service produces — frames
/// arriving, a device vanishing mid-sermon — none of which is reproducible against a sound card.
/// </summary>
public sealed class FakeAudioEngine : IAudioEngine
{
    public List<AudioDeviceInfo> Capture { get; set; } = new()
    {
        new AudioDeviceInfo("mixer-1", "ProFX USB", IsDefault: true),
    };

    public List<AudioDeviceInfo> Playback { get; set; } = new()
    {
        new AudioDeviceInfo("cable-1", "CABLE Input (VB-Audio Virtual Cable)", IsDefault: false),
    };

    public Exception? OpenCaptureThrows { get; set; }
    public Exception? OpenSinkThrows { get; set; }

    public List<string?> CaptureRequests { get; } = new();
    public List<string?> SinkRequests { get; } = new();

    public FakeAudioCapture? LastCapture { get; private set; }
    public FakeAudioSink? LastSink { get; private set; }

    public IReadOnlyList<AudioDeviceInfo> CaptureDevices() => Capture;

    public IReadOnlyList<AudioDeviceInfo> PlaybackDevices() => Playback;

    public IAudioCapture OpenCapture(string? deviceId)
    {
        CaptureRequests.Add(deviceId);
        if (OpenCaptureThrows is { } ex) throw ex;
        return LastCapture = new FakeAudioCapture();
    }

    public IAudioSink OpenSink(string? deviceId)
    {
        SinkRequests.Add(deviceId);
        if (OpenSinkThrows is { } ex) throw ex;
        return LastSink = new FakeAudioSink();
    }
}

public sealed class FakeAudioCapture : IAudioCapture
{
    public event Action<byte[]>? FrameReady;
    public event Action<Exception>? Faulted;

    public bool Started { get; private set; }
    public bool Disposed { get; private set; }
    public double LastPeak { get; set; }

    public void Start() => Started = true;

    public void Stop() => Started = false;

    /// <summary>Hands one frame to whoever is listening, as WASAPI's callback would.</summary>
    public void Emit(byte[] frame) => FrameReady?.Invoke(frame);

    public void Fault(Exception ex) => Faulted?.Invoke(ex);

    public void Dispose() => Disposed = true;
}

public sealed class FakeAudioSink : IAudioSink
{
    private readonly object _gate = new();
    private readonly List<byte[]> _written = new();

    public bool Started { get; private set; }
    public bool Disposed { get; private set; }
    public double LastPeak { get; set; }
    public int BufferedMilliseconds { get; set; }

    public IReadOnlyList<byte[]> Written
    {
        get { lock (_gate) return _written.ToList(); }
    }

    public void Start() => Started = true;

    public void Stop() => Started = false;

    public void Write(byte[] pcm24kMono)
    {
        lock (_gate) _written.Add(pcm24kMono);
    }

    public void Dispose() => Disposed = true;
}

/// <summary>
/// Hands out fake sessions and records what each attempt was asked for, so reconnection behaviour
/// can be asserted attempt by attempt.
/// </summary>
public sealed class FakeGeminiSessionFactory : IGeminiSessionFactory
{
    private readonly object _gate = new();
    private readonly List<FakeGeminiSession> _sessions = new();

    /// <summary>Given the zero-based attempt number, the failure to raise from ConnectAsync.</summary>
    public Func<int, Exception?>? ConnectFailure { get; set; }

    public IReadOnlyList<FakeGeminiSession> Sessions
    {
        get { lock (_gate) return _sessions.ToList(); }
    }

    public IGeminiSession Create(string apiKey, GeminiSessionOptions options)
    {
        lock (_gate)
        {
            var attempt = _sessions.Count;
            var session = new FakeGeminiSession(apiKey, options, ConnectFailure?.Invoke(attempt));
            _sessions.Add(session);
            return session;
        }
    }
}

public sealed class FakeGeminiSession : IGeminiSession
{
    private readonly Channel<GeminiMessage> _inbound =
        Channel.CreateUnbounded<GeminiMessage>(new UnboundedChannelOptions { SingleReader = true });

    private readonly Exception? _connectFailure;
    private readonly object _gate = new();
    private readonly List<byte[]> _sent = new();

    public FakeGeminiSession(string apiKey, GeminiSessionOptions options, Exception? connectFailure)
    {
        ApiKey = apiKey;
        Options = options;
        _connectFailure = connectFailure;
    }

    public string ApiKey { get; }
    public GeminiSessionOptions Options { get; }
    public bool Connected { get; private set; }
    public bool Disposed { get; private set; }

    public IReadOnlyList<byte[]> Sent
    {
        get { lock (_gate) return _sent.ToList(); }
    }

    public Task ConnectAsync(CancellationToken ct)
    {
        if (_connectFailure is not null) return Task.FromException(_connectFailure);
        Connected = true;
        return Task.CompletedTask;
    }

    public Task SendAudioAsync(byte[] pcm16kMono, CancellationToken ct)
    {
        lock (_gate) _sent.Add(pcm16kMono);
        return Task.CompletedTask;
    }

    public async Task ReceiveLoopAsync(Action<GeminiMessage> onMessage, CancellationToken ct)
    {
        try
        {
            await foreach (var message in _inbound.Reader.ReadAllAsync(ct)) onMessage(message);
        }
        catch (OperationCanceledException)
        {
            // The service cancels the loop when it tears a session down; that is not a failure.
        }
    }

    /// <summary>Delivers a server frame, as the real socket would.</summary>
    public void Push(GeminiMessage message) => _inbound.Writer.TryWrite(message);

    /// <summary>Ends the receive loop cleanly, as a server-side close would.</summary>
    public void Close() => _inbound.Writer.TryComplete();

    public ValueTask DisposeAsync()
    {
        Disposed = true;
        _inbound.Writer.TryComplete();
        return ValueTask.CompletedTask;
    }
}

/// <summary>
/// Stands in for the translator in orchestration tests. The point of most of those assertions is
/// that a translator which refuses to start does not stop a service going on air.
/// </summary>
public sealed class FakeTranslationService : ITranslationService
{
    public List<string> StartedLanguages { get; } = new();
    public int StopCalls { get; private set; }

    /// <summary>Returned from StartAsync — non-null means "did not come up", as a warning.</summary>
    public Core.Msg? Problem { get; set; }

    public Exception? StartThrows { get; set; }

    public bool IsRunning { get; private set; }

    /// <summary>Settable, so the reconciler's exemption for a smoke test can be exercised.</summary>
    public bool IsTesting { get; set; }

    public Task<Core.Msg?> StartAsync(string targetLanguage, CancellationToken ct = default)
    {
        if (StartThrows is { } ex) throw ex;

        StartedLanguages.Add(targetLanguage);
        IsRunning = Problem is null;
        return Task.FromResult(Problem);
    }

    public Task StopAsync()
    {
        StopCalls++;
        IsRunning = false;
        return Task.CompletedTask;
    }

    public Task<TranslationTestReport> TestAsync(TimeSpan duration, CancellationToken ct = default) =>
        Task.FromResult(new TranslationTestReport(
            true, new Core.Msg("正常。", "Fine."), true, true, true, null, null));
}
