using System;
using System.Net.WebSockets;
using System.Threading;
using System.Threading.Tasks;
using DemiCatPlugin;
using Xunit;

public class UiRendererReceiveLoopTests
{
    private sealed class StubWebSocket : WebSocket
    {
        public WebSocketState ReportedState { get; set; }
        public bool CloseOutputCalled { get; private set; }
        public Exception? ExceptionToThrow { get; set; }

        public override WebSocketCloseStatus? CloseStatus => null;
        public override string? CloseStatusDescription => null;
        public override WebSocketState State => ReportedState;
        public override string? SubProtocol => null;

        public override void Abort()
        {
        }

        public override Task CloseAsync(WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken)
            => Task.CompletedTask;

        public override Task CloseOutputAsync(WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken)
        {
            CloseOutputCalled = true;
            if (ExceptionToThrow != null)
            {
                throw ExceptionToThrow;
            }

            return Task.CompletedTask;
        }

        public override void Dispose()
        {
        }

        public override Task<WebSocketReceiveResult> ReceiveAsync(ArraySegment<byte> buffer, CancellationToken cancellationToken)
            => Task.FromResult(new WebSocketReceiveResult(0, WebSocketMessageType.Close, true));

        public override Task SendAsync(ArraySegment<byte> buffer, WebSocketMessageType messageType, bool endOfMessage, CancellationToken cancellationToken)
            => Task.CompletedTask;
    }

    [Theory]
    [InlineData(WebSocketState.Open)]
    [InlineData(WebSocketState.CloseReceived)]
    public async Task CloseWebSocketGracefully_AttemptsCloseWhenHandshakeRequired(WebSocketState state)
    {
        var socket = new StubWebSocket { ReportedState = state };

        await UiRenderer.CloseWebSocketGracefully(socket, CancellationToken.None);

        Assert.True(socket.CloseOutputCalled);
    }

    [Fact]
    public async Task CloseWebSocketGracefully_DoesNothingWhenAlreadyClosed()
    {
        var socket = new StubWebSocket { ReportedState = WebSocketState.Closed };

        await UiRenderer.CloseWebSocketGracefully(socket, CancellationToken.None);

        Assert.False(socket.CloseOutputCalled);
    }

    [Fact]
    public async Task CloseWebSocketGracefully_SwallowsWebSocketException()
    {
        var socket = new StubWebSocket
        {
            ReportedState = WebSocketState.CloseReceived,
            ExceptionToThrow = new WebSocketException("already closed")
        };

        await UiRenderer.CloseWebSocketGracefully(socket, CancellationToken.None);

        Assert.True(socket.CloseOutputCalled);
    }
}
