using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using DemiCatPlugin;
using Moq;
using Xunit;

public class ChatWindowChannelFetchTests
{
    [Fact]
    public async Task RefreshChannels_DeduplicatesConcurrentRequests()
    {
        SetupServices();
        var config = new Config
        {
            ApiBaseUrl = "http://localhost",
            GuildId = "guild"
        };
        var handler = new SequenceHandler();
        handler.EnqueueResponse(SerializeChannels(("1", "general")));
        using var client = new HttpClient(handler);
        var tokenManager = new TokenManager();
        var channelService = new ChannelService(config, client, tokenManager);
        var window = new ChatWindow(config, client, null, tokenManager, channelService);

        await Task.WhenAll(window.RefreshChannels(), window.RefreshChannels());

        Assert.Single(handler.Requests);
        Assert.Equal("/api/channels", handler.Requests[0].AbsolutePath);

        handler.EnqueueResponse(SerializeChannels(("2", "general")));
        await window.RefreshChannels();

        Assert.Equal(2, handler.Requests.Count);
        window.Dispose();
    }

    [Fact]
    public async Task OfficerRefreshChannels_DeduplicatesConcurrentRequests()
    {
        SetupServices();
        var config = new Config
        {
            ApiBaseUrl = "http://localhost",
            GuildId = "guild",
            Roles = new[] { "officer" },
            IsOfficerToken = true
        };
        var handler = new SequenceHandler();
        handler.EnqueueResponse(SerializeChannels(("1", "officer")));
        using var client = new HttpClient(handler);
        var tokenManager = new TokenManager();
        var channelService = new ChannelService(config, client, tokenManager);
        var window = new OfficerChatWindow(config, client, null, tokenManager, channelService);

        await Task.WhenAll(window.RefreshChannels(), window.RefreshChannels());

        Assert.Single(handler.Requests);
        Assert.Equal("/api/channels", handler.Requests[0].AbsolutePath);

        handler.EnqueueResponse(SerializeChannels(("2", "officer")));
        await window.RefreshChannels();

        Assert.Equal(2, handler.Requests.Count);
        window.Dispose();
    }

    [Fact]
    public void SetChannels_FiltersMismatchedGuildEntries()
    {
        SetupServices();
        var config = new Config
        {
            ApiBaseUrl = "http://localhost",
            GuildId = "guild-a"
        };
        using var client = new HttpClient(new HttpClientHandler());
        var tokenManager = new TokenManager();
        var channelService = new ChannelService(config, client, tokenManager);
        var window = new ChatWindow(config, client, null, tokenManager, channelService);

        var channels = new List<ChannelDto>
        {
            new() { Id = "1", Name = "General", GuildId = "guild-a" },
            new() { Id = "2", Name = "Other", GuildId = "guild-b" }
        };

        window.SetChannels(channels);

        Assert.Equal("guild-a", config.GuildId);

        var storedChannels = (List<ChannelDto>)typeof(ChatWindow)
            .GetField("_channels", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(window)!;

        Assert.Single(storedChannels);
        Assert.Equal("1", storedChannels[0].Id);

        window.Dispose();
    }

    private static string SerializeChannels(params (string Id, string Name)[] channels)
    {
        var list = new List<object>();
        foreach (var (id, name) in channels)
        {
            list.Add(new
            {
                id,
                name,
                parentId = (string?)null
            });
        }
        return System.Text.Json.JsonSerializer.Serialize(list);
    }

    private static void SetupServices()
    {
        var services = new PluginServices();
        var framework = new TestFramework();
        var log = new TestLog();
        typeof(PluginServices).GetProperty("Framework", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(services, framework);
        typeof(PluginServices).GetProperty("Log", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(services, log);
        var pluginInterface = new Mock<IDalamudPluginInterface>();
        typeof(PluginServices).GetProperty("PluginInterface", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(services, pluginInterface.Object);
    }

    private sealed class SequenceHandler : HttpMessageHandler
    {
        private readonly Queue<HttpResponseMessage> _responses = new();
        public List<Uri> Requests { get; } = new();

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request.RequestUri!);
            if (_responses.Count == 0)
            {
                throw new InvalidOperationException("No response queued.");
            }
            return Task.FromResult(_responses.Dequeue());
        }

        public void EnqueueResponse(string json)
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            };
            _responses.Enqueue(response);
        }
    }

    private sealed class TestFramework : IFramework
    {
        public event FrameworkUpdateDelegate? Update { add { } remove { } }
        public FrameworkUpdateType CurrentUpdateType => FrameworkUpdateType.None;
        public void RunOnTick(Action action, FrameworkUpdatePriority priority = FrameworkUpdatePriority.Normal) => action();
    }

    private sealed class TestLog : IPluginLog
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
        public void Fatal(Exception exception, string message) { }
    }
}
