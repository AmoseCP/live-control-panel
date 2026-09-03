using LiveControlPanel.Devices;
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

    /// <summary>
    /// Successive frames per source. The pre-flight takes two samples, so a two-entry list decides
    /// whether that source looks frozen; a shorter list repeats its last entry.
    /// </summary>
    public Dictionary<string, List<byte[]?>> Frames { get; } = new(StringComparer.Ordinal);

    private readonly Dictionary<string, int> _frameReads = new(StringComparer.Ordinal);

    public List<string> RefreshedInputs { get; } = new();

    /// <summary>A source whose two samples are identical — a wedged card or a No Signal screen.</summary>
    public void WithFrozenSource(string name, bool active = true)
    {
        SourceActive[name] = active;
        Frames[name] = new List<byte[]?> { new byte[] { 1, 2, 3, 4 } };
    }

    /// <summary>A source whose samples differ — a live camera, whose sensor noise guarantees it.</summary>
    public void WithMovingSource(string name, bool active = true)
    {
        SourceActive[name] = active;
        Frames[name] = new List<byte[]?> { new byte[] { 1, 2, 3, 4 }, new byte[] { 9, 8, 7, 6 } };
    }

    /// <summary>A source OBS will not render — the check must treat this as "cannot tell".</summary>
    public void WithUnrenderableSource(string name, bool active = true)
    {
        SourceActive[name] = active;
        Frames[name] = new List<byte[]?> { null };
    }

    public Task<byte[]?> GetSourceFrameAsync(string sourceName, CancellationToken ct = default)
    {
        Throw(nameof(GetSourceFrameAsync));

        if (!Frames.TryGetValue(sourceName, out var samples) || samples.Count == 0)
            return Task.FromResult<byte[]?>(null);

        var index = _frameReads.TryGetValue(sourceName, out var read) ? read : 0;
        _frameReads[sourceName] = index + 1;

        return Task.FromResult(samples[Math.Min(index, samples.Count - 1)]);
    }

    public Task RefreshInputAsync(string inputName, CancellationToken ct = default)
    {
        Throw(nameof(RefreshInputAsync));
        RefreshedInputs.Add(inputName);
        return Task.CompletedTask;
    }

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


// ---------------------------------------------------------------------------- capture-card recovery

/// <summary>
/// Stands in for Device Manager. The cases worth testing are all failure cases — above all the one
/// where the device is switched off and will not come back on, which is worse than the fault being
/// recovered from and must never be reported as a success.
/// </summary>
public sealed class FakeDeviceResetter : IDeviceResetter
{
    public const string CaptureCardId = @"USB\VID_07CA&PID_0570\5&1234ABCD&0&4";

    public List<PnpDeviceInfo> Devices { get; set; } = new()
    {
        new PnpDeviceInfo(CaptureCardId, "AVerMedia HDMI Capture", "Camera", "OK"),
    };

    public List<string> Disabled { get; } = new();
    public List<string> Enabled { get; } = new();

    public Exception? DisableThrows { get; set; }

    /// <summary>Given the 1-based attempt number, the failure to raise from Enable.</summary>
    public Func<int, Exception?>? EnableFailure { get; set; }

    public Task<IReadOnlyList<PnpDeviceInfo>> ListCandidatesAsync(CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<PnpDeviceInfo>>(Devices);

    public Task<PnpDeviceInfo?> FindAsync(string instanceId, CancellationToken ct = default) =>
        Task.FromResult(Devices.FirstOrDefault(d => d.InstanceId == instanceId));

    public Task DisableAsync(string instanceId, CancellationToken ct = default)
    {
        if (DisableThrows is { } ex) throw ex;
        Disabled.Add(instanceId);
        return Task.CompletedTask;
    }

    /// <summary>How many times Enable was called, successful or not.</summary>
    public int EnableAttempts { get; private set; }

    public Task EnableAsync(string instanceId, CancellationToken ct = default)
    {
        EnableAttempts++;
        if (EnableFailure?.Invoke(EnableAttempts) is { } ex) throw ex;

        Enabled.Add(instanceId);
        return Task.CompletedTask;
    }
}
