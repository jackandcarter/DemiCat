using System;
using System.Net.Http;
using System.Numerics;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using DemiCatPlugin;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Textures;
using Moq;
using Xunit;

public class PresenceSidebarTests
{
    private sealed class StubHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK));
    }

    private sealed class ImGuiContextScope : IDisposable
    {
        public ImGuiContextScope()
        {
            ImGui.CreateContext();
            var io = ImGui.GetIO();
            io.DisplaySize = new Vector2(800f, 600f);
        }

        public void BeginFrame()
        {
            ImGui.NewFrame();
            ImGui.Begin("test", ImGuiWindowFlags.NoSavedSettings);
        }

        public void EndFrame()
        {
            ImGui.End();
            ImGui.Render();
        }

        public void Dispose()
        {
            ImGui.DestroyContext();
        }
    }

    private static PresenceSidebar CreateSidebar()
    {
        var config = new Config();
        var httpClient = new HttpClient(new StubHandler());
        var service = new DiscordPresenceService(config, httpClient);
        return new PresenceSidebar(service);
    }

    private static void InvokeDrawPresence(PresenceSidebar sidebar, PresenceDto presence)
    {
        var method = typeof(PresenceSidebar).GetMethod("DrawPresence", BindingFlags.NonPublic | BindingFlags.Instance)!;
        method.Invoke(sidebar, new object[] { presence });
    }

    [Fact]
    public void DrawPresence_LoadsBannerTextureWhenMissing()
    {
        using var scope = new ImGuiContextScope();
        var sidebar = CreateSidebar();
        var presence = new PresenceDto
        {
            Id = "1",
            Name = "Tester",
            Status = "online",
            BannerUrl = "https://example.com/banner.png"
        };

        var texture = Mock.Of<ISharedImmediateTexture>();
        var loaderCalled = false;
        sidebar.TextureLoader = (url, assign) =>
        {
            loaderCalled = true;
            assign(texture);
        };

        scope.BeginFrame();
        InvokeDrawPresence(sidebar, presence);
        scope.EndFrame();

        Assert.True(loaderCalled);
        Assert.Same(texture, presence.BannerTexture);
    }

    [Fact]
    public void DrawPresence_DoesNotRequestBannerWithoutUrl()
    {
        using var scope = new ImGuiContextScope();
        var sidebar = CreateSidebar();
        var presence = new PresenceDto
        {
            Id = "2",
            Name = "NoBanner",
            Status = "online",
        };
        presence.AccentColorValue = 0x336699;

        var loaderCalled = false;
        sidebar.TextureLoader = (url, assign) => loaderCalled = true;

        scope.BeginFrame();
        InvokeDrawPresence(sidebar, presence);
        scope.EndFrame();

        Assert.False(loaderCalled);
    }

    [Fact]
    public void ComputeAccentGradient_ReturnsDistinctColors()
    {
        var gradient = PresenceSidebar.ComputeAccentGradient(0x336699);
        Assert.True(gradient.HasValue);
        var (top, bottom) = gradient.Value;
        Assert.NotEqual(top, bottom);
    }
}
