using System;
using System.Net.Http;
using DemiCatPlugin;
using Xunit;

public class ConfigDefaultsTests
{
    [Fact]
    public void FeatureFlags_DefaultValues()
    {
        var cfg = new Config();
        Assert.True(cfg.SyncedChat);
        Assert.True(cfg.Events);
        Assert.True(cfg.Templates);
        Assert.True(cfg.Requests);
        Assert.True(cfg.Officer);
        Assert.False(cfg.FCSyncShell);
    }

    [Fact]
    public void Syncshell_Disabled_PreventsInstance()
    {
        SyncshellWindow.Instance?.Dispose();
        var cfg = new Config { FCSyncShell = false };
        Assert.Throws<InvalidOperationException>(() => new SyncshellWindow(cfg, new HttpClient()));
        Assert.Null(SyncshellWindow.Instance);
    }
}
