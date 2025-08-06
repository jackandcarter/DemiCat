using Dalamud.Game.ClientState;
using Dalamud.IoC;
using Dalamud.Plugin;

namespace DalamudPlugin;

internal static class PluginServices
{
    [PluginService]
    internal static DalamudPluginInterface PluginInterface { get; private set; } = null!;

    [PluginService]
    internal static ClientState ClientState { get; private set; } = null!;
}
