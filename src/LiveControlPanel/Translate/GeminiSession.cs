using System.Net.WebSockets;
using System.Text;

namespace LiveControlPanel.Translate;

/// <summary>
/// One live-translation conversation. An interface so the translator's lifecycle — start, health,
/// reconnect, stop — is testable without the network, which is the only part of this feature that
/// can be exercised on a machine with no sound card and no API key.
/// </summary>
public interface IGeminiSession : IAsyncDisposable
{
    /// <summary>Opens the socket, sends the setup frame and returns once the server accepts it.</summary>
    Task ConnectAsync(CancellationToken ct);

    Task SendAudioAsync(byte[] pcm16kMono, CancellationToken ct);

    /// <summary>Pumps server frames until the socket closes or <paramref name="ct"/> fires.</summary>
    Task ReceiveLoopAsync(Action<GeminiMessage> onMessage, CancellationToken ct);
}

public interface IGeminiSessionFactory
{
    IGeminiSession Create(string apiKey, GeminiSessionOptions options);
}

/// <summary>The Gemini Live API refused or dropped the session.</summary>
public sealed class GeminiSessionException : Exception
{
    public GeminiSessionException(string message, Exception? inner = null) : base(message, inner) { }
}

public sealed class GeminiWebSocketSessionFactory : IGeminiSessionFactory
{
    public IGeminiSession Create(string apiKey, GeminiSessionOptions options) =>
        new GeminiWebSocketSession(apiKey, options);
}

/// <summary>
/// BidiGenerateContent over a raw WebSocket.
///
/// Raw rather than an SDK for the same reason <c>ObsClient</c> is: the panel needs exact control of
/// the setup payload (the translation config is what the whole feature rests on) and of reconnection
/// timing, and it must not acquire a dependency that changes shape on a preview model's schedule.
/// </summary>
public sealed class GeminiWebSocketSession : IGeminiSession
{
    private const int ReceiveBufferBytes = 64 * 1024;

    /// <summary>Guards against a runaway frame eating memory on a long service.</summary>
    private const int MaxMessageBytes = 8 * 1024 * 1024;

    private static readonly TimeSpan SetupTimeout = TimeSpan.FromSeconds(20);

    private readonly string _apiKey;
    private readonly GeminiSessionOptions _options;
    private readonly ClientWebSocket _socket = new();
    private readonly SemaphoreSlim _sendGate = new(1, 1);

    public GeminiWebSocketSession(string apiKey, GeminiSessionOptions options)
    {
        _apiKey = apiKey;
        _options = options;
    }

    public async Task ConnectAsync(CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(_apiKey))
            throw new GeminiSessionException("No Gemini API key is configured.");

        await _socket.ConnectAsync(GeminiProtocol.Endpoint(_apiKey), ct).ConfigureAwait(false);
        await SendTextAsync(GeminiProtocol.BuildSetup(_options), ct).ConfigureAwait(false);

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(SetupTimeout);

        try
        {
            while (true)
            {
                var frame = await ReceiveTextAsync(deadline.Token).ConfigureAwait(false);
                if (frame is null)
                    throw new GeminiSessionException("The Gemini session closed before setup completed.");

                var message = GeminiProtocol.Parse(frame);
                if (message?.Error is { } error) throw new GeminiSessionException(error);
                if (message?.SetupComplete == true) return;
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new GeminiSessionException("The Gemini session did not confirm setup in time.");
        }
    }

    public Task SendAudioAsync(byte[] pcm16kMono, CancellationToken ct) =>
        SendTextAsync(GeminiProtocol.BuildAudioChunk(pcm16kMono), ct);

    public async Task ReceiveLoopAsync(Action<GeminiMessage> onMessage, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            var frame = await ReceiveTextAsync(ct).ConfigureAwait(false);
            if (frame is null) return;

            var message = GeminiProtocol.Parse(frame);
            if (message is not null) onMessage(message);
        }
    }

    private async Task SendTextAsync(string json, CancellationToken ct)
    {
        var bytes = Encoding.UTF8.GetBytes(json);

        await _sendGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_socket.State != WebSocketState.Open)
                throw new GeminiSessionException($"The Gemini session is {_socket.State}.");

            await _socket.SendAsync(bytes, WebSocketMessageType.Text, true, ct).ConfigureAwait(false);
        }
        finally
        {
            _sendGate.Release();
        }
    }

    /// <summary>Reassembles one logical message; null means the peer closed.</summary>
    private async Task<string?> ReceiveTextAsync(CancellationToken ct)
    {
        var buffer = new byte[ReceiveBufferBytes];
        using var assembled = new MemoryStream();

        while (true)
        {
            int count;
            bool endOfMessage;

            try
            {
                var result = await _socket.ReceiveAsync(buffer, ct).ConfigureAwait(false);
                if (result.MessageType == WebSocketMessageType.Close) return null;

                count = result.Count;
                endOfMessage = result.EndOfMessage;
            }
            catch (WebSocketException ex)
            {
                // Only the WebSocketErrorCode, not the message: the API key is in the endpoint's
                // query string, and this exception's text is the one place it could plausibly ride
                // along into a plaintext log kept for 31 days on a shared PC.
                throw new GeminiSessionException($"The Gemini session dropped ({ex.WebSocketErrorCode}).");
            }

            assembled.Write(buffer, 0, count);
            if (assembled.Length > MaxMessageBytes)
                throw new GeminiSessionException("The Gemini session sent an oversized frame.");

            if (endOfMessage) break;
        }

        return Encoding.UTF8.GetString(assembled.ToArray());
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            if (_socket.State == WebSocketState.Open)
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                await _socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", timeout.Token)
                    .ConfigureAwait(false);
            }
        }
        catch (Exception)
        {
            // Closing politely is a courtesy; the service must never be held up by it.
        }
        finally
        {
            _socket.Dispose();
            _sendGate.Dispose();
        }
    }
}
