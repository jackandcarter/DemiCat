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

    private static TemplatesWindow CreateWindow(ButtonRowEditor editor)
    {
        var config = new Config();
        var http = new HttpClient(new StubHandler());
        var window = new TemplatesWindow(config, http);
        var field = typeof(TemplatesWindow).GetField("_buttonRows", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        field!.SetValue(window, editor);
        return window;
    }

    [Fact]
    public void ToEmbedDto_UsesButtonMetadata()
    {
        var button = new ButtonRowEditor.ButtonData
        {
            Label = "Join",
            Style = ButtonRowEditor.ButtonStyle.Success,
            Emoji = "🔥",
            MaxSignups = 3,
            Width = 2,
            Height = 1
        };
        var editor = new ButtonRowEditor(new()
        {
            new List<ButtonRowEditor.ButtonData> { button }
        });
        // Simulate user editing label
        editor.Value[0][0].Label = "Signup";

        var window = CreateWindow(editor);
        var tmpl = new Template { Title = "T", Description = "D" };

        var embed = window.ToEmbedDto(tmpl);
        var btn = Assert.Single(embed.Buttons!);
        Assert.Equal("Signup", btn.Label);
        Assert.Equal(ButtonStyle.Success, btn.Style);
        Assert.Equal("🔥", btn.Emoji);
        Assert.Equal(3, btn.MaxSignups);
        Assert.Equal(2, btn.Width);
        Assert.Equal(1, btn.Height);
    }

    [Fact]
    public void BuildButtonsPayload_ProducesFullMetadata()
    {
        var button = new ButtonRowEditor.ButtonData
        {
            Label = "Join",
            Style = ButtonRowEditor.ButtonStyle.Danger,
            Emoji = "💥",
            MaxSignups = 5,
            Width = 3,
            Height = 2
        };
        var editor = new ButtonRowEditor(new()
        {
            new List<ButtonRowEditor.ButtonData> { button }
        });
        var window = CreateWindow(editor);

        var payload = window.BuildButtonsPayload();
        var btn = Assert.Single(payload);
        Assert.Equal("Join", btn.label);
        Assert.Equal((int)ButtonRowEditor.ButtonStyle.Danger, btn.style);
        Assert.Equal("💥", btn.emoji);
        Assert.Equal(5, btn.maxSignups);
        Assert.Equal(3, btn.width);
        Assert.Equal(2, btn.height);
    }
}
