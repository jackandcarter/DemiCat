using System.Collections.Generic;
using DemiCatPlugin;
using Xunit;

public class ChatWindowMentionTests
{
    [Fact]
    public void ReplaceMentionTokens_ReplacesAllMentionTypes()
    {
        var mentions = new List<DiscordMentionDto>
        {
            new() { Id = "1", Name = "Alice", Type = "user" },
            new() { Id = "2", Name = "Admins", Type = "role" },
            new() { Id = "3", Name = "general", Type = "channel" }
        };
        var text = "<@1> <@&2> <#3>";
        var result = ChatWindow.ReplaceMentionTokens(text, mentions);
        Assert.Equal("@Alice @Admins #general", result);
    }
}
