using System.IO;
using System;
using DemiCatPlugin;
using Dalamud.Plugin;
using Moq;
using Xunit;

public class TokenManagerTests
{
    [Fact]
    public void WithoutStoredToken_StateIsUnlinked()
    {
        var mock = new Mock<IDalamudPluginInterface>();
        var tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        Directory.CreateDirectory(tempDir);
        mock.Setup(p => p.ConfigDirectory).Returns(new DirectoryInfo(tempDir));

        var tm = new TokenManager(mock.Object);

        Assert.Equal(LinkState.Unlinked, tm.State);
    }
}
