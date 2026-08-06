using System.Net.WebSockets;
using System.Text;
using LiveControlPanel.Core;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace LiveControlPanel.Tests;

/// <summary>
/// The hub must deliver states to a client in the order they were produced. The previous
/// implementation queued a fire-and-forget task per broadcast behind a semaphore; the tasks could
/// reach the semaphore out of order, so during the start orchestration's rapid pushes an older state
/// could overwrite a newer one on the page.
/// </summary>
public class StateHubTests
{
    /// <summary>
    /// A socket that records what was sent and deliberately stalls the first broadcast — under the
    /// old implementation that reliably let later sends overtake it.
    /// </summary>
    private sealed class RecordingSocket : WebSocket
    {
        private readonly List<string> _sent = new();
        private int _sends;

        public IReadOnlyList<string> Snapshot() { lock (_sent) return _sent.ToList(); }

        public override WebSocketCloseStatus? CloseStatus => null;
        public override string? CloseStatusDescription => null;
        public override WebSocketState State => WebSocketState.Open;
        public override string? SubProtocol => null;
        public override void Abort() { }
        public override void Dispose() { }

        public override Task CloseAsync(
            WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken) =>
            Task.CompletedTask;

        public override Task CloseOutputAsync(
            WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken) =>
            Task.CompletedTask;

        public override async Task<WebSocketReceiveResult> ReceiveAsync(
            ArraySegment<byte> buffer, CancellationToken cancellationToken)
        {
            // The hub only reads to notice disconnection; block until the test cancels.
            await Task.Delay(Timeout.Infinite, cancellationToken);
            throw new OperationCanceledException(cancellationToken);
        }

        public override async Task SendAsync(
            ArraySegment<byte> buffer, WebSocketMessageType messageType, bool endOfMessage,
            CancellationToken cancellationToken)
        {
            // Send #1 is the initial snapshot; #2 is the first Broadcast — stall that one.
            if (Interlocked.Increment(ref _sends) == 2) await Task.Delay(150);

            lock (_sent) _sent.Add(Encoding.UTF8.GetString(buffer.Array!, buffer.Offset, buffer.Count));
        }
    }

    [Fact]
    public async Task Broadcasts_reach_a_client_in_the_order_they_were_produced()
    {
        var hub = new StateHub(NullLogger<StateHub>.Instance);
        var socket = new RecordingSocket();
        using var cts = new CancellationTokenSource();

        var run = hub.RunClientAsync(socket, new RuntimeState(), cts.Token);
        for (var i = 0; i < 100 && hub.ClientCount == 0; i++) await Task.Delay(10);
        Assert.Equal(1, hub.ClientCount);

        for (var n = 1; n <= 5; n++)
            hub.Broadcast(new RuntimeState { Obs = new ObsState { KbitsPerSec = n } });

        for (var i = 0; i < 300 && socket.Snapshot().Count < 6; i++) await Task.Delay(10);

        var sent = socket.Snapshot();
        Assert.Equal(6, sent.Count);   // initial + five broadcasts, none lost

        // Despite broadcast #1 being artificially slow, nothing overtook it.
        for (var n = 1; n <= 5; n++)
            Assert.Contains("\"kbitsPerSec\": " + n, sent[n]);

        cts.Cancel();
        await run;
    }
}
