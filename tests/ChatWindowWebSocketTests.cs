using System;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Threading;
using System.Threading.Tasks;
using DemiCatPlugin;
using Dalamud.Plugin.Services;
using Xunit;
using System.Net.Http;

public class ChatWindowWebSocketTests
{
    [Fact]
    public async Task RunWebSocket_RetriesAfterDrop()
    {
        using var server = new MockWsServer();
        SetupServices();
        var config = new Config { EnableFcChat = true };
        using var client = new HttpClient();
        var chat = new TestChatWindow(config, client, server.Uri);
        chat.StartNetworking();

        await WaitUntil(() => server.ConnectionCount >= 1, TimeSpan.FromSeconds(5));
        await WaitUntil(() => server.ConnectionCount >= 2, TimeSpan.FromSeconds(10));
        await WaitUntil(() => chat.StatusMessage == string.Empty, TimeSpan.FromSeconds(5));

        chat.StopNetworking();
    }

    private static async Task WaitUntil(Func<bool> cond, TimeSpan timeout)
    {
        var start = DateTime.UtcNow;
        while (!cond())
        {
            if (DateTime.UtcNow - start > timeout)
                throw new TimeoutException();
            await Task.Delay(50);
        }
    }

    private static void SetupServices()
    {
        var ps = new PluginServices();
        var framework = new TestFramework();
        var log = new TestLog();
        typeof(PluginServices).GetProperty("Framework", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.SetValue(ps, framework);
        typeof(PluginServices).GetProperty("Log", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.SetValue(ps, log);
    }

    private class TestFramework : IFramework
    {
        public event FrameworkUpdateDelegate? Update { add { } remove { } }
        public FrameworkUpdateType CurrentUpdateType => FrameworkUpdateType.None;
        public void RunOnTick(Action action, FrameworkUpdatePriority priority = FrameworkUpdatePriority.Normal) => action();
    }

    private class TestLog : IPluginLog
    {
        public void Verbose(string message) { }
        public void Verbose(string message, Exception exception) { }
        public void Debug(string message) { }
        public void Debug(string message, Exception exception) { }
        public void Info(string message) { }
        public void Info(string message, Exception exception) { }
        public void Warning(string message) { }
        public void Warning(string message, Exception exception) { }
        public void Error(string message) { }
        public void Error(Exception exception, string message) { }
        public void Fatal(string message) { }
        public void Fatal(string message, Exception exception) { }
    }

    private class TestChatWindow : ChatWindow
    {
        private readonly Uri _uri;
        public TestChatWindow(Config config, HttpClient httpClient, Uri uri) : base(config, httpClient, null, new TokenManager())
        {
            _uri = uri;
        }
        protected override Uri BuildWebSocketUri() => _uri;
        public string StatusMessage => _statusMessage;
    }

    private class MockWsServer : IDisposable
    {
        private readonly HttpListener _listener;
        private readonly CancellationTokenSource _cts = new();
        private int _connections;
        public int ConnectionCount => _connections;
        public Uri Uri { get; }
        public MockWsServer()
        {
            var port = GetFreePort();
            _listener = new HttpListener();
            _listener.Prefixes.Add($"http://localhost:{port}/ws/messages/");
            _listener.Start();
            Uri = new Uri($"ws://localhost:{port}/ws/messages");
            _ = Task.Run(AcceptLoop);
        }
        private async Task AcceptLoop()
        {
            while (!_cts.Token.IsCancellationRequested)
            {
                var ctx = await _listener.GetContextAsync();
                if (!ctx.Request.IsWebSocketRequest)
                {
                    ctx.Response.StatusCode = 400;
                    ctx.Response.Close();
                    continue;
                }
                var wsCtx = await ctx.AcceptWebSocketAsync(null);
                Interlocked.Increment(ref _connections);
                if (_connections == 1)
                {
                    await Task.Delay(100);
                    await wsCtx.WebSocket.CloseAsync(WebSocketCloseStatus.NormalClosure, "bye", CancellationToken.None);
                }
                else
                {
                    while (!_cts.Token.IsCancellationRequested && wsCtx.WebSocket.State == WebSocketState.Open)
                    {
                        await Task.Delay(50);
                    }
                }
            }
        }
        private static int GetFreePort()
        {
            var l = new TcpListener(IPAddress.Loopback, 0);
            l.Start();
            var p = ((IPEndPoint)l.LocalEndpoint).Port;
            l.Stop();
            return p;
        }
        public void Dispose()
        {
            _cts.Cancel();
            _listener.Stop();
        }
    }
}
