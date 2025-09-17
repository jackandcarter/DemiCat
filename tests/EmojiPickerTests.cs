using System;
using System.Net;
using System.Net.Http;
using System.Numerics;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Textures;
using DemiCatPlugin;
using DemiCatPlugin.Emoji;
using Moq;
using Xunit;

public sealed class EmojiPickerTests : IDisposable
{
    private static readonly IntPtr Context = ImGui.CreateContext();
    private bool _frameBegun;

    public EmojiPickerTests()
    {
        ImGui.SetCurrentContext(Context);
        var io = ImGui.GetIO();
        if (io.DisplaySize.X <= 0 || io.DisplaySize.Y <= 0)
            io.DisplaySize = new Vector2(800, 600);
        if (io.DeltaTime <= 0f)
            io.DeltaTime = 1f / 60f;
    }

    [Fact]
    public void CustomEmoji_ImageUrlReflectsAnimation()
    {
        var still = new CustomEmoji("123", "sparkle", false);
        var animated = new CustomEmoji("456", "dance", true);

        Assert.Equal("https://cdn.discordapp.com/emojis/123.png", still.ImageUrl);
        Assert.Equal("https://cdn.discordapp.com/emojis/456.gif", animated.ImageUrl);
    }

    [Fact]
    public void DrawCustom_FetchesCustomEmojiTexture()
    {
        ImGui.NewFrame();
        _frameBegun = true;

        var svc = new EmojiService(new HttpClient(new DummyHandler()), new TokenManager(), new Config());
        svc.Custom.Add(new CustomEmoji("789", "wave", false));

        var picker = new EmojiPicker(svc);
        string? fetchedUrl = null;

        var wrap = new Mock<ISharedTextureWrap>();
        wrap.SetupGet(w => w.Handle).Returns(IntPtr.Zero);

        var tex = new Mock<ISharedImmediateTexture>();
        tex.Setup(t => t.GetWrapOrEmpty()).Returns(wrap.Object);

        WebTextureCache.FetchOverride = (url, cb) =>
        {
            fetchedUrl = url;
            cb(tex.Object);
            return null;
        };

        var method = typeof(EmojiPicker).GetMethod("DrawCustom", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var args = new object?[] { string.Empty, 28f };

        var opened = ImGui.Begin("picker-test");
        try
        {
            if (opened)
            {
                method.Invoke(picker, args);
            }
        }
        finally
        {
            ImGui.End();
            WebTextureCache.FetchOverride = null;
        }

        Assert.Equal(svc.Custom[0].ImageUrl, fetchedUrl);
    }

    public void Dispose()
    {
        if (_frameBegun)
        {
            ImGui.Render();
            _frameBegun = false;
        }
    }

    private sealed class DummyHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
    }
}
