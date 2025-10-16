using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Net;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using DemiCatPlugin;
using System.Text.Json;
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

    [Fact]
    public void UnsubscribeLastChannel_SendsEmptySubscriptionList()
    {
        var handler = new SuccessHandler();
        var client = new HttpClient(handler);
        var config = new Config { ApiBaseUrl = "http://localhost", GuildId = "guild" };
        var tm = new TokenManager();
        var bridge = new ChatBridge(config, client, tm, () => new Uri("ws://localhost"), new ChannelSelectionService(config));

#if TEST
        var frames = new List<string>();
        bridge.ForceWebSocketOpen = true;
        bridge.SendRawInterceptor = frames.Add;
#endif

        bridge.Subscribe("channel-1", config.GuildId, ChannelKind.Chat);

#if TEST
        frames.Clear();
#endif

        bridge.Unsubscribe("channel-1");

#if TEST
        Assert.Contains("{\"op\":\"sub\",\"channels\":[]}", frames);
#endif
    }

    [Fact]
    public async Task Send_FormatsPayloadForServerCompatibility()
    {
        var handler = new SuccessHandler();
        var client = new HttpClient(handler);
        var config = new Config { ApiBaseUrl = "http://localhost" };
        var tm = new TokenManager();
        var bridge = new ChatBridge(config, client, tm, () => new Uri("ws://localhost"), new ChannelSelectionService(config));

#if TEST
        var frames = new List<string>();
        bridge.ForceWebSocketOpen = true;
        bridge.SendRawInterceptor = frames.Add;
#endif

        var payload = new
        {
            content = "hello",
            embedColor = 42,
            metadata = new { nested = true }
        };

        await bridge.Send("123", payload);

#if TEST
        var frame = Assert.Single(frames);
        using var doc = JsonDocument.Parse(frame);
        var root = doc.RootElement;
        Assert.Equal("send", root.GetProperty("op").GetString());
        Assert.Equal("123", root.GetProperty("channel").GetString());
        var data = root.GetProperty("d");
        Assert.Equal("hello", data.GetProperty("content").GetString());
        Assert.Equal(42, data.GetProperty("embedColor").GetInt32());
        Assert.True(data.GetProperty("metadata").GetProperty("nested").GetBoolean());
#endif
    }

    [Fact]
    public async Task AckAsync_UsesCurFieldForCursorUpdates()
    {
        var handler = new SuccessHandler();
        var client = new HttpClient(handler);
        var config = new Config { ApiBaseUrl = "http://localhost", GuildId = "guild" };
        var tm = new TokenManager();
        var bridge = new ChatBridge(config, client, tm, () => new Uri("ws://localhost"), new ChannelSelectionService(config));

#if TEST
        var frames = new List<string>();
        bridge.ForceWebSocketOpen = true;
        bridge.SendRawInterceptor = frames.Add;
#endif

        bridge.Subscribe("123", config.GuildId, ChannelKind.Chat);

        await Task.Delay(10);

#if TEST
        frames.Clear();
#endif

        var cursorsField = typeof(ChatBridge).GetField("_cursors", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var ackedField = typeof(ChatBridge).GetField("_acked", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var cursors = (Dictionary<string, long>)cursorsField.GetValue(bridge)!;
        var acked = (Dictionary<string, long>)ackedField.GetValue(bridge)!;
        var key = ChannelKeyHelper.BuildCursorKey(config.GuildId, ChannelKind.Chat, "123");
        cursors[key] = 9876;
        acked.Remove(key);

        var ackAsync = typeof(ChatBridge).GetMethod("AckAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var task = (Task)ackAsync.Invoke(bridge, new object[] { "123" })!;
        await task;

#if TEST
        var frame = Assert.Single(frames);
        using var doc = JsonDocument.Parse(frame);
        var root = doc.RootElement;
        Assert.Equal("ack", root.GetProperty("op").GetString());
        Assert.Equal("123", root.GetProperty("channel").GetString());
        Assert.Equal(9876, root.GetProperty("cur").GetInt64());
#endif
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

