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
        Assert.False(cfg.IsOfficerToken);
        Assert.False(cfg.FCSyncShell);
        Assert.False(cfg.EnableSyncShell);
        Assert.True(cfg.ShowSyncshellProgressOverlay);
        Assert.True(cfg.SyncshellPeerSyncEnabled);
        Assert.Equal(4096, cfg.SyncshellCacheLimitMb);
    }

    [Fact]
    public void Syncshell_Disabled_PreventsInstance()
    {
        SyncshellWindow.Instance?.Dispose();
        var cfg = new Config { EnableSyncShell = false };
        Assert.Throws<InvalidOperationException>(() => new SyncshellWindow(cfg));
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
        Assert.Equal(Config.DefaultFcEmbedColor, cfg.FcEmbedColor);
        Assert.Equal(Config.DefaultOfficerEmbedColor, cfg.OfficerEmbedColor);
        Assert.False(cfg.FcEmbedBorder.Enabled);
        Assert.Equal(Config.DefaultEmbedBorderGlyph, cfg.FcEmbedBorder.Glyph);
        Assert.Equal(Config.DefaultFcEmbedColor, cfg.FcEmbedBorder.Color);
        Assert.False(cfg.OfficerEmbedBorder.Enabled);
        Assert.Equal(Config.DefaultEmbedBorderGlyph, cfg.OfficerEmbedBorder.Glyph);
        Assert.Equal(Config.DefaultOfficerEmbedColor, cfg.OfficerEmbedBorder.Color);
    }
}
