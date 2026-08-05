using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using LiveControlPanel.Config;

namespace LiveControlPanel.Obs;

/// <summary>
/// obs-websocket v5 client over a raw WebSocket.
///
/// Hand-rolled rather than using obs-websocket-dotnet because that library omits
/// "eventSubscriptions" from its Identify payload, leaving OBS on the default mask — which
/// excludes the high-volume InputVolumeMeters event that FR 4.4's audio check needs.
///
/// Runs a permanent connect/read loop with exponential backoff so the service tolerates OBS not
/// being started yet (FR 8, M3.1), and never lets a dead connection block a caller for long.
/// </summary>
public sealed class ObsClient : IObsClient, IAsyncDisposable
{
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(6);
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan MaxBackoff = TimeSpan.FromSeconds(30);

    private readonly ConfigStore _config;
    private readonly ILogger<ObsClient> _log;
    private readonly CancellationTokenSource _shutdown = new();

    private readonly ConcurrentDictionary<string, TaskCompletionSource<JsonNode?>> _pending = new();
    private readonly ConcurrentDictionary<string, (double Peak, DateTime At)> _audioPeaks = new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim _sendGate = new(1, 1);

    private ClientWebSocket? _socket;
    private volatile bool _identified;
    private long _requestCounter;

    // Bitrate is not reported by the protocol; derive it from the BytesSent delta.
    private long _lastBytesSent;
    private DateTime _lastBytesAt;

    private volatile ObsStatus _status = new(false, false, 0, null, 0, 0, Array.Empty<string>());

    public ObsClient(ConfigStore config, ILogger<ObsClient> log)
    {
        _config = config;
        _log = log;
    }

    public ObsStatus Status => _status;

    /// <summary>Raised whenever the published status changes, so the state manager can push it.</summary>
    public event Action<ObsStatus>? StatusChanged;

    public void Start()
    {
        _ = Task.Run(() => ConnectLoopAsync(_shutdown.Token));
        _ = Task.Run(() => PollLoopAsync(_shutdown.Token));
    }

    // ---------------------------------------------------------------- commands

    public async Task SetSceneAsync(string sceneName, CancellationToken ct = default) =>
        await RequestAsync("SetCurrentProgramScene",
            new JsonObject { ["sceneName"] = sceneName }, ct).ConfigureAwait(false);

    public async Task StartStreamAsync(CancellationToken ct = default) =>
        await RequestAsync("StartStream", null, ct).ConfigureAwait(false);

    public async Task StopStreamAsync(CancellationToken ct = default) =>
        await RequestAsync("StopStream", null, ct).ConfigureAwait(false);

    public async Task<IReadOnlyList<string>> GetInputNamesAsync(CancellationToken ct = default)
    {
        var data = await RequestAsync("GetInputList", null, ct).ConfigureAwait(false);
        var inputs = data?["inputs"]?.AsArray();
        if (inputs is null) return Array.Empty<string>();

        return inputs
            .Select(i => i?["inputName"]?.GetValue<string>())
            .Where(n => !string.IsNullOrEmpty(n))
            .Select(n => n!)
            .ToList();
    }

    public double? GetRecentAudioPeak(string inputName, TimeSpan window)
    {
        if (string.IsNullOrWhiteSpace(inputName)) return null;
        if (!_audioPeaks.TryGetValue(inputName, out var sample)) return null;
        if (DateTime.UtcNow - sample.At > window) return null;
        return sample.Peak;
    }

    public async Task<bool?> IsSourceActiveAsync(string sourceName, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(sourceName)) return null;
        try
        {
            var data = await RequestAsync("GetSourceActive",
                new JsonObject { ["sourceName"] = sourceName }, ct).ConfigureAwait(false);
            return data?["videoActive"]?.GetValue<bool>();
        }
        catch (Exception)
        {
            return null;
        }
    }

    // ---------------------------------------------------------------- request plumbing

    private async Task<JsonNode?> RequestAsync(string requestType, JsonObject? requestData, CancellationToken ct)
    {
        var socket = _socket;
        if (!_identified || socket is null || socket.State != WebSocketState.Open)
            throw new ObsUnavailableException();

        var id = Interlocked.Increment(ref _requestCounter).ToString();
        var tcs = new TaskCompletionSource<JsonNode?>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = tcs;

        try
        {
            var payload = new JsonObject
            {
                ["op"] = ObsOp.Request,
                ["d"] = new JsonObject
                {
                    ["requestType"] = requestType,
                    ["requestId"] = id,
                    ["requestData"] = requestData,
                },
            };

            await SendAsync(socket, payload, ct).ConfigureAwait(false);

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct, _shutdown.Token);
            timeout.CancelAfter(RequestTimeout);
            using var reg = timeout.Token.Register(() => tcs.TrySetException(new ObsUnavailableException()));

            return await tcs.Task.ConfigureAwait(false);
        }
        finally
        {
            _pending.TryRemove(id, out _);
        }
    }

    private async Task SendAsync(ClientWebSocket socket, JsonNode payload, CancellationToken ct)
    {
        var bytes = Encoding.UTF8.GetBytes(payload.ToJsonString());
        await _sendGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await socket.SendAsync(bytes, WebSocketMessageType.Text, true, ct).ConfigureAwait(false);
        }
        finally
        {
            _sendGate.Release();
        }
    }

    // ---------------------------------------------------------------- connect / read loop

    private async Task ConnectLoopAsync(CancellationToken ct)
    {
        var backoff = TimeSpan.FromSeconds(1);

        while (!ct.IsCancellationRequested)
        {
            var url = _config.Settings.Obs.Url;
            try
            {
                using var socket = new ClientWebSocket();
                await socket.ConnectAsync(new Uri(url), ct).ConfigureAwait(false);
                _socket = socket;
                _log.LogInformation("OBS websocket connected to {Url}", url);
                backoff = TimeSpan.FromSeconds(1);

                await ReadLoopAsync(socket, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                // Expected while OBS is not running. Debug level keeps the log readable.
                _log.LogDebug(ex, "OBS websocket connect/read failed for {Url}", url);
            }
            finally
            {
                _identified = false;
                _socket = null;
                FailAllPending();
                Publish(_status with
                {
                    Connected = false, Streaming = false, StreamTimeSeconds = 0,
                    KbitsPerSec = 0, DroppedFramesPercent = 0,
                });
            }

            try { await Task.Delay(backoff, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }

            backoff = TimeSpan.FromTicks(Math.Min(backoff.Ticks * 2, MaxBackoff.Ticks));
        }
    }

    private async Task ReadLoopAsync(ClientWebSocket socket, CancellationToken ct)
    {
        var buffer = new byte[64 * 1024];
        var accumulated = new MemoryStream();

        while (socket.State == WebSocketState.Open && !ct.IsCancellationRequested)
        {
            var result = await socket.ReceiveAsync(buffer, ct).ConfigureAwait(false);
            if (result.MessageType == WebSocketMessageType.Close) return;

            accumulated.Write(buffer, 0, result.Count);
            if (!result.EndOfMessage) continue;

            var json = Encoding.UTF8.GetString(accumulated.ToArray());
            accumulated.SetLength(0);

            try { await HandleMessageAsync(socket, json, ct).ConfigureAwait(false); }
            catch (Exception ex) { _log.LogWarning(ex, "Failed to handle OBS message"); }
        }
    }

    private async Task HandleMessageAsync(ClientWebSocket socket, string json, CancellationToken ct)
    {
        var root = JsonNode.Parse(json)?.AsObject();
        if (root is null) return;

        var op = root["op"]?.GetValue<int>();
        var d = root["d"];

        switch (op)
        {
            case ObsOp.Hello:
                await SendIdentifyAsync(socket, d, ct).ConfigureAwait(false);
                break;

            case ObsOp.Identified:
                _identified = true;
                await OnIdentifiedAsync(ct).ConfigureAwait(false);
                break;

            case ObsOp.RequestResponse:
                CompleteRequest(d);
                break;

            case ObsOp.Event:
                HandleEvent(d);
                break;
        }
    }

    private async Task SendIdentifyAsync(ClientWebSocket socket, JsonNode? hello, CancellationToken ct)
    {
        var identify = new JsonObject
        {
            ["rpcVersion"] = 1,
            // All standard events plus the high-volume volume meters, which the default mask omits.
            ["eventSubscriptions"] = (int)(ObsEventSubscription.All | ObsEventSubscription.InputVolumeMeters),
        };

        var auth = hello?["authentication"];
        if (auth is not null)
        {
            var salt = auth["salt"]?.GetValue<string>() ?? "";
            var challenge = auth["challenge"]?.GetValue<string>() ?? "";
            identify["authentication"] =
                ObsAuth.BuildAuthentication(_config.Settings.Obs.Password ?? "", salt, challenge);
        }

        await SendAsync(socket, new JsonObject { ["op"] = ObsOp.Identify, ["d"] = identify }, ct)
            .ConfigureAwait(false);
    }

    private async Task OnIdentifiedAsync(CancellationToken ct)
    {
        _lastBytesSent = 0;
        _lastBytesAt = default;
        await RefreshStatusAsync(ct).ConfigureAwait(false);
    }

    private void CompleteRequest(JsonNode? d)
    {
        var id = d?["requestId"]?.GetValue<string>();
        if (id is null || !_pending.TryRemove(id, out var tcs)) return;

        var ok = d?["requestStatus"]?["result"]?.GetValue<bool>() ?? false;
        if (ok)
        {
            tcs.TrySetResult(d?["responseData"]);
        }
        else
        {
            var comment = d?["requestStatus"]?["comment"]?.GetValue<string>();
            var code = d?["requestStatus"]?["code"]?.GetValue<int>();
            tcs.TrySetException(new ObsRequestException(comment ?? $"OBS request failed (code {code})"));
        }
    }

    private void HandleEvent(JsonNode? d)
    {
        var type = d?["eventType"]?.GetValue<string>();
        var data = d?["eventData"];

        switch (type)
        {
            case "InputVolumeMeters":
                RecordVolumeMeters(data);
                break;

            case "CurrentProgramSceneChanged":
                Publish(_status with { CurrentScene = data?["sceneName"]?.GetValue<string>() });
                break;

            case "StreamStateChanged":
                var active = data?["outputActive"]?.GetValue<bool>() ?? false;
                Publish(_status with { Streaming = active });
                break;
        }
    }

    /// <summary>
    /// InputVolumeMeters carries, per input, a list of channels each holding
    /// [magnitude, peak, inputPeak] in mul (0..1). We keep the largest peak seen per input.
    /// </summary>
    private void RecordVolumeMeters(JsonNode? data)
    {
        var inputs = data?["inputs"]?.AsArray();
        if (inputs is null) return;

        foreach (var input in inputs)
        {
            var name = input?["inputName"]?.GetValue<string>();
            if (string.IsNullOrEmpty(name)) continue;

            var peak = 0d;
            var channels = input?["inputLevelsMul"]?.AsArray();
            if (channels is not null)
            {
                foreach (var channel in channels)
                {
                    if (channel is not JsonArray values) continue;
                    foreach (var value in values)
                    {
                        if (value is null) continue;
                        try { peak = Math.Max(peak, Math.Abs(value.GetValue<double>())); }
                        catch (Exception) { /* unexpected shape: ignore this reading */ }
                    }
                }
            }

            _audioPeaks[name] = (peak, DateTime.UtcNow);
        }
    }

    // ---------------------------------------------------------------- status polling

    private async Task PollLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try { await Task.Delay(PollInterval, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }

            if (!_identified) continue;
            try { await RefreshStatusAsync(ct).ConfigureAwait(false); }
            catch (Exception ex) { _log.LogDebug(ex, "OBS status refresh failed"); }
        }
    }

    private async Task RefreshStatusAsync(CancellationToken ct)
    {
        var stream = await RequestAsync("GetStreamStatus", null, ct).ConfigureAwait(false);
        var sceneList = await RequestAsync("GetSceneList", null, ct).ConfigureAwait(false);

        var streaming = stream?["outputActive"]?.GetValue<bool>() ?? false;
        var durationMs = stream?["outputDuration"]?.GetValue<double>() ?? 0;
        var totalFrames = stream?["outputTotalFrames"]?.GetValue<double>() ?? 0;
        var skippedFrames = stream?["outputSkippedFrames"]?.GetValue<double>() ?? 0;
        var bytes = (long)(stream?["outputBytes"]?.GetValue<double>() ?? 0);

        var dropped = totalFrames > 0 ? Math.Round(skippedFrames / totalFrames * 100, 2) : 0;

        var now = DateTime.UtcNow;
        var kbits = _status.KbitsPerSec;
        if (_lastBytesAt != default && bytes >= _lastBytesSent)
        {
            var seconds = (now - _lastBytesAt).TotalSeconds;
            if (seconds > 0.5) kbits = (long)((bytes - _lastBytesSent) * 8 / 1000 / seconds);
        }
        _lastBytesSent = bytes;
        _lastBytesAt = now;
        if (!streaming) kbits = 0;

        var scenes = sceneList?["scenes"]?.AsArray()
            .Select(s => s?["sceneName"]?.GetValue<string>())
            .Where(n => !string.IsNullOrEmpty(n))
            .Select(n => n!)
            .Reverse()   // OBS returns scenes bottom-up; the UI reads top-down.
            .ToList() ?? new List<string>();

        Publish(new ObsStatus(
            Connected: true,
            Streaming: streaming,
            StreamTimeSeconds: (long)(durationMs / 1000),
            CurrentScene: sceneList?["currentProgramSceneName"]?.GetValue<string>(),
            DroppedFramesPercent: dropped,
            KbitsPerSec: kbits,
            Scenes: scenes));
    }

    private void Publish(ObsStatus status)
    {
        var previous = _status;
        _status = status;

        if (previous.Connected == status.Connected &&
            previous.Streaming == status.Streaming &&
            previous.StreamTimeSeconds == status.StreamTimeSeconds &&
            previous.CurrentScene == status.CurrentScene &&
            Math.Abs(previous.DroppedFramesPercent - status.DroppedFramesPercent) < 0.001 &&
            previous.KbitsPerSec == status.KbitsPerSec &&
            previous.Scenes.SequenceEqual(status.Scenes))
        {
            return;
        }

        StatusChanged?.Invoke(status);
    }

    private void FailAllPending()
    {
        foreach (var key in _pending.Keys.ToList())
        {
            if (_pending.TryRemove(key, out var tcs)) tcs.TrySetException(new ObsUnavailableException());
        }
    }

    public async ValueTask DisposeAsync()
    {
        _shutdown.Cancel();
        var socket = _socket;
        if (socket is not null && socket.State == WebSocketState.Open)
        {
            try
            {
                await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "shutdown", CancellationToken.None)
                    .ConfigureAwait(false);
            }
            catch (Exception) { /* shutting down anyway */ }
        }
        _shutdown.Dispose();
        _sendGate.Dispose();
    }
}

/// <summary>OBS is not reachable. Distinct from a rejected request so the UI can say "open OBS".</summary>
public sealed class ObsUnavailableException : Exception
{
    public ObsUnavailableException() : base("OBS is not connected.") { }
}

public sealed class ObsRequestException : Exception
{
    public ObsRequestException(string message) : base(message) { }
}
