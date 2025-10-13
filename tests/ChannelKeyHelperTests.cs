using DemiCatPlugin;
using Xunit;

public class ChannelKeyHelperTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("default")]
    [InlineData("DEFAULT")]
    public void IsDefaultGuild_TreatsSentinelAsDefault(string? value)
    {
        Assert.True(ChannelKeyHelper.IsDefaultGuild(value));
    }

    [Fact]
    public void NormalizeGuildId_ReturnsSentinelForEmptyValues()
    {
        Assert.Equal("default", ChannelKeyHelper.NormalizeGuildId(null));
        Assert.Equal("default", ChannelKeyHelper.NormalizeGuildId("   "));
        Assert.Equal("guild-123", ChannelKeyHelper.NormalizeGuildId("guild-123"));
    }
}
