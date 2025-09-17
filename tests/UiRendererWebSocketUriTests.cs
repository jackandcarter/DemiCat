using System.Net.Http;
using DemiCatPlugin;
using Xunit;

public class UiRendererWebSocketUriTests
{
    [Theory]
    [InlineData("http://example.com", "/ws/custom", "ws://example.com/ws/custom")]
    [InlineData("https://example.com/base", "custom/path", "wss://example.com/base/custom/path")]
    [InlineData("https://example.com/base/", "  ///trim/me  ", "wss://example.com/base/trim/me")]
    [InlineData("http://example.com/base", "  ws  ", "ws://example.com/base/ws")]
    public void BuildWebSocketUri_ComposesExpectedUrl(string baseUrl, string webSocketPath, string expected)
    {
        var config = new Config
        {
            ApiBaseUrl = baseUrl,
            WebSocketPath = webSocketPath
        };
        using var httpClient = new HttpClient();
        var selection = new ChannelSelectionService(config);
        using var ui = new UiRenderer(config, httpClient, selection);

        var uri = ui.BuildWebSocketUri();

        Assert.NotNull(uri);
        Assert.Equal(expected, uri!.ToString());
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void BuildWebSocketUri_UsesDefaultPathWhenBlank(string webSocketPath)
    {
        var config = new Config
        {
            ApiBaseUrl = "http://localhost:8080/base",
            WebSocketPath = webSocketPath
        };
        using var httpClient = new HttpClient();
        var selection = new ChannelSelectionService(config);
        using var ui = new UiRenderer(config, httpClient, selection);

        var uri = ui.BuildWebSocketUri();

        Assert.NotNull(uri);
        Assert.Equal("ws://localhost:8080/base/ws/embeds", uri!.ToString());
    }
}
