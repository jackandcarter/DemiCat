using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using DemiCatPlugin;
using Dalamud.Plugin.Services;
using Xunit;

public class ChatWindowMessageLimitTests
{
    [Fact]
    public async Task RefreshMessages_UsesCursorAndLimits()
    {
        SetupServices();
        var config = new Config { ApiBaseUrl = "http://localhost", ChatChannelId = "1" };
        var handler = new SequenceHandler();
        using var client = new HttpClient(handler);
        var tm = new TokenManager();
        var channelService = new ChannelService(config, client, tm);
        var window = new ChatWindow(config, client, null, tm, channelService);

        handler.EnqueueResponse(SerializeMessages(71, 120));
        handler.EnqueueResponse(SerializeMessages(21, 70));
        await window.RefreshMessages();

        var msgs = GetMessages(window);
        Assert.Equal(100, msgs.Count);
        Assert.Equal("21", msgs[0].Id);
        Assert.Equal("120", msgs[^1].Id);
        Assert.Equal(120, config.ChatCursors["1"]);
        Assert.Equal(2, handler.Requests.Count);

        handler.Requests.Clear();
        handler.EnqueueResponse(SerializeMessages(121, 130));
        await window.RefreshMessages();

        msgs = GetMessages(window);
        Assert.Equal(100, msgs.Count);
        Assert.Equal("31", msgs[0].Id);
        Assert.Equal("130", msgs[^1].Id);
        Assert.Equal(130, config.ChatCursors["1"]);
        Assert.Single(handler.Requests);
        Assert.Contains("after=120", handler.Requests[0].Query);
    }

    [Fact]
    public async Task RefreshMessages_NoCursorRequestsLatest()
    {
        SetupServices();
        var config = new Config { ApiBaseUrl = "http://localhost", ChatChannelId = "1" };
        var handler = new SequenceHandler();
        using var client = new HttpClient(handler);
        var tm = new TokenManager();
        var channelService = new ChannelService(config, client, tm);
        var window = new ChatWindow(config, client, null, tm, channelService);

        handler.EnqueueResponse(SerializeMessages(1, 20));
        await window.RefreshMessages();

        Assert.Single(handler.Requests);
        Assert.DoesNotContain("after=", handler.Requests[0].Query);
        Assert.Equal(20, config.ChatCursors["1"]);
    }

    [Fact]
    public void HandleBridgeMessage_TrimsOldMessages()
    {
        SetupServices();
        var config = new Config { ApiBaseUrl = "http://localhost", ChatChannelId = "1" };
        using var client = new HttpClient(new SequenceHandler());
        var tm = new TokenManager();
        var channelService = new ChannelService(config, client, tm);
        var window = new ChatWindow(config, client, null, tm, channelService);

        var msgs = GetMessages(window);
        for (int i = 1; i <= 100; i++)
            msgs.Add(new DiscordMessageDto { Id = i.ToString(), ChannelId = "1", Author = new DiscordUserDto(), Timestamp = DateTime.UtcNow });
        config.ChatCursors["1"] = 100;

        var method = typeof(ChatWindow).GetMethod("HandleBridgeMessage", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var newMsg = new DiscordMessageDto { Id = "101", ChannelId = "1", Author = new DiscordUserDto(), Timestamp = DateTime.UtcNow };
        var json = JsonSerializer.Serialize(newMsg);
        method.Invoke(window, new object[] { json });

        Assert.Equal(100, msgs.Count);
        Assert.Equal("2", msgs[0].Id);
        Assert.Equal("101", msgs[^1].Id);
        Assert.Equal(101, config.ChatCursors["1"]);
    }

    private static List<DiscordMessageDto> GetMessages(ChatWindow window)
    {
        var field = typeof(ChatWindow).GetField("_messages", BindingFlags.Instance | BindingFlags.NonPublic)!;
        return (List<DiscordMessageDto>)field.GetValue(window)!;
    }

    private static string SerializeMessages(int start, int end)
    {
        var list = new List<object>();
        for (int i = start; i <= end; i++)
        {
            list.Add(new
            {
                id = i.ToString(),
                channelId = "1",
                author = new { id = "u", name = "n" },
                content = "",
                timestamp = DateTime.UtcNow
            });
        }
        return JsonSerializer.Serialize(list);
    }

    private class SequenceHandler : HttpMessageHandler
    {
        private readonly Queue<HttpResponseMessage> _responses = new();
        public List<Uri> Requests { get; } = new();

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request.RequestUri!);
            var resp = _responses.Dequeue();
            return Task.FromResult(resp);
        }

        public void EnqueueResponse(string json)
        {
            var msg = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            };
            _responses.Enqueue(msg);
        }
    }

    private static void SetupServices()
    {
        var ps = new PluginServices();
        var framework = new TestFramework();
        var log = new TestLog();
        typeof(PluginServices).GetProperty("Framework", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(ps, framework);
        typeof(PluginServices).GetProperty("Log", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(ps, log);
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
}
