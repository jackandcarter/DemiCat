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
        var channelService = new ChannelService(config, http, new TokenManager());
        var selection = new ChannelSelectionService(config);
        var window = new TemplatesWindow(config, http, channelService, selection);
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
        Assert.Equal(ButtonSizeHelper.ComputeWidth("Signup"), btn.Width);
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
        Assert.Equal(ButtonSizeHelper.ComputeWidth("Join"), btn.width);
    }

    [Fact]
    public void BuildButtonsPayload_IgnoresEmptyTemplateTags()
    {
        var rows = new ButtonRows(new() { new() { new ButtonData { Label = "Join" } } });
        var window = CreateWindow(rows);

        var tmpl = new Template
        {
            Buttons = new List<Template.TemplateButton>
            {
                new Template.TemplateButton { Label = "Join", Tag = string.Empty }
            }
        };

        var payload = window.BuildButtonsPayload(tmpl);
        var btn = Assert.Single(payload);
        Assert.Equal("Join", btn.label);
    }

    [Fact]
    public void BuildButtonsPayload_AssignsDistinctIdsForDuplicateLabels()
    {
        var rows = new ButtonRows(new()
        {
            new() { new ButtonData { Label = "Join" }, new ButtonData { Label = "Join" } },
            new() { new ButtonData { Label = "Join" } }
        });
        var window = CreateWindow(rows);

        var payload = window.BuildButtonsPayload(new Template());
        Assert.Equal(3, payload.Count);
        Assert.NotEqual(payload[0].customId, payload[1].customId);
        Assert.NotEqual(payload[0].customId, payload[2].customId);
        Assert.NotEqual(payload[1].customId, payload[2].customId);
    }

    [Fact]
    public void BuildButtonsPayload_ComputesWidthFromLabel()
    {
        var rows = new ButtonRows(new() { new() { new ButtonData { Label = "Short" }, new ButtonData { Label = "Much Longer Label" } } });
        var window = CreateWindow(rows);

        var payload = window.BuildButtonsPayload(new Template());
        Assert.Equal(ButtonSizeHelper.ComputeWidth("Short"), payload[0].width);
        Assert.Equal(ButtonSizeHelper.ComputeWidth("Much Longer Label"), payload[1].width);
        Assert.True(payload[1].width > payload[0].width);
    }
}
