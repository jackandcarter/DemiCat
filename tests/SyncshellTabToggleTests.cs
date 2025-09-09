using System.Net.Http;
using System.Runtime.Serialization;
using DemiCatPlugin;
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
        var ui = new UiRenderer(cfg, http);
        var officer = new OfficerChatWindow(cfg, http, null, token, channelService);
        var settings = (SettingsWindow)FormatterServices.GetUninitializedObject(typeof(SettingsWindow));
        var main = new MainWindow(cfg, ui, null, officer, settings, http, channelService);

        Assert.Null(SyncshellWindow.Instance);

        cfg.FCSyncShell = true;
        main.UpdateSyncshell();
        Assert.NotNull(SyncshellWindow.Instance);

        cfg.FCSyncShell = false;
        main.UpdateSyncshell();
        Assert.Null(SyncshellWindow.Instance);
    }
}
