using System;
using System.Linq;
using DemiCatPlugin;
using Xunit;

public class BridgeMessageFormatterTests
{
    private static BridgeMessageFormatter.BridgeFormatterOptions CreateOptions()
        => new()
        {
            UseCharacterName = false,
            ChannelKind = ChannelKind.FcChat,
            Presences = Array.Empty<PresenceDto>(),
            Roles = Array.Empty<RoleDto>(),
            Timestamp = DateTimeOffset.UtcNow,
            AuthorName = "You",
            CharacterName = "Tester",
            WorldName = "World"
        };

    [Fact]
    public void Format_LongContentProducesError()
    {
        var input = new string('x', 2100);
        var result = BridgeMessageFormatter.Format(input, Array.Empty<string>(), CreateOptions());

        Assert.Contains(result.Errors, e => e.Contains("2000"));
    }

    [Fact]
    public void Format_SplitsIntoMultipleEmbedsWhenTooLong()
    {
        var input = new string('a', 5000);
        var result = BridgeMessageFormatter.Format(input, Array.Empty<string>(), CreateOptions());

        Assert.True(result.Embeds.Count >= 2);
        Assert.True(result.Embeds[0].Description!.Length <= 4096);
        Assert.Contains(result.Warnings, w => w.Contains("split"));
    }

    [Fact]
    public void Format_ResolvesMentions()
    {
        var options = CreateOptions();
        options.Presences = new[] { new PresenceDto { Id = "1", Name = "Alice" } };

        var result = BridgeMessageFormatter.Format("Hello @Alice", Array.Empty<string>(), options);

        Assert.Equal("Hello <@1>", result.Content);
        Assert.Contains("@Alice", result.DisplayContent);
        Assert.Single(result.Mentions);
        Assert.Equal("1", result.Mentions[0].Id);
    }
}
