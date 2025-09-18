using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Reflection;
using Dalamud.Plugin.Services;
using DemiCatPlugin;
using DemiCatPlugin.Emoji;
using Xunit;

public class EmojiManagerTests
{
    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly HttpStatusCode _status;
        private readonly string _content;

        public int CallCount { get; private set; }

        public StubHandler(HttpStatusCode status, string content)
        {
            _status = status;
            _content = content;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            CallCount++;
            var response = new HttpResponseMessage(_status)
            {
                Content = new StringContent(_content, Encoding.UTF8, "application/json")
            };
            return Task.FromResult(response);
        }
    }

    private sealed class TestFramework : IFramework
    {
        public event FrameworkUpdateDelegate? Update { add { } remove { } }
        public FrameworkUpdateType CurrentUpdateType => FrameworkUpdateType.None;
        public void RunOnTick(System.Action action, FrameworkUpdatePriority priority = FrameworkUpdatePriority.Normal)
            => action();
    }

    private sealed class TestLog : IPluginLog
    {
        public void Verbose(string message) { }
        public void Verbose(string message, System.Exception exception) { }
        public void Debug(string message) { }
        public void Debug(string message, System.Exception exception) { }
        public void Info(string message) { }
        public void Info(string message, System.Exception exception) { }
        public void Warning(string message) { }
        public void Warning(string message, System.Exception exception) { }
        public void Error(string message) { }
        public void Error(System.Exception exception, string message) { }
        public void Fatal(string message) { }
        public void Fatal(System.Exception exception, string message) { }
    }

    private static void SetupPluginServices()
    {
        var services = new PluginServices();
        typeof(PluginServices).GetProperty("Framework", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(services, new TestFramework());
        typeof(PluginServices).GetProperty("Log", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(services, new TestLog());
    }

    [Fact]
    public async Task EnsureCustomAsyncWithoutGuildDoesNotHitApi()
    {
        SetupPluginServices();
        var handler = new StubHandler(HttpStatusCode.OK, "[]");
        using var client = new HttpClient(handler);
        var config = new Config { ApiBaseUrl = "http://host" };
        using var manager = new EmojiManager(client, new TokenManager(), config);

        await manager.EnsureCustomAsync();

        Assert.Equal(0, handler.CallCount);
        Assert.Empty(manager.Custom);
    }

    [Fact]
    public async Task RefreshCustomAsyncLoadsAndNormalizesEmoji()
    {
        SetupPluginServices();
        var json = "[{\"id\":\"1\",\"name\":\"foo\",\"isAnimated\":false,\"imageUrl\":\"http://image\"}]";
        var handler = new StubHandler(HttpStatusCode.OK, json);
        using var client = new HttpClient(handler);
        var config = new Config { ApiBaseUrl = "http://host", GuildId = "99" };
        using var manager = new EmojiManager(client, new TokenManager(), config);

        await manager.RefreshCustomAsync();

        Assert.Equal(1, handler.CallCount);
        var emoji = Assert.Single(manager.Custom);
        Assert.Equal("1", emoji.Id);
        Assert.Equal("foo", emoji.Name);
        Assert.False(emoji.Animated);
        Assert.Equal("http://image", emoji.ImageUrl);
        Assert.True(manager.TryGetCustomEmoji("1", out var lookup));
        Assert.Equal(emoji, lookup);
        var normalized = EmojiFormatter.Normalize(manager, EmojiFormatter.CreateCustomToken("1"));
        Assert.Equal("<:foo:1>", normalized);
    }
}
