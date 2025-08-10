using Dalamud.Game.ClientState;
using Dalamud.IoC;
using Dalamud.Plugin;

namespace DalamudPlugin;

internal static class PluginServices
{
    [PluginService]
    internal static IDalamudPluginInterface PluginInterface { get; private set; } = null!;

    [PluginService]
    internal static IClientState ClientState { get; private set; } = null!;
}
