using Dalamud.Game.ClientState;
using Dalamud.Game.Gui.Toast;
using Dalamud.IoC;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;

namespace DemiCatPlugin;

internal class PluginServices
{
    internal static PluginServices? Instance { get; private set; }

    [PluginService]
    internal IDalamudPluginInterface PluginInterface { get; private set; } = null!;

    [PluginService]
    internal IClientState ClientState { get; private set; } = null!;

    [PluginService]
    internal ITextureProvider TextureProvider { get; private set; } = null!;

    [PluginService]
    internal ITextureReadbackProvider TextureReadbackProvider { get; private set; } = null!;

    [PluginService]
    internal IFramework Framework { get; private set; } = null!;

    [PluginService]
    internal IPluginLog Log { get; private set; } = null!;

    [PluginService]
    internal IDataManager DataManager { get; private set; } = null!;

    [PluginService]
    internal IToastGui ToastGui { get; private set; } = null!;

    [PluginService]
    internal IChatGui ChatGui { get; private set; } = null!;

    public PluginServices()
    {
        Instance = this;
    }
}
