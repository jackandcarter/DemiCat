using System;
using System.Net.Http;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using DemiCatPlugin;
using Xunit;

public class ChatBridgeNoTokenTests
{
    [Fact]
    public async Task StartWithoutToken_DoesNotMakeHttpCalls()
    {
        var handler = new CountingHandler();
        var client = new HttpClient(handler);
        var config = new Config { ApiBaseUrl = "http://localhost" };
        var tm = new TokenManager();
        typeof(TokenManager).GetField("_token", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(tm, null);
        typeof(TokenManager).GetProperty("State", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)!.SetValue(tm, LinkState.Unlinked);

        var bridge = new ChatBridge(config, client, tm, () => new Uri("ws://localhost"), new ChannelSelectionService(config));
        bridge.Start();
        await Task.Delay(100);
        bridge.Stop();

        Assert.Equal(0, handler.CallCount);
    }

    private class CountingHandler : HttpMessageHandler
    {
        public int CallCount { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            CallCount++;
            return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK));
        }
    }
}

