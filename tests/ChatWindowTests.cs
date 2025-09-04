using DemiCatPlugin;
using Xunit;

public class ChatWindowTests
{
    [Fact]
    public void FormatContent_ConvertsMarkdownToImGuiTags()
    {
        var input = "Here is **bold**, *italic*, __underline__, and [link](https://example.com).";
        var expected = "Here is [B]bold[/B], [I]italic[/I], [U]underline[/U], and [LINK=https://example.com]link[/LINK].";

        var result = MarkdownFormatter.Format(input);

        Assert.Equal(expected, result);
    }

    [Fact]
    public void MentionResolver_ReplacesUserAndRoleMentions()
    {
        var presences = new[] { new PresenceDto { Id = "1", Name = "Alice" } };
        var roles = new[] { new RoleDto { Id = "2", Name = "Admin" } };
        var input = "Hello @Alice and @Admin";

        var result = MentionResolver.Resolve(input, presences, roles);

        Assert.Equal("Hello <@1> and <@&2>", result);
    }
}
