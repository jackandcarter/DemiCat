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
}
