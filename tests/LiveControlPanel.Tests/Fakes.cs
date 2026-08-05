using LiveControlPanel.Notify;
using LiveControlPanel.Obs;
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

    public List<BroadcastInfo> Unfinished { get; set; } = new();
    public string LifeCycleStatus { get; set; } = "live";
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
        CreateCalls++;
        LastCreateRequest = request;

        var id = $"bcast{++_counter}";
        return Task.FromResult(new BroadcastInfo(id, request.Title, "created", IYouTubeClient.WatchUrl(id)));
    }

    public Task BindStreamAsync(string broadcastId, string streamId, CancellationToken ct = default)
    {
        Throw(nameof(BindStreamAsync));
        BindCalls++;
        return Task.CompletedTask;
    }

    public Task SetThumbnailAsync(
        string broadcastId, Stream image, string contentType, CancellationToken ct = default)
    {
        Throw(nameof(SetThumbnailAsync));
        ThumbnailCalls++;
        return Task.CompletedTask;
    }

    public Task<string?> GetLifeCycleStatusAsync(string broadcastId, CancellationToken ct = default)
    {
        Throw(nameof(GetLifeCycleStatusAsync));
        return Task.FromResult<string?>(LifeCycleStatus);
    }

    public Task TransitionToCompleteAsync(string broadcastId, CancellationToken ct = default)
    {
        Throw(nameof(TransitionToCompleteAsync));
        TransitionCalls++;
        TransitionedIds.Add(broadcastId);
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<BroadcastInfo>> ListUnfinishedBroadcastsAsync(CancellationToken ct = default)
    {
        Throw(nameof(ListUnfinishedBroadcastsAsync));
        return Task.FromResult<IReadOnlyList<BroadcastInfo>>(Unfinished);
    }

    public Task<StreamKeyInfo> CreateReusableStreamAsync(string title, CancellationToken ct = default)
    {
        Throw(nameof(CreateReusableStreamAsync));
        return Task.FromResult(new StreamKeyInfo("stream-1", "key-1", "rtmp://a.rtmp.youtube.com/live2"));
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
    public string FailureMessage { get; set; } = "发送失败：找不到该群。";

    public Task<TelegramResult> SendAsync(
        string botToken, string chatId, string text, CancellationToken ct = default)
    {
        if (ShouldFail) return Task.FromResult(new TelegramResult(false, FailureMessage));

        Sent.Add(text);
        return Task.FromResult(new TelegramResult(true, "已发送。"));
    }
}
