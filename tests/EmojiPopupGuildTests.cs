using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using System.Collections.Generic;
using DemiCatPlugin;
using Dalamud.Interface.Textures;
using Moq;
using Xunit;

public class EmojiPopupGuildTests
{
    private class StubHandler : HttpMessageHandler
    {
        private readonly string _response;
        public StubHandler(string response) => _response = response;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(_response)
            });
    }

    private class TestFramework : Dalamud.Plugin.Services.IFramework
    {
        public event Dalamud.Plugin.Services.FrameworkUpdateDelegate? Update { add { } remove { } }
        public Dalamud.Plugin.Services.FrameworkUpdateType CurrentUpdateType => Dalamud.Plugin.Services.FrameworkUpdateType.None;
        public void RunOnTick(System.Action action, Dalamud.Plugin.Services.FrameworkUpdatePriority priority = Dalamud.Plugin.Services.FrameworkUpdatePriority.Normal) => action();
    }

    private class TestLog : Dalamud.Plugin.Services.IPluginLog
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

    [Fact]
    public async Task FetchGuild_CachesTextures()
    {
        var json = "[{\"id\":\"1\",\"name\":\"foo\",\"isAnimated\":false,\"imageUrl\":\"http://image\"}]";
        var config = new Config { ApiBaseUrl = "http://host", GuildId = "1" };
        var http = new HttpClient(new StubHandler(json));
        var popup = new EmojiPopup(config, http);

        var ps = new PluginServices();
        var framework = new TestFramework();
        var log = new TestLog();
        typeof(PluginServices).GetProperty("Framework", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.SetValue(ps, framework);
        typeof(PluginServices).GetProperty("Log", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.SetValue(ps, log);

        var urls = new List<string>();
        WebTextureCache.FetchOverride = (url, cb) =>
        {
            if (url != null) urls.Add(url);
            cb(new Mock<ISharedImmediateTexture>().Object);
            return null;
        };

        var fetch = typeof(EmojiPopup).GetMethod("FetchGuild", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        await (Task)fetch!.Invoke(popup, null)!;

        popup.PreloadGuildTextures();

        Assert.Equal(new[] { "http://image" }, urls);
        Assert.Equal("foo", EmojiPopup.LookupGuildName("1"));
        WebTextureCache.FetchOverride = null;
    }
}

