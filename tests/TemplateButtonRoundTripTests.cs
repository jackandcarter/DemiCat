using System.Collections.Generic;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using DemiCatPlugin;
using DemiCat.UI;
using DiscordHelper;
using Xunit;

public class TemplateButtonRoundTripTests
{
    private class StubHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK));
    }

    private static TemplatesWindow CreateWindow(ButtonRows state)
    {
        var config = new Config();
        var http = new HttpClient(new StubHandler());
        var window = new TemplatesWindow(config, http);
        var field = typeof(TemplatesWindow).GetField("_buttonRows", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        field!.SetValue(window, state);
        return window;
    }

    [Fact]
    public void ToEmbedDto_UsesButtonLabels()
    {
        var rows = new ButtonRows(new() { new() { new ButtonData { Label = "Join" } } });
        rows.SetLabel(0, 0, "Signup");

        var window = CreateWindow(rows);
        var tmpl = new Template { Title = "T", Description = "D" };

        var embed = window.ToEmbedDto(tmpl);
        var btn = Assert.Single(embed.Buttons!);
        Assert.Equal("Signup", btn.Label);
        Assert.Equal(ButtonStyle.Primary, btn.Style);
        Assert.Null(btn.Emoji);
        Assert.Null(btn.MaxSignups);
        Assert.Null(btn.Width);
        Assert.Null(btn.Height);
    }

    [Fact]
    public void BuildButtonsPayload_ProducesMetadata()
    {
        var rows = new ButtonRows(new() { new() { new ButtonData { Label = "Join" } } });
        var window = CreateWindow(rows);

        var payload = window.BuildButtonsPayload(new Template());
        var btn = Assert.Single(payload);
        Assert.Equal("Join", btn.label);
        Assert.Equal((int)ButtonStyle.Primary, btn.style);
        Assert.Equal(0, btn.rowIndex);

        Assert.Null(btn.emoji);
        Assert.Null(btn.maxSignups);
        Assert.Null(btn.width);
        Assert.Null(btn.height);
    }
}
