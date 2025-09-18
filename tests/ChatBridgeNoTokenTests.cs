using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Net;
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

    [Fact]
    public void StopClearsSubscriptionsAndMetadata()
    {
        var handler = new SuccessHandler();
        var client = new HttpClient(handler);
        var config = new Config { ApiBaseUrl = "http://localhost", GuildId = "guild" };
        var tm = new TokenManager();
        var bridge = new ChatBridge(config, client, tm, () => new Uri("ws://localhost"), new ChannelSelectionService(config));

        bridge.Start();
        bridge.Subscribe("channel-old", config.GuildId, ChannelKind.Chat);

        var metadataField = typeof(ChatBridge).GetField("_channelMetadata", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var subsField = typeof(ChatBridge).GetField("_subs", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var cursorsField = typeof(ChatBridge).GetField("_cursors", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var ackedField = typeof(ChatBridge).GetField("_acked", BindingFlags.Instance | BindingFlags.NonPublic)!;

        var metadata = (Dictionary<string, (string GuildId, string Kind)>)metadataField.GetValue(bridge)!;
        var subs = (HashSet<string>)subsField.GetValue(bridge)!;
        var cursors = (Dictionary<string, long>)cursorsField.GetValue(bridge)!;
        var acked = (Dictionary<string, long>)ackedField.GetValue(bridge)!;

        var oldKey = ChannelKeyHelper.BuildCursorKey(config.GuildId, ChannelKind.Chat, "channel-old");
        cursors[oldKey] = 123;
        acked[oldKey] = 123;

        Assert.Single(metadata);
        Assert.Single(subs);

        bridge.Stop();

        Assert.Empty(metadata);
        Assert.Empty(subs);
        Assert.Empty(cursors);
        Assert.Empty(acked);

        bridge.Start();
        bridge.Subscribe("channel-new", config.GuildId, ChannelKind.Chat);

        var newKey = ChannelKeyHelper.BuildCursorKey(config.GuildId, ChannelKind.Chat, "channel-new");

        Assert.Single(metadata);
        Assert.True(metadata.ContainsKey("channel-new"));
        Assert.Single(subs);
        Assert.Contains(newKey, subs);
        Assert.DoesNotContain(oldKey, subs);
        Assert.DoesNotContain(oldKey, cursors.Keys);
        Assert.DoesNotContain(oldKey, acked.Keys);

        bridge.Stop();
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

    private class SuccessHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
    }
}

