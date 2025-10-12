using System;
using Dalamud.Plugin;
using Dalamud.Plugin.Ipc;
using Dalamud.Plugin.Services;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace DemiCatPlugin.SyncShell;

public sealed class GlamourerIpc
{
    private readonly IDalamudPluginInterface _pluginInterface;
    private readonly IClientState _clientState;
    private readonly IPluginLog _log;
    private readonly ICallGateSubscriber<int, uint, (int, JObject?)>? _getState;

    public GlamourerIpc(IDalamudPluginInterface pluginInterface, IClientState clientState, IPluginLog log)
    {
        _pluginInterface = pluginInterface ?? throw new ArgumentNullException(nameof(pluginInterface));
        _clientState = clientState ?? throw new ArgumentNullException(nameof(clientState));
        _log = log ?? throw new ArgumentNullException(nameof(log));

        try
        {
            _getState = _pluginInterface.GetIpcSubscriber<int, uint, (int, JObject?)>("Glamourer.GetState");
            Available = _getState != null;
        }
        catch (Exception ex)
        {
            Available = false;
            _log.Information(ex, "Glamourer IPC unavailable");
        }
    }

    public bool Available { get; }

    public string? TryGetPlayerDesignJson()
    {
        if (!Available || _getState == null)
        {
            return null;
        }

        var player = _clientState.LocalPlayer;
        if (player == null)
        {
            return null;
        }

        try
        {
            var (result, state) = _getState.InvokeFunc(player.ObjectIndex, 0);
            if (result != 0 || state == null)
            {
                return null;
            }

            return state.ToString(Formatting.None);
        }
        catch (Exception ex)
        {
            _log.Warning(ex, "Failed to capture Glamourer state");
            return null;
        }
    }
}
