using DemiCatPlugin;
using Xunit;
using System;

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
    public void FormatContent_HandlesExtendedMarkdown()
    {
        var input = "~~strike~~ `code`" +
                    "\n```\nblock\n```" +
                    "\n> quote" +
                    "\n||secret||";
        var expected = "[S]strike[/S] [CODE]code[/CODE]\n" +
                        "[CODEBLOCK]\nblock\n[/CODEBLOCK]\n" +
                        "[QUOTE]quote[/QUOTE]\n" +
                        "[SPOILER]secret[/SPOILER]";

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

    [Fact]
    public void MentionResolver_HandlesSpecialCaseInsensitiveAndEscapes()
    {
        var presences = new[] { new PresenceDto { Id = "1", Name = "Alice" } };
        var roles = new[] { new RoleDto { Id = "2", Name = "Admin" } };
        var input = "@ALICE @admin @Unknown @everyone @Here test@example.com";

        var result = MentionResolver.Resolve(input, presences, roles);

        Assert.Equal("<@1> <@&2> @​Unknown <@everyone> <@here> test@​example.com", result);
    }

    [Fact]
    public void MentionResolver_SkipsUnallowedRoleMentions()
    {
        var presences = Array.Empty<PresenceDto>();
        var roles = new[] { new RoleDto { Id = "2", Name = "Admin" } };
        var allowed = new[] { "3" };
        var input = "Hello @Admin";

        var result = MentionResolver.Resolve(input, presences, roles, allowed);

        Assert.Equal("Hello @​Admin", result);
    }

    [Fact]
    public void MentionResolver_RecognizesExistingMentionTokens()
    {
        var presences = new[] { new PresenceDto { Id = "1", Name = "Alice" } };
        var roles = new[] { new RoleDto { Id = "2", Name = "Admin" } };
        var input = "Hello <@1> and <@&2> and <@1>";

        var result = MentionResolver.ResolveDetailed(input, presences, roles);

        Assert.Equal(input, result.Content);
        Assert.Collection(result.Mentions,
            mention =>
            {
                Assert.Equal("1", mention.Id);
                Assert.Equal("user", mention.Type);
            },
            mention =>
            {
                Assert.Equal("2", mention.Id);
                Assert.Equal("role", mention.Type);
            });
    }

    [Fact]
    public void MentionResolver_UsesMetadataWhenPresent()
    {
        var presences = new[] { new PresenceDto { Id = "1", Name = "Alice" } };
        var roles = new[] { new RoleDto { Id = "2", Name = "Admin" } };
        const char metadata = '\u2063';
        var input = $"Hello @Alice{metadata}<@1> and @Admin{metadata}<@&2>.";

        var result = MentionResolver.ResolveDetailed(input, presences, roles);

        Assert.Equal("Hello <@1> and <@&2>.", result.Content);
        Assert.Collection(result.Mentions,
            mention => Assert.Equal("1", mention.Id),
            mention => Assert.Equal("2", mention.Id));
    }
}
