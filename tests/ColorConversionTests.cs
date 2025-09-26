using System.Numerics;
using System.Net.Http;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Dalamud.Bindings.ImGui;
using DemiCatPlugin;
using DemiCatPlugin.Emoji;
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

    [Theory]
    [InlineData(0x123456)]
    [InlineData(0xABCDEF)]
    [InlineData(0x000000)]
    [InlineData(0xFFFFFF)]
    public void RgbToVector4MatchesComponents(uint rgb)
    {
        var vec = ColorUtils.RgbToVector4(rgb);
        Assert.Equal(((rgb >> 16) & 0xFF) / 255f, vec.X, 5);
        Assert.Equal(((rgb >> 8) & 0xFF) / 255f, vec.Y, 5);
        Assert.Equal((rgb & 0xFF) / 255f, vec.Z, 5);
        Assert.Equal(1f, vec.W, 5);
    }

    [Theory]
    [InlineData(0x000000, 0xFFFFFF, 0f, 0x000000)]
    [InlineData(0x000000, 0xFFFFFF, 1f, 0xFFFFFF)]
    [InlineData(0x000000, 0xFFFFFF, 0.5f, 0x808080)]
    [InlineData(0x123456, 0x654321, 0.25f, 0x273849)]
    public void MixRgbInterpolates(uint source, uint target, float amount, uint expected)
    {
        var result = ColorUtils.MixRgb(source, target, amount);
        Assert.Equal(expected, result);
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
        var tokenManager = new TokenManager();
        var channelService = new ChannelService(config, http, tokenManager);
        var selection = new ChannelSelectionService(config);
        var emojiManager = new EmojiManager(http, tokenManager, config);
        var window = new EventCreateWindow(config, http, channelService, selection, emojiManager);
        typeof(EventCreateWindow).GetField("_color", BindingFlags.NonPublic | BindingFlags.Instance)!
            .SetValue(window, ColorUtils.RgbToImGui(rgb));
        var preview = (EmbedDto)typeof(EventCreateWindow).GetMethod("BuildPreview", BindingFlags.NonPublic | BindingFlags.Instance)!
            .Invoke(window, Array.Empty<object>())!;
        Assert.Equal(rgb, preview.Color);
    }
}
