using System.Net.Http;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using DemiCatPlugin;
using DemiCatPlugin.Emoji;
using Xunit;

public class EventCreateWindowFormattingTests
{
    [Fact]
    public void BoldButton_WrapsSelection()
    {
        var window = CreateWindow();
        SetDescription(window, "hello world", 6, 11);
        InvokeWrap(window, "**", "**");
        Assert.Equal("hello **world**", GetDescription(window));
    }

    [Fact]
    public void ItalicButton_WrapsWord_WhenNoSelection()
    {
        var window = CreateWindow();
        SetDescription(window, "hello world", 8, 8);
        InvokeWrap(window, "*", "*");
        Assert.Equal("hello *world*", GetDescription(window));
    }

    private static EventCreateWindow CreateWindow()
    {
        var config = new Config();
        var handler = new DummyHandler();
        var http = new HttpClient(handler);
        var tokenManager = new TokenManager();
        var channelService = new ChannelService(config, http, tokenManager);
        var selection = new ChannelSelectionService(config);
        var emojiManager = new EmojiManager(http, tokenManager, config);
        return new EventCreateWindow(config, http, channelService, selection, emojiManager, tokenManager);
    }

    private static void SetDescription(EventCreateWindow window, string text, int start, int end)
    {
        typeof(EventCreateWindow).GetField("_description", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(window, text);
        typeof(EventCreateWindow).GetField("_descriptionSelectionStart", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(window, start);
        typeof(EventCreateWindow).GetField("_descriptionSelectionEnd", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(window, end);
    }

    private static void InvokeWrap(EventCreateWindow window, string prefix, string suffix)
    {
        typeof(EventCreateWindow).GetMethod("WrapDescription", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(window, new object[] { prefix, suffix });
    }

    private static string GetDescription(EventCreateWindow window)
    {
        return (string)typeof(EventCreateWindow).GetField("_description", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(window)!;
    }

    private class DummyHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK));
        }
    }
}
