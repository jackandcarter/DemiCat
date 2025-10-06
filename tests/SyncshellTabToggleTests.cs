using System.Net.Http;
using System.Runtime.Serialization;
using DemiCatPlugin;
using DemiCatPlugin.Emoji;
using Xunit;

public class SyncshellTabToggleTests
{
    [Fact]
    public void SyncshellWindow_TogglesWithConfig()
    {
        SyncshellWindow.Instance?.Dispose();
        var cfg = new Config { FCSyncShell = false };
        var http = new HttpClient();
        var token = new TokenManager();
        var channelService = new ChannelService(cfg, http, token);
        var selection = new ChannelSelectionService(cfg);
        var emojiManager = new EmojiManager(http, token, cfg);
        var ui = new UiRenderer(cfg, http, selection, emojiManager);
        var officer = new OfficerChatWindow(cfg, http, null, token, channelService, selection);
        var settings = (SettingsWindow)FormatterServices.GetUninitializedObject(typeof(SettingsWindow));
        var notePadService = new NotePadService(cfg, http, token);
        var notePadWindow = new NotePadWindow(cfg, notePadService);
        var main = new MainWindow(
            cfg,
            ui,
            null,
            officer,
            settings,
            http,
            channelService,
            selection,
            emojiManager,
            notePadWindow,
            () => token.IsReady(),
            () => token.State == LinkState.Linked);

        Assert.Null(SyncshellWindow.Instance);

        cfg.FCSyncShell = true;
        main.UpdateSyncshell();
        Assert.NotNull(SyncshellWindow.Instance);

        cfg.FCSyncShell = false;
        main.UpdateSyncshell();
        Assert.Null(SyncshellWindow.Instance);
    }
}
