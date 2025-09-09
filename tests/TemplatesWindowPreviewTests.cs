using System.Net.Http;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using DemiCatPlugin;
using Xunit;

public class TemplatesWindowPreviewTests
{
    private class StubHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK));
    }

    private static EventView? GetPreviewEvent(TemplatesWindow win)
        => (EventView?)typeof(TemplatesWindow)
            .GetField("_previewEvent", BindingFlags.NonPublic | BindingFlags.Instance)!
            .GetValue(win);

    private static bool GetShowPreview(TemplatesWindow win)
        => (bool)typeof(TemplatesWindow)
            .GetField("_showPreview", BindingFlags.NonPublic | BindingFlags.Instance)!
            .GetValue(win)!;

    private static void SetShowPreview(TemplatesWindow win, bool value)
        => typeof(TemplatesWindow)
            .GetField("_showPreview", BindingFlags.NonPublic | BindingFlags.Instance)!
            .SetValue(win, value);

    [Fact]
    public void Preview_Reopens_After_Close_And_Selection_Disposes()
    {
        var config = new Config();
        config.TemplateData.Add(new Template { Name = "One", Title = "T1", Description = "D1" });
        config.TemplateData.Add(new Template { Name = "Two", Title = "T2", Description = "D2" });

        var http = new HttpClient(new StubHandler());
        var window = new TemplatesWindow(config, http);

        var firstTmpl = config.TemplateData[0];
        window.SelectTemplate(0, firstTmpl);
        window.OpenPreview(firstTmpl);
        var first = GetPreviewEvent(window);
        Assert.True(GetShowPreview(window));
        Assert.NotNull(first);

        SetShowPreview(window, false); // simulate closing window
        window.OpenPreview(firstTmpl);
        var second = GetPreviewEvent(window);
        Assert.True(GetShowPreview(window));
        Assert.NotNull(second);
        Assert.NotSame(first, second);

        var secondTmpl = config.TemplateData[1];
        window.SelectTemplate(1, secondTmpl);
        Assert.False(GetShowPreview(window));
        Assert.Null(GetPreviewEvent(window));
    }
}
