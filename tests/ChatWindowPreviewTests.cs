using System;
using System.Linq;
using DemiCatPlugin;
using DiscordHelper;
using Xunit;

public class ChatWindowPreviewTests
{
    private static readonly DateTimeOffset FixedTimestamp = new(2024, 1, 1, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public void GetPreviewPlainText_ReturnsNull_WhenEmbedsCoverContent()
    {
        var options = new BridgeMessageFormatter.BridgeFormatterOptions
        {
            AuthorName = "Tester",
            Timestamp = FixedTimestamp,
            EmbedBorder = Config.EmbedBorderSettings.CreateDefault(ChannelKind.FcChat)
        };
        var message = BridgeMessageFormatter.Format("Hello world", Array.Empty<string>(), options);

        var preview = ChatWindow.GetPreviewPlainText(message);

        Assert.Null(preview);
    }

    [Fact]
    public void GetPreviewPlainText_ReturnsRemainder_WhenEmbedsDoNotCoverContent()
    {
        var options = new BridgeMessageFormatter.BridgeFormatterOptions
        {
            AuthorName = "Tester",
            Timestamp = FixedTimestamp,
            EmbedBorder = Config.EmbedBorderSettings.CreateDefault(ChannelKind.FcChat)
        };
        var longBody = new string('A', 50000);
        var message = BridgeMessageFormatter.Format(longBody, Array.Empty<string>(), options);

        var embedText = string.Concat(message.Embeds.Select(e => e.Description ?? string.Empty));
        Assert.True(embedText.Length <= message.DisplayContent.Length);
        var expected = message.DisplayContent[embedText.Length..];

        Assert.NotEqual(0, expected.Length);

        var preview = ChatWindow.GetPreviewPlainText(message);

        Assert.Equal(expected, preview);
    }

    [Fact]
    public void GetPreviewPlainText_ReturnsDisplayContent_WhenNoEmbeds()
    {
        var message = new BridgeMessageFormatter.BridgeFormattedMessage
        {
            Content = "Plain preview",
            DisplayContent = "Plain preview",
            Embeds = Array.Empty<EmbedDto>()
        };

        var preview = ChatWindow.GetPreviewPlainText(message);

        Assert.Equal("Plain preview", preview);
    }
}
