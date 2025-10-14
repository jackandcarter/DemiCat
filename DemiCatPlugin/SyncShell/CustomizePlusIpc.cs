using System;
using Dalamud.Plugin;
using Dalamud.Plugin.Ipc;
using Dalamud.Plugin.Ipc.Exceptions;
using Dalamud.Plugin.Services;

namespace DemiCatPlugin.SyncShell;

public sealed class CustomizePlusIpc
{
    private static readonly string[] ApplyGateNames =
    {
        "CustomizePlus.ApplyProfileJson",
        "CustomizePlus.Api/ApplyProfileJson",
    };

    private static readonly string[] ExportGateNames =
    {
        "CustomizePlus.ExportCurrentProfileJson",
    };

    private readonly IPluginLog _log;
    private readonly ICallGateSubscriber<string, object?>? _applyGate;
    private readonly ICallGateSubscriber<string>? _exportGate;
    private string? _lastAppliedJson;

    public CustomizePlusIpc(IDalamudPluginInterface pluginInterface, IPluginLog log)
    {
        if (pluginInterface == null)
        {
            throw new ArgumentNullException(nameof(pluginInterface));
        }

        _log = log ?? throw new ArgumentNullException(nameof(log));

        _applyGate = TryGetApplyGate(pluginInterface);
        _exportGate = TryGetExportGate(pluginInterface);
        Available = _applyGate != null;
    }

    public bool Available { get; }

    public string? LastAppliedJson => _lastAppliedJson;

    public void ApplyProfileJson(string json)
    {
        if (!Available || _applyGate == null || string.IsNullOrWhiteSpace(json))
        {
            return;
        }

        try
        {
            _applyGate.InvokeAction(json);
            _lastAppliedJson = json;
        }
        catch (Exception ex)
        {
            _log.Warning(ex, "Customize+ ApplyProfileJson failed");
        }
    }

    public string? TryExportProfileJson()
    {
        if (_exportGate == null)
        {
            return _lastAppliedJson;
        }

        try
        {
            var json = _exportGate.InvokeFunc();
            if (!string.IsNullOrWhiteSpace(json))
            {
                _lastAppliedJson = json;
                return json;
            }
        }
        catch (IpcError)
        {
            // Gate exists but failed; fall back to last applied JSON.
        }
        catch (Exception ex)
        {
            _log.Debug(ex, "Customize+ export failed");
        }

        return _lastAppliedJson;
    }

    private ICallGateSubscriber<string, object?>? TryGetApplyGate(IDalamudPluginInterface pluginInterface)
    {
        foreach (var name in ApplyGateNames)
        {
            try
            {
                var gate = pluginInterface.GetIpcSubscriber<string, object?>(name);
                if (gate != null)
                {
                    return gate;
                }
            }
            catch (IpcNotReadyError)
            {
                // Try next name; plugin not ready yet.
            }
            catch (IpcError)
            {
                // Try next name.
            }
            catch (Exception ex)
            {
                _log.Debug(ex, "Customize+ apply gate {Gate} unavailable", name);
            }
        }

        return null;
    }

    private ICallGateSubscriber<string>? TryGetExportGate(IDalamudPluginInterface pluginInterface)
    {
        foreach (var name in ExportGateNames)
        {
            try
            {
                return pluginInterface.GetIpcSubscriber<string>(name);
            }
            catch (IpcNotReadyError)
            {
            }
            catch (IpcError)
            {
            }
            catch (Exception ex)
            {
                _log.Debug(ex, "Customize+ export gate {Gate} unavailable", name);
            }
        }

        return null;
    }
}
