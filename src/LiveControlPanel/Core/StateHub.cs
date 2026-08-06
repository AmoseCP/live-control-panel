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
            await client.EnqueueSendAsync(Serialize(initial)).ConfigureAwait(false);

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
        }
    }

    public void Broadcast(RuntimeState state)
    {
        if (_clients.IsEmpty) return;

        var payload = Serialize(state);
        foreach (var (id, client) in _clients.ToArray())
        {
            client.EnqueueSendAsync(payload).ContinueWith(t =>
            {
                _log.LogDebug(t.Exception, "Dropping websocket client {Id}", id);
                _clients.TryRemove(id, out _);
            }, CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);
        }
    }

    private static byte[] Serialize(RuntimeState state) =>
        Encoding.UTF8.GetBytes(JsonSerializer.Serialize(state, Json.Options));

    /// <summary>
    /// One connected browser. Sends are chained, not merely serialized: the previous fire-and-forget
    /// tasks behind a semaphore could still reach it out of order, letting an older state overwrite a
    /// newer one on the page during the rapid step-by-step pushes of the start orchestration.
    /// </summary>
    private sealed class Client
    {
        private readonly WebSocket _socket;
        private readonly object _gate = new();
        private Task _chain = Task.CompletedTask;

        public Client(WebSocket socket) => _socket = socket;

        /// <summary>Queues a send after everything already queued. The returned task is this send only.</summary>
        public Task EnqueueSendAsync(byte[] payload)
        {
            lock (_gate)
            {
                var send = _chain
                    .ContinueWith(_ => SendCoreAsync(payload),
                        CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default)
                    .Unwrap();

                // The chain itself never faults: a failed send is the caller's signal to drop the
                // client, not a reason to wedge every later send behind an exception.
                _chain = send.ContinueWith(_ => { },
                    CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default);

                return send;
            }
        }

        private async Task SendCoreAsync(byte[] payload)
        {
            if (_socket.State != WebSocketState.Open) return;
            await _socket.SendAsync(payload, WebSocketMessageType.Text, true, CancellationToken.None)
                .ConfigureAwait(false);
        }
    }
}
