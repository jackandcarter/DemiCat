using System.Net.Http;
using System.Runtime.Serialization;
using System.Threading.Tasks;
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
        var selection = new ChannelSelectionService(cfg);
        var ui = new UiRenderer(cfg, http, selection);
        var officer = new OfficerChatWindow(cfg, http, null, token, channelService, selection);
        var settings = (SettingsWindow)FormatterServices.GetUninitializedObject(typeof(SettingsWindow));
        var main = new MainWindow(cfg, ui, null, officer, settings, http, channelService, selection, () => Task.FromResult(true));

        Assert.Null(SyncshellWindow.Instance);

        cfg.FCSyncShell = true;
        main.UpdateSyncshell();
        Assert.NotNull(SyncshellWindow.Instance);

        cfg.FCSyncShell = false;
        main.UpdateSyncshell();
        Assert.Null(SyncshellWindow.Instance);
    }
}
