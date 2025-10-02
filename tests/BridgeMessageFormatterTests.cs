using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using DemiCatPlugin;
using DemiCatPlugin.Emoji;
using Xunit;

public class BridgeMessageFormatterTests
{
    private static readonly DateTimeOffset FixedTimestamp = new(2024, 1, 2, 3, 4, 5, TimeSpan.Zero);

    private static BridgeMessageFormatter.BridgeFormatterOptions CreateOptions(EmojiManager? manager = null)
        => new()
        {
            UseCharacterName = false,
            ChannelKind = ChannelKind.FcChat,
            Presences = Array.Empty<PresenceDto>(),
            Roles = Array.Empty<RoleDto>(),
            Timestamp = FixedTimestamp,
            AuthorName = "You",
            CharacterName = "Tester",
            WorldName = "World",
            EmbedBorder = Config.EmbedBorderSettings.CreateDefault(ChannelKind.FcChat),
            EmojiManager = manager
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

    [Fact]
    public void Format_UsesEmbedColorOverride()
    {
        var options = CreateOptions();
        options.EmbedColor = 0x123456;

        var result = BridgeMessageFormatter.Format("Hello", Array.Empty<string>(), options);

        var embed = Assert.Single(result.Embeds);
        Assert.Equal((uint)0x123456, embed.Color);
    }

    [Fact]
    public void Format_AppliesEmbedBorderWhenWithinLimits()
    {
        var options = CreateOptions();
        options.EmbedBorder = new Config.EmbedBorderSettings
        {
            Enabled = true,
            Glyph = Config.SanitizeEmbedBorderGlyph("⚫"),
            Color = 0x112233
        };

        var result = BridgeMessageFormatter.Format("Hi", Array.Empty<string>(), options);

        var embed = Assert.Single(result.Embeds);
        var border = Assert.NotNull(embed.Border);
        Assert.True(border.Enabled);
        Assert.Equal(Config.SanitizeEmbedBorderGlyph("⚫"), border.Glyph);
        Assert.Equal((uint)0x112233 & 0xFFFFFFu, border.Color);

        var expected = EmbedBorderBuilder.Apply("Hi", options.EmbedBorder, ChannelKind.FcChat, 4096);
        Assert.True(expected.Applied);
        Assert.Equal(expected.Text, embed.Description);
        Assert.Equal(expected.Text, result.DisplayContent);
        Assert.DoesNotContain(result.Warnings, w => w.Contains("border", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Format_DisablesBorderWhenLineTooLong()
    {
        var options = CreateOptions();
        options.EmbedBorder = new Config.EmbedBorderSettings
        {
            Enabled = true,
            Glyph = Config.DefaultEmbedBorderGlyph,
            Color = 0x445566
        };

        var input = new string('x', 200);
        var result = BridgeMessageFormatter.Format(input, Array.Empty<string>(), options);

        var embed = Assert.Single(result.Embeds);
        Assert.Null(embed.Border);
        Assert.Equal(input, embed.Description);
        Assert.Equal(input, result.DisplayContent);
        Assert.Contains(result.Warnings, w => w.Contains("border", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Format_NormalizesCustomEmojiDisplayContent()
    {
        using var manager = CreateEmojiManager();
        var options = CreateOptions(manager);

        var result = BridgeMessageFormatter.Format("Hello custom:42", Array.Empty<string>(), options);

        Assert.Equal("Hello <:party:42>", result.DisplayContent);
    }

    private static EmojiManager CreateEmojiManager()
    {
        var handler = new NullHandler();
        var client = new HttpClient(handler);
        var manager = new EmojiManager(client, new TokenManager(), new Config());

        var lookupField = typeof(EmojiManager).GetField("_customLookup", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var customField = typeof(EmojiManager).GetField("_custom", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var emoji = new CustomEmoji("42", "party", false, "http://image");
        var lookup = new Dictionary<string, CustomEmoji>(StringComparer.Ordinal) { ["42"] = emoji };
        lookupField.SetValue(manager, lookup);
        customField.SetValue(manager, new[] { emoji });

        return manager;
    }

    private sealed class NullHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
    }
}
