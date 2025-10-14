using System;
using Dalamud.Plugin;
using Dalamud.Plugin.Ipc;
using Dalamud.Plugin.Ipc.Exceptions;
using Dalamud.Plugin.Services;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace DemiCatPlugin.SyncShell;

public sealed class GlamourerIpc
{
    private readonly IDalamudPluginInterface _pluginInterface;
    private readonly IClientState _clientState;
    private static readonly string[] ApplyGateNames =
    {
        "Glamourer.Api/ApplyPlayerDesign",
        "Glamourer.Api/ApplyAll",
        "Glamourer.Api/Design/Apply",
    };

    private readonly IPluginLog _log;
    private readonly ICallGateSubscriber<int, uint, (int, JObject?)>? _getState;
    private readonly ICallGateSubscriber<string, object?>? _applyDesign;

    public GlamourerIpc(IDalamudPluginInterface pluginInterface, IClientState clientState, IPluginLog log)
    {
        _pluginInterface = pluginInterface ?? throw new ArgumentNullException(nameof(pluginInterface));
        _clientState = clientState ?? throw new ArgumentNullException(nameof(clientState));
        _log = log ?? throw new ArgumentNullException(nameof(log));

        try
        {
            _getState = _pluginInterface.GetIpcSubscriber<int, uint, (int, JObject?)>("Glamourer.GetState");
        }
        catch (Exception ex)
        {
            _log.Information(ex, "Glamourer IPC unavailable");
        }

        _applyDesign = TryGetApplyGate();
        Available = _getState != null || _applyDesign != null;
    }

    public bool Available { get; }

    public bool CanApply => _applyDesign != null;

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

    public void ApplyPlayerDesignJson(string json)
    {
        if (!CanApply || _applyDesign == null || string.IsNullOrWhiteSpace(json))
        {
            return;
        }

        try
        {
            _applyDesign.InvokeAction(json);
        }
        catch (Exception ex)
        {
            _log.Warning(ex, "Failed to apply Glamourer design");
        }
    }

    private ICallGateSubscriber<string, object?>? TryGetApplyGate()
    {
        foreach (var gate in ApplyGateNames)
        {
            try
            {
                var subscriber = _pluginInterface.GetIpcSubscriber<string, object?>(gate);
                if (subscriber != null)
                {
                    return subscriber;
                }
            }
            catch (IpcNotReadyError)
            {
            }
            catch (IpcError)
            {
            }
            catch (Exception ex)
            {
                _log.Debug(ex, "Glamourer apply gate {Gate} unavailable", gate);
            }
        }

        return null;
    }
}
