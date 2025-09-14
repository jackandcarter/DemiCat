using System.Numerics;
using System.Net.Http;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Dalamud.Bindings.ImGui;
using DemiCatPlugin;
using DiscordHelper;
using Xunit;

public class ColorConversionTests
{
    [Theory]
    [InlineData(0xFF0000)]
    [InlineData(0x00FF00)]
    [InlineData(0x0000FF)]
    [InlineData(0x123456)]
    [InlineData(0xABCDEF)]
    public void RgbToAbgrMatchesImGui(uint rgb)
    {
        var expected = ImGui.ColorConvertFloat4ToU32(new Vector4(
            ((rgb >> 16) & 0xFF) / 255f,
            ((rgb >> 8) & 0xFF) / 255f,
            (rgb & 0xFF) / 255f,
            1f)) & 0x00FFFFFF;
        var actual = ColorUtils.RgbToAbgr(rgb);
        Assert.Equal(expected, actual);
    }

    [Theory]
    [InlineData(0xFF0000)]
    [InlineData(0x00FF00)]
    [InlineData(0x0000FF)]
    [InlineData(0x123456)]
    [InlineData(0xABCDEF)]
    public void AbgrToRgbRoundTrip(uint rgb)
    {
        var abgr = ColorUtils.RgbToAbgr(rgb);
        var back = ColorUtils.AbgrToRgb(abgr);
        Assert.Equal(rgb, back);
    }

    [Theory]
    [InlineData(0xFF0000)]
    [InlineData(0x00FF00)]
    [InlineData(0x0000FF)]
    [InlineData(0x123456)]
    [InlineData(0xABCDEF)]
    public void ImGuiVectorRoundTrip(uint rgb)
    {
        var imGui = ColorUtils.RgbToImGui(rgb);
        var vec = ColorUtils.ImGuiToVector(imGui);
        var back = ColorUtils.ImGuiToRgb(ColorUtils.VectorToImGui(vec));
        Assert.Equal(rgb, back);
    }

    private class StubHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK));
    }

    [Theory]
    [InlineData(0xFF0000)]
    [InlineData(0x00FF00)]
    [InlineData(0x0000FF)]
    [InlineData(0x123456)]
    public void BuildPreviewUsesRgb(uint rgb)
    {
        var config = new Config();
        var http = new HttpClient(new StubHandler());
        var channelService = new ChannelService(config, http, new TokenManager());
        var selection = new ChannelSelectionService(config);
        var window = new EventCreateWindow(config, http, channelService, selection);
        typeof(EventCreateWindow).GetField("_color", BindingFlags.NonPublic | BindingFlags.Instance)!
            .SetValue(window, ColorUtils.RgbToImGui(rgb));
        var preview = (EmbedDto)typeof(EventCreateWindow).GetMethod("BuildPreview", BindingFlags.NonPublic | BindingFlags.Instance)!
            .Invoke(window, Array.Empty<object>())!;
        Assert.Equal(rgb, preview.Color);
    }
}
