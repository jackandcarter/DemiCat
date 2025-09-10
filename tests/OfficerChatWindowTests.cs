using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using DemiCatPlugin;
using Dalamud.Plugin.Services;
using Xunit;

public class OfficerChatWindowTests
{
    [Fact]
    public async Task RefreshMessages_UsesOfficerEndpoint()
    {
        SetupServices();
        var config = new Config
        {
            Officer = true,
            Roles = new[] { "officer" },
            ApiBaseUrl = "http://localhost",
            OfficerChannelId = "42"
        };
        var handler = new SequenceHandler();
        using var client = new HttpClient(handler);
        var tm = new TokenManager();
        var channelService = new ChannelService(config, client, tm);
        var window = new OfficerChatWindow(config, client, null, tm, channelService);

        handler.EnqueueResponse(SerializeMessages(71, 120));
        handler.EnqueueResponse(SerializeMessages(21, 70));
        await window.RefreshMessages();

        var msgs = GetMessages(window);
        Assert.Equal(100, msgs.Count);
        Assert.Equal("21", msgs[0].Id);
        Assert.Equal("120", msgs[^1].Id);
        Assert.Equal("/api/officer-messages/42", handler.Requests[0].AbsolutePath);
        Assert.Equal("/api/officer-messages/42", handler.Requests[1].AbsolutePath);
    }

    [Fact]
    public async Task SendMessage_PostsToOfficerEndpoint()
    {
        SetupServices();
        var config = new Config
        {
            Officer = true,
            Roles = new[] { "officer" },
            ApiBaseUrl = "http://localhost",
            OfficerChannelId = "42"
        };
        var handler = new TestHandler();
        using var client = new HttpClient(handler);
        var tm = new TokenManager();
        var channelService = new ChannelService(config, client, tm);
        var window = new OfficerChatWindow(config, client, null, tm, channelService);

        typeof(ChatWindow).GetField("_input", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(window, "hi");

        handler.EnqueueResponse("{\"id\":\"1\"}");
        var method = typeof(ChatWindow).GetMethod("SendMessage", BindingFlags.Instance | BindingFlags.NonPublic)!;
        await (Task)method.Invoke(window, null)!;

        Assert.Single(handler.Requests);
        Assert.Equal("/api/officer-messages", handler.Requests[0].AbsolutePath);
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
                channelId = "42",
                author = new { id = "u", name = "n" },
                content = string.Empty,
                timestamp = DateTime.UtcNow
            });
        }
        return System.Text.Json.JsonSerializer.Serialize(list);
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

    private class TestHandler : HttpMessageHandler
    {
        public List<Uri> Requests { get; } = new();
        private readonly Queue<HttpResponseMessage> _responses = new();

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request.RequestUri!);
            return Task.FromResult(_responses.Dequeue());
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

