using System;
using System.Collections.Generic;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using DemiCatPlugin;
using Xunit;

public class ChannelWatcherTests
{
    private class StubWebSocket : WebSocket
    {
        private readonly Queue<ArraySegment<byte>> _segments;

        public StubWebSocket(string message, int chunkSize)
        {
            var bytes = Encoding.UTF8.GetBytes(message);
            _segments = new Queue<ArraySegment<byte>>();
            for (int i = 0; i < bytes.Length; i += chunkSize)
            {
                var len = Math.Min(chunkSize, bytes.Length - i);
                var chunk = new byte[len];
                Array.Copy(bytes, i, chunk, 0, len);
                _segments.Enqueue(new ArraySegment<byte>(chunk));
            }
        }

        public override WebSocketCloseStatus? CloseStatus => null;
        public override string? CloseStatusDescription => null;
        public override WebSocketState State => WebSocketState.Open;
        public override string? SubProtocol => null;

        public override void Abort() { }
        public override Task CloseAsync(WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken) => Task.CompletedTask;
        public override Task CloseOutputAsync(WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken) => Task.CompletedTask;
        public override void Dispose() { }

        public override Task<WebSocketReceiveResult> ReceiveAsync(ArraySegment<byte> buffer, CancellationToken cancellationToken)
        {
            if (_segments.Count == 0)
            {
                return Task.FromResult(new WebSocketReceiveResult(0, WebSocketMessageType.Close, true));
            }
            var segment = _segments.Dequeue();
            Array.Copy(segment.Array!, segment.Offset, buffer.Array!, buffer.Offset, segment.Count);
            bool end = _segments.Count == 0;
            return Task.FromResult(new WebSocketReceiveResult(segment.Count, WebSocketMessageType.Text, end));
        }

        public override Task SendAsync(ArraySegment<byte> buffer, WebSocketMessageType messageType, bool endOfMessage, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    [Fact]
    public async Task ReceiveMessageAsync_ReadsLargeMessages()
    {
        var message = new string('a', 5000);
        var ws = new StubWebSocket(message, 1000);
        var buffer = new byte[1024];
        var (received, type) = await ChannelWatcher.ReceiveMessageAsync(ws, buffer, CancellationToken.None);
        Assert.Equal(WebSocketMessageType.Text, type);
        Assert.Equal(message, received);
    }

    [Fact]
    public async Task ReceiveMessageAsync_HandlesMultiByteCharacters()
    {
        var message = "a🙂b";
        var ws = new StubWebSocket(message, 2);
        var buffer = new byte[3];
        var (received, type) = await ChannelWatcher.ReceiveMessageAsync(ws, buffer, CancellationToken.None);
        Assert.Equal(WebSocketMessageType.Text, type);
        Assert.Equal(message, received);
    }
}
