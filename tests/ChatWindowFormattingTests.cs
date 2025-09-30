using System;
using System.Net.Http;
using System.Net;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using DemiCatPlugin;
using Dalamud.Plugin.Services;
using Xunit;

public class ChatWindowFormattingTests
{
    [Fact]
    public void BoldButton_WrapsSelection()
    {
        var window = CreateWindow();
        SetInput(window, "hello world", 6, 11);
        InvokeWrap(window, "**", "**");
        Assert.Equal("hello **world**", GetInput(window));
    }

    [Fact]
    public void ItalicButton_WrapsSelection()
    {
        var window = CreateWindow();
        SetInput(window, "hello world", 0, 5);
        InvokeWrap(window, "*", "*");
        Assert.Equal("*hello* world", GetInput(window));
    }

    [Fact]
    public void CodeButton_WrapsSelection()
    {
        var window = CreateWindow();
        SetInput(window, "test", 0, 4);
        InvokeWrap(window, "`", "`");
        Assert.Equal("`test`", GetInput(window));
    }

    [Fact]
    public void SpoilerButton_WrapsSelection()
    {
        var window = CreateWindow();
        SetInput(window, "secret", 0, 6);
        InvokeWrap(window, "||", "||");
        Assert.Equal("||secret||", GetInput(window));
    }

    [Fact]
    public void LinkButton_WrapsSelection()
    {
        var window = CreateWindow();
        SetInput(window, "click", 0, 5);
        InvokeWrap(window, "[", "](url)");
        Assert.Equal("[click](url)", GetInput(window));
    }

    [Fact]
    public void BoldButton_WrapsWord_WhenNoSelection()
    {
        var window = CreateWindow();
        SetInput(window, "hello world", 8, 8);
        InvokeWrap(window, "**", "**");
        Assert.Equal("hello **world**", GetInput(window));
    }

    [Fact]
    public void ItalicButton_WrapsWord_WhenNoSelection()
    {
        var window = CreateWindow();
        SetInput(window, "hello world", 2, 2);
        InvokeWrap(window, "*", "*");
        Assert.Equal("*hello* world", GetInput(window));
    }

    [Fact]
    public void CodeButton_WrapsWord_WhenNoSelection()
    {
        var window = CreateWindow();
        SetInput(window, "test", 2, 2);
        InvokeWrap(window, "`", "`");
        Assert.Equal("`test`", GetInput(window));
    }

    [Fact]
    public void SpoilerButton_WrapsWord_WhenNoSelection()
    {
        var window = CreateWindow();
        SetInput(window, "secret", 3, 3);
        InvokeWrap(window, "||", "||");
        Assert.Equal("||secret||", GetInput(window));
    }

    [Fact]
    public void LinkButton_WrapsWord_WhenNoSelection()
    {
        var window = CreateWindow();
        SetInput(window, "click", 2, 2);
        InvokeWrap(window, "[", "](url)");
        Assert.Equal("[click](url)", GetInput(window));
    }

    [Fact]
    public void InsertTextAtSelection_ReplacesSelection()
    {
        var window = CreateWindow();
        SetInput(window, "hello world", 6, 11);
        InsertAtSelection(window, ":smile:");
        Assert.Equal("hello :smile:", GetInput(window));
        var (start, end) = GetSelection(window);
        Assert.Equal(13, start);
        Assert.Equal(13, end);
    }

    [Fact]
    public void InsertTextAtSelection_AppendsWhenCollapsedAtEnd()
    {
        var window = CreateWindow();
        SetInput(window, "hello", 5, 5);
        InsertAtSelection(window, "🙂");
        Assert.Equal("hello🙂", GetInput(window));
        var (start, end) = GetSelection(window);
        Assert.Equal(6, start);
        Assert.Equal(6, end);
    }

    private static ChatWindow CreateWindow()
    {
        SetupServices();
        var config = new Config { ApiBaseUrl = "http://localhost" };
        var handler = new DummyHandler();
        var client = new HttpClient(handler);
        var tm = new TokenManager();
        var channelService = new ChannelService(config, client, tm);
        return new ChatWindow(config, client, null, tm, channelService);
    }

    private static void SetInput(ChatWindow window, string text, int start, int end)
    {
        typeof(ChatWindow).GetField("_input", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(window, text);
        typeof(ChatWindow).GetField("_selectionStart", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(window, start);
        typeof(ChatWindow).GetField("_selectionEnd", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(window, end);
    }

    private static void InvokeWrap(ChatWindow window, string prefix, string suffix)
    {
        typeof(ChatWindow).GetMethod("WrapSelection", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(window, new object[] { prefix, suffix });
    }

    private static void InsertAtSelection(ChatWindow window, string text)
    {
        typeof(ChatWindow).GetMethod("InsertTextAtSelection", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(window, new object[] { text });
    }

    private static string GetInput(ChatWindow window)
    {
        return (string)typeof(ChatWindow).GetField("_input", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!;
    }

    private static (int Start, int End) GetSelection(ChatWindow window)
    {
        var start = (int)typeof(ChatWindow).GetField("_selectionStart", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(window)!;
        var end = (int)typeof(ChatWindow).GetField("_selectionEnd", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(window)!;
        return (start, end);
    }

    private class DummyHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
        }
    }

    private static void SetupServices()
    {
        var ps = new PluginServices();
        var framework = new TestFramework();
        var log = new TestLog();
        typeof(PluginServices).GetProperty("Framework", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(ps, framework);
        typeof(PluginServices).GetProperty("Log", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(ps, log);
    }

    private class TestFramework : IFramework
    {
        public event FrameworkUpdateDelegate? Update { add { } remove { } }
        public FrameworkUpdateType CurrentUpdateType => FrameworkUpdateType.None;
        public Task RunOnTick(System.Action action, FrameworkUpdatePriority priority = FrameworkUpdatePriority.Normal)
        {
            action();
            return Task.CompletedTask;
        }

        public Task RunOnTick(Func<Task> action, FrameworkUpdatePriority priority = FrameworkUpdatePriority.Normal) => action();
    }

    private class TestLog : IPluginLog
    {
        public void Verbose(string message) { }
        public void Verbose(string message, System.Exception exception) { }
        public void Debug(string message) { }
        public void Debug(string message, System.Exception exception) { }
        public void Info(string message) { }
        public void Info(string message, System.Exception exception) { }
        public void Warning(string message) { }
        public void Warning(string message, System.Exception exception) { }
        public void Error(string message) { }
        public void Error(System.Exception exception, string message) { }
        public void Fatal(string message) { }
        public void Fatal(string message, System.Exception exception) { }
    }
}
