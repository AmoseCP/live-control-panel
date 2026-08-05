using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using LiveControlPanel.Config;

namespace LiveControlPanel.Core;

/// <summary>
/// Server-push fan-out for <c>/ws</c>. FR 2.2: the page never polls, so every state change has to
/// arrive here. Dead sockets are dropped silently — an iPad that locked its screen simply
/// reconnects.
/// </summary>
public sealed class StateHub
{
    private readonly ConcurrentDictionary<Guid, Client> _clients = new();
    private readonly ILogger<StateHub> _log;

    public StateHub(ILogger<StateHub> log) => _log = log;

    public int ClientCount => _clients.Count;

    /// <summary>Registers a socket and blocks until the peer goes away.</summary>
    public async Task RunClientAsync(WebSocket socket, RuntimeState initial, CancellationToken ct)
    {
        var id = Guid.NewGuid();
        var client = new Client(socket);
        _clients[id] = client;

        try
        {
            await client.SendAsync(Serialize(initial), ct).ConfigureAwait(false);

            // The client sends nothing meaningful; reading just detects disconnection.
            var buffer = new byte[1024];
            while (socket.State == WebSocketState.Open && !ct.IsCancellationRequested)
            {
                var result = await socket.ReceiveAsync(buffer, ct).ConfigureAwait(false);
                if (result.MessageType == WebSocketMessageType.Close) break;
            }
        }
        catch (OperationCanceledException) { /* shutting down */ }
        catch (WebSocketException) { /* client vanished */ }
        finally
        {
            _clients.TryRemove(id, out _);
            client.Dispose();
        }
    }

    public void Broadcast(RuntimeState state)
    {
        if (_clients.IsEmpty) return;

        var payload = Serialize(state);
        foreach (var (id, client) in _clients.ToArray())
        {
            _ = Task.Run(async () =>
            {
                try
                {
                    await client.SendAsync(payload, CancellationToken.None).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _log.LogDebug(ex, "Dropping websocket client {Id}", id);
                    _clients.TryRemove(id, out _);
                }
            });
        }
    }

    private static byte[] Serialize(RuntimeState state) =>
        Encoding.UTF8.GetBytes(JsonSerializer.Serialize(state, Json.Options));

    /// <summary>
    /// One connected browser. The semaphore matters: a WebSocket permits only one send at a time, and
    /// OBS status polling plus an operator action can easily overlap.
    /// </summary>
    private sealed class Client : IDisposable
    {
        private readonly WebSocket _socket;
        private readonly SemaphoreSlim _sendGate = new(1, 1);

        public Client(WebSocket socket) => _socket = socket;

        public async Task SendAsync(byte[] payload, CancellationToken ct)
        {
            if (_socket.State != WebSocketState.Open) return;

            await _sendGate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                if (_socket.State != WebSocketState.Open) return;
                await _socket.SendAsync(payload, WebSocketMessageType.Text, true, ct).ConfigureAwait(false);
            }
            finally
            {
                _sendGate.Release();
            }
        }

        public void Dispose() => _sendGate.Dispose();
    }
}
