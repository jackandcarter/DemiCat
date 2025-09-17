using System;
using System.Collections.Generic;
using System.Collections;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Linq;
using DemiCatPlugin;
using Dalamud.Plugin.Services;
using Xunit;
using System.Net.Http;
using System.Reflection;

public class ChatWindowWebSocketTests
{
    [Fact]
    public async Task WebSocket_SubAckSendResyncFlow()
    {
        using var server = new MockWsServer(async (ws, _, srv) =>
        {
            while (ws.State == WebSocketState.Open)
            {
                var msg = await srv.Receive(ws);
                if (msg.Contains("\"op\":\"sub\""))
                {
                    await srv.Send(ws, "{\"op\":\"ack\",\"channel\":\"1\",\"guildId\":\"\",\"kind\":\"CHAT\"}");
                    await srv.Send(ws, "{\"op\":\"batch\",\"channel\":\"1\",\"guildId\":\"\",\"kind\":\"CHAT\",\"messages\":[{\"cursor\":1,\"op\":\"mc\",\"d\":{}}]}");
                }
                else if (msg.Contains("\"op\":\"ack\""))
                {
                    await srv.Send(ws, "{\"op\":\"ack\",\"channel\":\"1\"}");
                    await srv.Send(ws, "{\"op\":\"batch\",\"channel\":\"1\",\"guildId\":\"\",\"kind\":\"CHAT\",\"messages\":[{\"cursor\":2,\"op\":\"mc\",\"d\":{}}]}");
                }
            }
        });

        _ = SetupServices();
        var config = new Config { EnableFcChat = true, ApiBaseUrl = server.HttpBase, ChatChannelId = "1" };
        var selection = new ChannelSelectionService(config);
        using var client = new HttpClient();
        var bridge = new ChatBridge(config, client, new TokenManager(), () => server.Uri, selection);

        var messageCount = 0;
        var ackField = typeof(ChatBridge).GetField("_ackFrameCount", BindingFlags.Instance | BindingFlags.NonPublic)!;
        bridge.MessageReceived += _ => Interlocked.Increment(ref messageCount);
        bridge.Start();
        bridge.Subscribe("1", config.GuildId, ChannelKind.Chat);

        await WaitUntil(() => server.Received.Exists(m => m.Contains("\"op\":\"sub\"")), TimeSpan.FromSeconds(5));
        await WaitUntil(() => (int)ackField.GetValue(bridge)! > 0, TimeSpan.FromSeconds(5));
        await WaitUntil(() => Volatile.Read(ref messageCount) > 0, TimeSpan.FromSeconds(5));

        Assert.Equal(1, (int)ackField.GetValue(bridge)!);

        bridge.Ack("1", config.GuildId, ChannelKind.Chat);
        await WaitUntil(() => server.Received.Exists(m => m.Contains("\"op\":\"ack\"")), TimeSpan.FromSeconds(5));
        await WaitUntil(() => (int)ackField.GetValue(bridge)! > 1, TimeSpan.FromSeconds(5));
        await WaitUntil(() => Volatile.Read(ref messageCount) > 1, TimeSpan.FromSeconds(5));

        await bridge.Send("1", new { text = "hi" });
        await WaitUntil(() => server.Received.Exists(m => m.Contains("\"op\":\"send\"")), TimeSpan.FromSeconds(5));

        bridge.Resync("1");
        await WaitUntil(() => server.Received.Exists(m => m.Contains("\"op\":\"resync\"")), TimeSpan.FromSeconds(5));

        bridge.Stop();

        Assert.Equal(2, (int)ackField.GetValue(bridge)!);
        Assert.Equal(2, Volatile.Read(ref messageCount));
    }

    [Fact]
    public async Task WebSocket_ReconnectPersistsCursor()
    {
        int firstSince = -1;
        int secondSince = -1;
        var firstBatchSent = false;

        using var server = new MockWsServer(async (ws, idx, srv) =>
        {
            if (idx == 1)
            {
                var sub = await srv.Receive(ws);
                firstSince = ExtractSince(sub);
                await srv.Send(ws, "{\"op\":\"batch\",\"channel\":\"1\",\"guildId\":\"\",\"kind\":\"CHAT\",\"messages\":[{\"cursor\":7,\"op\":\"mc\",\"d\":{}}]}");
                firstBatchSent = true;
                await srv.Receive(ws); // ack
                await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "bye", CancellationToken.None);
            }
            else
            {
                var sub = await srv.Receive(ws);
                secondSince = ExtractSince(sub);
                while (ws.State == WebSocketState.Open)
                {
                    await Task.Delay(50);
                }
            }
        });

        _ = SetupServices();
        var config = new Config { EnableFcChat = true, ApiBaseUrl = server.HttpBase, ChatChannelId = "1" };
        var selection = new ChannelSelectionService(config);
        using var client = new HttpClient();
        var bridge = new ChatBridge(config, client, new TokenManager(), () => server.Uri, selection);

        bool received = false;
        bridge.MessageReceived += _ => received = true;
        bridge.Start();
        bridge.Subscribe("1", config.GuildId, ChannelKind.Chat);

        await WaitUntil(() => firstBatchSent && received, TimeSpan.FromSeconds(5));
        bridge.Ack("1", config.GuildId, ChannelKind.Chat);

        await WaitUntil(() => secondSince != -1, TimeSpan.FromSeconds(10));

        bridge.Stop();

        Assert.Equal(0, firstSince);
        Assert.Equal(7, secondSince);
    }

    [Fact]
    public async Task WebSocket_TypingEventRaises()
    {
        using var server = new MockWsServer(async (ws, _, srv) =>
        {
            var msg = await srv.Receive(ws);
            if (msg.Contains("\"op\":\"sub\""))
            {
                await srv.Send(ws, "{\"op\":\"batch\",\"channel\":\"1\",\"guildId\":\"\",\"kind\":\"CHAT\",\"messages\":[{\"cursor\":1,\"op\":\"ty\",\"d\":{\"id\":\"u1\",\"name\":\"Alice\"}}]}");
            }
            while (ws.State == WebSocketState.Open)
            {
                await Task.Delay(50);
            }
        });

        _ = SetupServices();
        var config = new Config { EnableFcChat = true, ApiBaseUrl = server.HttpBase, ChatChannelId = "1" };
        var selection = new ChannelSelectionService(config);
        using var client = new HttpClient();
        var bridge = new ChatBridge(config, client, new TokenManager(), () => server.Uri, selection);

        DiscordUserDto? user = null;
        bridge.TypingReceived += u => user = u;
        bridge.Start();
        bridge.Subscribe("1", config.GuildId, ChannelKind.Chat);

        await WaitUntil(() => user != null, TimeSpan.FromSeconds(5));

        bridge.Stop();

        Assert.NotNull(user);
        Assert.Equal("u1", user!.Id);
    }

    [Fact]
    public async Task WebSocket_TypingEventUpdatesChatWindow()
    {
        using var server = new MockWsServer(async (ws, _, srv) =>
        {
            var msg = await srv.Receive(ws);
            if (msg.Contains("\"op\":\"sub\""))
            {
                await srv.Send(ws, "{\"op\":\"batch\",\"channel\":\"1\",\"guildId\":\"\",\"kind\":\"CHAT\",\"messages\":[{\"cursor\":1,\"op\":\"ty\",\"d\":{\"id\":\"u1\",\"name\":\"Alice\"}}]}");
            }
            while (ws.State == WebSocketState.Open)
            {
                await Task.Delay(50);
            }
        });

        _ = SetupServices();
        var config = new Config { EnableFcChat = true, ApiBaseUrl = server.HttpBase, ChatChannelId = "1" };
        using var client = new HttpClient();
        var tm = new TokenManager();
        var channelService = new ChannelService(config, client, tm);
        var window = new ChatWindow(config, client, null, tm, channelService);
        window.StartNetworking();

        await WaitUntil(() => GetTypingUsers(window).Count > 0, TimeSpan.FromSeconds(5));

        var names = GetTypingUsers(window);
        window.StopNetworking();

        Assert.Contains("Alice", names);
    }

    [Fact]
    public async Task WebSocket_DropsMismatchedBatch()
    {
        using var server = new MockWsServer(async (ws, _, srv) =>
        {
            while (ws.State == WebSocketState.Open)
            {
                var msg = await srv.Receive(ws);
                if (msg.Contains("\"op\":\"sub\""))
                {
                    const string payload = "{\"op\":\"batch\",\"channel\":\"1\",\"guildId\":\"\",\"kind\":\"OFFICER_CHAT\",\"messages\":[{\"cursor\":1,\"op\":\"mc\",\"d\":{}}]}";
                    await srv.Send(ws, payload);
                    await Task.Delay(50);
                    await srv.Send(ws, payload);
                }
            }
        });

        var log = SetupServices();
        var config = new Config { EnableFcChat = true, ApiBaseUrl = server.HttpBase, ChatChannelId = "1" };
        var selection = new ChannelSelectionService(config);
        using var client = new HttpClient();
        var bridge = new ChatBridge(config, client, new TokenManager(), () => server.Uri, selection);

        string? payload = null;
        bridge.MessageReceived += p => payload = p;
        bridge.Start();
        bridge.Subscribe("1", config.GuildId, ChannelKind.Chat);

        await WaitUntil(() => server.Received.Exists(m => m.Contains("\"op\":\"sub\"")), TimeSpan.FromSeconds(5));
        await WaitUntil(() => log.WarningCount > 0, TimeSpan.FromSeconds(5));
        var warnings = log.SnapshotWarnings().Where(w => w.Contains("chat.ws drop batch")).ToList();
        Assert.Single(warnings);
        await Task.Delay(200);
        var throttled = log.SnapshotWarnings().Where(w => w.Contains("chat.ws drop batch")).ToList();
        Assert.Single(throttled);

        bridge.Stop();

        Assert.Null(payload);
    }

    [Fact]
    public async Task WebSocket_Ping404FallsBackToHealth()
    {
        using var server = new MockWsServer(async (ws, _, srv) =>
        {
            while (ws.State == WebSocketState.Open)
            {
                await srv.Receive(ws);
            }
        }) { PingStatus = 404 };

        _ = SetupServices();
        var config = new Config { EnableFcChat = true, ApiBaseUrl = server.HttpBase, ChatChannelId = "1" };
        var selection = new ChannelSelectionService(config);
        using var client = new HttpClient();
        var bridge = new ChatBridge(config, client, new TokenManager(), () => server.Uri, selection);

        bridge.Start();
        bridge.Subscribe("1", config.GuildId, ChannelKind.Chat);

        await WaitUntil(() => server.Received.Exists(m => m.Contains("\"op\":\"sub\"")), TimeSpan.FromSeconds(5));

        bridge.Stop();
    }

    [Fact]
    public async Task WebSocket_AckStoresCursorWithScopedKey()
    {
        using var server = new MockWsServer(async (ws, _, srv) =>
        {
            while (ws.State == WebSocketState.Open)
            {
                var msg = await srv.Receive(ws);
                if (msg.Contains("\"op\":\"sub\""))
                {
                    await srv.Send(ws, "{\"op\":\"batch\",\"channel\":\"1\",\"guildId\":\"guild-1\",\"kind\":\"CHAT\",\"messages\":[{\"cursor\":42,\"op\":\"mc\",\"d\":{}}]}");
                }
                await Task.Delay(20);
            }
        });

        _ = SetupServices();
        var config = new Config { GuildId = "guild-1", EnableFcChat = true, ApiBaseUrl = server.HttpBase, ChatChannelId = "1" };
        var selection = new ChannelSelectionService(config);
        using var client = new HttpClient();
        var bridge = new ChatBridge(config, client, new TokenManager(), () => server.Uri, selection);

        bool received = false;
        bridge.MessageReceived += _ => received = true;

        bridge.Start();
        bridge.Subscribe("1", config.GuildId, ChannelKind.Chat);

        try
        {
            await WaitUntil(() => received, TimeSpan.FromSeconds(5));
            bridge.Ack("1", config.GuildId, ChannelKind.Chat);
            await WaitUntil(() => server.Received.Exists(m => m.Contains("\"op\":\"ack\"")), TimeSpan.FromSeconds(5));
            var key = ChannelKeyHelper.BuildCursorKey(config.GuildId, ChannelKind.Chat, "1");
            await WaitUntil(() => config.ChatCursors.ContainsKey(key), TimeSpan.FromSeconds(5));

            Assert.True(config.ChatCursors.TryGetValue(key, out var cursor));
            Assert.Equal(42, cursor);
            Assert.All(config.ChatCursors.Keys, k => Assert.Equal(3, k.Split(':').Length));
            Assert.DoesNotContain(config.ChatCursors.Keys, k => k == "1");
        }
        finally
        {
            bridge.Stop();
        }
    }

    private static int ExtractSince(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var ch = doc.RootElement.GetProperty("channels")[0];
        return ch.TryGetProperty("since", out var since) ? since.GetInt32() : 0;
    }

    private static List<string> GetTypingUsers(ChatWindow window)
    {
        var field = typeof(ChatWindow).GetField("_typingUsers", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var dict = (IDictionary)field.GetValue(window)!;
        var list = new List<string>();
        foreach (DictionaryEntry entry in dict)
        {
            var nameProp = entry.Value.GetType().GetProperty("Name")!;
            list.Add((string)nameProp.GetValue(entry.Value)!);
        }
        return list;
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

    private static TestLog SetupServices()
    {
        var ps = new PluginServices();
        var framework = new TestFramework();
        var log = new TestLog();
        typeof(PluginServices).GetProperty("Framework", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(ps, framework);
        typeof(PluginServices).GetProperty("Log", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(ps, log);
        return log;
    }

    private class TestFramework : IFramework
    {
        public event FrameworkUpdateDelegate? Update { add { } remove { } }
        public FrameworkUpdateType CurrentUpdateType => FrameworkUpdateType.None;
        public void RunOnTick(Action action, FrameworkUpdatePriority priority = FrameworkUpdatePriority.Normal) => action();
    }

    private class TestLog : IPluginLog
    {
        private readonly List<string> _warnings = new();

        public int WarningCount
        {
            get
            {
                lock (_warnings)
                    return _warnings.Count;
            }
        }

        public List<string> SnapshotWarnings()
        {
            lock (_warnings)
                return new List<string>(_warnings);
        }

        public void Verbose(string message) { }
        public void Verbose(string message, Exception exception) { }
        public void Debug(string message) { }
        public void Debug(string message, Exception exception) { }
        public void Info(string message) { }
        public void Info(string message, Exception exception) { }
        public void Warning(string message) => AddWarning(message);
        public void Warning(string message, Exception exception) => AddWarning(message);
        public void Error(string message) { }
        public void Error(Exception exception, string message) { }
        public void Fatal(string message) { }
        public void Fatal(string message, Exception exception) { }

        private void AddWarning(string message)
        {
            lock (_warnings)
            {
                _warnings.Add(message);
            }
        }
    }

    private class MockWsServer : IDisposable
    {
        private readonly HttpListener _listener;
        private readonly CancellationTokenSource _cts = new();
        private readonly Func<WebSocket, int, MockWsServer, Task> _handler;
        private int _connections;
        public List<string> Received { get; } = new();
        public Uri Uri { get; }
        public string HttpBase { get; }
        public int PingStatus { get; set; } = 200;

        public MockWsServer(Func<WebSocket, int, MockWsServer, Task> handler)
        {
            _handler = handler;
            var port = GetFreePort();
            _listener = new HttpListener();
            _listener.Prefixes.Add($"http://localhost:{port}/");
            _listener.Start();
            Uri = new Uri($"ws://localhost:{port}/ws/chat");
            HttpBase = $"http://localhost:{port}";
            _ = Task.Run(AcceptLoop);
        }

        private async Task AcceptLoop()
        {
            while (!_cts.Token.IsCancellationRequested)
            {
                var ctx = await _listener.GetContextAsync();
                if (ctx.Request.HttpMethod == "HEAD" && ctx.Request.RawUrl == "/api/ping")
                {
                    ctx.Response.StatusCode = PingStatus;
                    ctx.Response.Close();
                    continue;
                }
                if (ctx.Request.HttpMethod == "HEAD" && ctx.Request.RawUrl == "/health")
                {
                    ctx.Response.StatusCode = 200;
                    ctx.Response.Close();
                    continue;
                }
                if (!ctx.Request.IsWebSocketRequest || ctx.Request.RawUrl != "/ws/chat")
                {
                    ctx.Response.StatusCode = 400;
                    ctx.Response.Close();
                    continue;
                }
                var wsCtx = await ctx.AcceptWebSocketAsync(null);
                var idx = Interlocked.Increment(ref _connections);
                _ = Task.Run(() => _handler(wsCtx.WebSocket, idx, this));
            }
        }

        public async Task<string> Receive(WebSocket ws)
        {
            var buffer = new byte[4096];
            var result = await ws.ReceiveAsync(new ArraySegment<byte>(buffer), CancellationToken.None);
            var text = Encoding.UTF8.GetString(buffer, 0, result.Count);
            lock (Received) Received.Add(text);
            return text;
        }

        public Task Send(WebSocket ws, string text)
        {
            var bytes = Encoding.UTF8.GetBytes(text);
            return ws.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, CancellationToken.None);
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

