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
        public StubHandler(string response)
        {
            _response = response;
        }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(_response)
            });
    }

    [Fact]
    public async Task FetchGuild_LoadsTextures()
    {
        var json = "[{\"id\":\"1\",\"name\":\"foo\",\"isAnimated\":false,\"imageUrl\":\"http://image\"}]";
        var config = new Config { ApiBaseUrl = "http://host", GuildId = "1" };
        var http = new HttpClient(new StubHandler(json));
        var popup = new EmojiPopup(config, http);

        var urls = new List<string>();
        var texture = new Mock<ISharedImmediateTexture>().Object;
        popup.TextureLoader = (url, set) =>
        {
            if (url != null) urls.Add(url);
            set(texture);
        };

        var fetch = typeof(EmojiPopup).GetMethod("FetchGuild", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        await (Task)fetch!.Invoke(popup, null)!;

        popup.LoadGuildTextures();

        var guildField = typeof(EmojiPopup).GetField("_guild", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        var list = (List<EmojiPopup.GuildEmoji>)guildField!.GetValue(popup)!;
        Assert.Single(list);
        Assert.Same(texture, list[0].Texture);
        Assert.Equal(new[] { "http://image" }, urls);
    }
}
