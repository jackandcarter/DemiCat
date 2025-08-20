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
}
