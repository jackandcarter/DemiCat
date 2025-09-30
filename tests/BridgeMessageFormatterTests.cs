using System;
using System.Linq;
using DemiCatPlugin;
using Xunit;

public class BridgeMessageFormatterTests
{
    private static readonly DateTimeOffset FixedTimestamp = new(2024, 1, 2, 3, 4, 5, TimeSpan.Zero);

    private static BridgeMessageFormatter.BridgeFormatterOptions CreateOptions()
        => new()
        {
            UseCharacterName = false,
            ChannelKind = ChannelKind.FcChat,
            Presences = Array.Empty<PresenceDto>(),
            Roles = Array.Empty<RoleDto>(),
            Timestamp = FixedTimestamp,
            AuthorName = "You",
            CharacterName = "Tester",
            WorldName = "World"
        };

    [Fact]
    public void Format_LongContentProducesError()
    {
        var input = new string('x', 2100);
        var result = BridgeMessageFormatter.Format(input, Array.Empty<string>(), CreateOptions());

        Assert.Equal(input, result.Content);
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
        Assert.All(result.Embeds, e => Assert.Null(e.FooterText));
        Assert.Equal(new string('a', result.Embeds[0].Description!.Length), result.Embeds[0].Description);
    }

    [Fact]
    public void Format_ResolvesMentions()
    {
        var options = CreateOptions();
        options.Presences = new[] { new PresenceDto { Id = "1", Name = "Alice" } };

        var result = BridgeMessageFormatter.Format("Hello @Alice", Array.Empty<string>(), options);

        Assert.Equal("Hello <@1>", result.Content);
        Assert.Equal("Hello @Alice", result.DisplayContent);
        Assert.Contains("@Alice", result.DisplayContent);
        Assert.Single(result.Mentions);
        Assert.Equal("1", result.Mentions[0].Id);
    }
}
