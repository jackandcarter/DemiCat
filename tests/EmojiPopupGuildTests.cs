using System;
using System.Collections.Generic;
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

public class EmojiFormatterTests
{
    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly HttpStatusCode _status;
        private readonly string _content;

        public StubHandler(HttpStatusCode status, string content)
        {
            _status = status;
            _content = content;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(new HttpResponseMessage(_status)
            {
                Content = new StringContent(_content, Encoding.UTF8, "application/json")
            });
    }

    private sealed class TestFramework : IFramework
    {
        public event FrameworkUpdateDelegate? Update { add { } remove { } }
        public FrameworkUpdateType CurrentUpdateType => FrameworkUpdateType.None;
        public Task RunOnTick(System.Action action, FrameworkUpdatePriority priority = FrameworkUpdatePriority.Normal)
        {
            action();
            return Task.CompletedTask;
        }

        public Task RunOnTick(Func<Task> action, FrameworkUpdatePriority priority = FrameworkUpdatePriority.Normal) => action();
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
    public async Task RefreshUnicodeAsyncLoadsEmojiMetadata()
    {
        SetupPluginServices();
        var json = "[{\"emoji\":\"😀\",\"name\":\"grin\",\"imageUrl\":\"http://image\"}]";
        using var client = new HttpClient(new StubHandler(HttpStatusCode.OK, json));
        var config = new Config { ApiBaseUrl = "http://host", GuildId = "1" };
        using var manager = new EmojiManager(client, new TokenManager(), config);

        await manager.RefreshUnicodeAsync();

        var emoji = Assert.Single(manager.Unicode);
        Assert.Equal("😀", emoji.Emoji);
        Assert.Equal("grin", emoji.Name);
        Assert.Equal("http://image", emoji.ImageUrl);
        var status = manager.UnicodeStatus;
        Assert.False(status.Loading);
        Assert.True(status.Loaded);
        Assert.False(status.HasError);
    }

    [Fact]
    public void NormalizeMissingCustomEmojiFallsBackToPlaceholder()
    {
        SetupPluginServices();
        using var client = new HttpClient(new StubHandler(HttpStatusCode.OK, "[]"));
        var config = new Config { ApiBaseUrl = "http://host", GuildId = "1" };
        using var manager = new EmojiManager(client, new TokenManager(), config);

        var token = EmojiFormatter.CreateCustomToken("123");
        var normalized = EmojiFormatter.Normalize(manager, token);

        Assert.Equal("<:emoji:123>", normalized);
    }

    [Fact]
    public void NormalizeStandardEmojiReturnsInput()
    {
        SetupPluginServices();
        using var client = new HttpClient(new StubHandler(HttpStatusCode.OK, "[]"));
        var config = new Config { ApiBaseUrl = "http://host", GuildId = "1" };
        using var manager = new EmojiManager(client, new TokenManager(), config);

        const string smile = "😀";
        var normalized = EmojiFormatter.Normalize(manager, smile);

        Assert.Equal(smile, normalized);
    }

    [Fact]
    public void NormalizeDiscordTokenWithoutLookupPreservesName()
    {
        SetupPluginServices();
        using var client = new HttpClient(new StubHandler(HttpStatusCode.OK, "[]"));
        var config = new Config { ApiBaseUrl = "http://host", GuildId = "1" };
        using var manager = new EmojiManager(client, new TokenManager(), config);

        const string token = "<:foo:123>";
        var normalized = EmojiFormatter.Normalize(manager, token);

        Assert.Equal(token, normalized);
    }

    [Fact]
    public void NormalizeAnimatedDiscordTokenWithoutLookupPreservesAnimation()
    {
        SetupPluginServices();
        using var client = new HttpClient(new StubHandler(HttpStatusCode.OK, "[]"));
        var config = new Config { ApiBaseUrl = "http://host", GuildId = "1" };
        using var manager = new EmojiManager(client, new TokenManager(), config);

        const string token = "<a:bar:456>";
        var normalized = EmojiFormatter.Normalize(manager, token);

        Assert.Equal(token, normalized);
    }

    [Fact]
    public void NormalizeCustomTokensUsesLookupWhenAvailable()
    {
        SetupPluginServices();
        using var client = new HttpClient(new StubHandler(HttpStatusCode.OK, "[]"));
        var config = new Config { ApiBaseUrl = "http://host", GuildId = "1" };
        using var manager = new EmojiManager(client, new TokenManager(), config);

        var lookupField = typeof(EmojiManager).GetField("_customLookup", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var customField = typeof(EmojiManager).GetField("_custom", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var emoji = new CustomEmoji("77", "sparkles", false, "http://image");
        lookupField.SetValue(manager, new Dictionary<string, CustomEmoji>(StringComparer.Ordinal) { ["77"] = emoji });
        customField.SetValue(manager, new[] { emoji });

        var normalized = EmojiFormatter.NormalizeCustomTokens(manager, "Say custom:77!");

        Assert.Equal("Say <:sparkles:77>!", normalized);
    }

    [Fact]
    public void NormalizeCustomTokensFallsBackWithoutLookup()
    {
        SetupPluginServices();
        using var client = new HttpClient(new StubHandler(HttpStatusCode.OK, "[]"));
        var config = new Config { ApiBaseUrl = "http://host", GuildId = "1" };
        using var manager = new EmojiManager(client, new TokenManager(), config);

        var normalized = EmojiFormatter.NormalizeCustomTokens(manager, "custom:88 testing");

        Assert.Equal("<:emoji:88> testing", normalized);
    }
}
