using System;
using System.Text.Json;
using DemiCatPlugin.SyncShell;
using Xunit;

public class SyncshellServiceHelperTests
{
    [Theory]
    [InlineData(0, "Idle")]
    [InlineData(1, "Syncing 1 member")]
    [InlineData(2, "Syncing 2 members")]
    public void FormatStatusForMembersMatchesExpected(int count, string expected)
    {
        var status = SyncShellService.FormatStatusForMembers(count);
        Assert.Equal(expected, status);
    }

    [Fact]
    public void PresenceUpdateSerializesStringIdentifiers()
    {
        var dto = new PresenceUpdateDto
        {
            ActiveMemberIds = { "123456789012345678", "987654321098765432" }
        };

        var json = JsonSerializer.Serialize(dto);
        Assert.Contains("\"123456789012345678\"", json);
        Assert.Contains("\"987654321098765432\"", json);
    }

    [Theory]
    [InlineData("2024-05-01T12:30:00Z", true)]
    [InlineData("2024-05-01 12:30:00", true)]
    [InlineData("not-a-timestamp", false)]
    [InlineData(null, false)]
    public void ParseTimestampHandlesInputs(string? value, bool expectValue)
    {
        var result = SyncShellService.ParseTimestamp(value);
        if (expectValue)
        {
            Assert.NotNull(result);
            Assert.True(result.Value > DateTimeOffset.MinValue);
        }
        else
        {
            Assert.Null(result);
        }
    }
}
