using System.Collections.Generic;
using System.Net.Http;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using DemiCatPlugin;
using DemiCat.UI;
using DiscordHelper;
using Xunit;

public class EventButtonWidthTests
{
    private class StubHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK));
    }

    [Fact]
    public void BuildPreview_ComputesButtonWidth()
    {
        var config = new Config();
        var http = new HttpClient(new StubHandler());
        var channelService = new ChannelService(config, http, new TokenManager());
        var window = new EventCreateWindow(config, http, channelService);
        var buttonsField = typeof(EventCreateWindow).GetField("_buttons", BindingFlags.NonPublic | BindingFlags.Instance)!;
        buttonsField.SetValue(window, new List<Template.TemplateButton>
        {
            new() { Tag = "s", Label = "Short", Include = true },
            new() { Tag = "l", Label = "A much longer label", Include = true }
        });

        var preview = (EmbedDto)typeof(EventCreateWindow)
            .GetMethod("BuildPreview", BindingFlags.NonPublic | BindingFlags.Instance)!
            .Invoke(window, System.Array.Empty<object>())!;
        var btns = preview.Buttons!;
        Assert.Equal(ButtonSizeHelper.ComputeWidth("Short"), btns[0].Width);
        Assert.Equal(ButtonSizeHelper.ComputeWidth("A much longer label"), btns[1].Width);
        Assert.True(btns[1].Width > btns[0].Width);
    }
}
