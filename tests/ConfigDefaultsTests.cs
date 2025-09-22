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

    [Fact]
    public void Appearance_DefaultValues()
    {
        var cfg = new Config();
        Assert.Equal(1f, cfg.FcChatOpacity);
        Assert.Equal(1f, cfg.OfficerChatOpacity);
        Assert.False(cfg.ChatFadeOutEnabled);
        Assert.Equal(10, cfg.ChatFadeOutDelaySeconds);
        Assert.Equal(0.3f, cfg.ChatFadeOutMinimumAlpha);
        Assert.Equal(0.35f, cfg.ChatInputSplitRatio);
    }
}
