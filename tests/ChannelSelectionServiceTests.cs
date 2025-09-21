using DemiCatPlugin;
using Xunit;

public class ChannelSelectionServiceTests
{
    [Fact]
    public void SetChannel_PersistsSelectionsPerGuild()
    {
        var config = new Config();
        var service = new ChannelSelectionService(config);

        service.SetChannel(ChannelKind.Chat, "guild-a", "chan-1");
        service.SetChannel(ChannelKind.Chat, "guild-b", "chan-2");

        var keyA = ChannelKeyHelper.BuildSelectionKey("guild-a", ChannelKind.Chat);
        var keyB = ChannelKeyHelper.BuildSelectionKey("guild-b", ChannelKind.Chat);

        Assert.Equal("chan-1", config.ChannelSelections[keyA]);
        Assert.Equal("chan-2", config.ChannelSelections[keyB]);
        Assert.Equal(2, config.ChannelSelections.Count);

        var selectedA = service.GetChannel(ChannelKind.Chat, "guild-a", out var storedA);
        var selectedB = service.GetChannel(ChannelKind.Chat, "guild-b", out var storedB);
        var defaultSelection = service.GetChannel(ChannelKind.Chat, null, out var storedDefault);

        Assert.True(storedA);
        Assert.True(storedB);
        Assert.False(storedDefault);
        Assert.Equal("chan-1", selectedA);
        Assert.Equal("chan-2", selectedB);
        Assert.Equal(string.Empty, defaultSelection);

        service.SetChannel(ChannelKind.Chat, "guild-a", "chan-3");
        Assert.Equal("chan-3", service.GetChannel(ChannelKind.Chat, "guild-a", out _));
        Assert.Equal("chan-2", service.GetChannel(ChannelKind.Chat, "guild-b", out _));
    }

    [Fact]
    public void SetChannel_PersistsDefaultSelectionWhenMatchingExistingConfig()
    {
        var config = new Config
        {
            FcChannelId = "chan-1"
        };
        var service = new ChannelSelectionService(config);

        var initial = service.GetChannel(ChannelKind.FcChat, null, out var hasStored);
        Assert.False(hasStored);
        Assert.Equal("chan-1", initial);

        var key = ChannelKeyHelper.BuildSelectionKey(null, ChannelKind.FcChat);
        Assert.False(config.ChannelSelections.ContainsKey(key));

        var events = 0;
        (string Kind, string Guild, string OldId, string NewId) last = default;
        service.ChannelChanged += (kind, guild, oldId, newId) =>
        {
            events++;
            last = (kind, guild, oldId, newId);
        };

        service.SetChannel(ChannelKind.FcChat, null, "chan-1");

        Assert.True(config.ChannelSelections.TryGetValue(key, out var stored));
        Assert.Equal("chan-1", stored);
        Assert.Equal(1, events);
        Assert.Equal(ChannelKind.FcChat, last.Kind);
        Assert.Equal(string.Empty, last.Guild);
        Assert.Equal("chan-1", last.OldId);
        Assert.Equal("chan-1", last.NewId);
    }

    [Fact]
    public void SetChannel_EventDoesNotNotifyWhenKeysMatch()
    {
        const string guildId = "guild-1";
        var normalizedGuild = ChannelKeyHelper.NormalizeGuildId(guildId);
        var key = ChannelKeyHelper.BuildSelectionKey(guildId, ChannelKind.Event);
        var scopedKey = $"Event:{normalizedGuild}";

        var config = new Config();
        config.ChannelSelections[key] = "event-42";
        config.ChannelSelections[scopedKey] = "event-42";

        var service = new ChannelSelectionService(config);

        var events = 0;
        service.ChannelChanged += (kind, guild, oldId, newId) => events++;

        service.SetChannel(ChannelKind.Event, guildId, "event-42");

        Assert.Equal(0, events);
        Assert.Equal("event-42", config.ChannelSelections[key]);
        Assert.Equal("event-42", config.ChannelSelections[scopedKey]);
    }

    [Fact]
    public void SetChannel_EventPersistsMissingNormalKey()
    {
        const string guildId = "guild-1";
        var normalizedGuild = ChannelKeyHelper.NormalizeGuildId(guildId);
        var scopedKey = $"Event:{normalizedGuild}";

        var config = new Config();
        config.ChannelSelections[scopedKey] = "event-42";

        var service = new ChannelSelectionService(config);

        var events = 0;
        (string OldId, string NewId) last = default;
        service.ChannelChanged += (kind, guild, oldId, newId) =>
        {
            events++;
            last = (oldId, newId);
        };

        service.SetChannel(ChannelKind.Event, guildId, "event-42");

        var key = ChannelKeyHelper.BuildSelectionKey(guildId, ChannelKind.Event);
        Assert.True(config.ChannelSelections.TryGetValue(key, out var storedNormal));
        Assert.Equal("event-42", storedNormal);
        Assert.Equal("event-42", config.ChannelSelections[scopedKey]);
        Assert.Equal(1, events);
        Assert.Equal("event-42", last.OldId);
        Assert.Equal("event-42", last.NewId);
    }
}
