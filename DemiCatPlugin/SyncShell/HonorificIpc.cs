using System;
using Dalamud.Plugin;
using Dalamud.Plugin.Ipc;
using Dalamud.Plugin.Ipc.Exceptions;
using Dalamud.Plugin.Services;

namespace DemiCatPlugin.SyncShell;

public sealed class HonorificIpc
{
    private const string ApplyGateName = "Honorific.Api/SetTitleJson";
    private const string ExportGateName = "Honorific.Api/GetTitleJson";

    private readonly IPluginLog _log;
    private readonly ICallGateSubscriber<string, object?>? _applyGate;
    private readonly ICallGateSubscriber<string>? _exportGate;
    private string? _lastAppliedJson;

    public HonorificIpc(IDalamudPluginInterface pluginInterface, IPluginLog log)
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

    public void SetTitleJson(string json)
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
            _log.Warning(ex, "Honorific SetTitleJson failed");
        }
    }

    public string? TryGetTitleJson()
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
        }
        catch (Exception ex)
        {
            _log.Debug(ex, "Honorific export failed");
        }

        return _lastAppliedJson;
    }

    private ICallGateSubscriber<string, object?>? TryGetApplyGate(IDalamudPluginInterface pluginInterface)
    {
        try
        {
            return pluginInterface.GetIpcSubscriber<string, object?>(ApplyGateName);
        }
        catch (IpcNotReadyError)
        {
        }
        catch (IpcError)
        {
        }
        catch (Exception ex)
        {
            _log.Debug(ex, "Honorific apply gate unavailable");
        }

        return null;
    }

    private ICallGateSubscriber<string>? TryGetExportGate(IDalamudPluginInterface pluginInterface)
    {
        try
        {
            return pluginInterface.GetIpcSubscriber<string>(ExportGateName);
        }
        catch (IpcNotReadyError)
        {
        }
        catch (IpcError)
        {
        }
        catch (Exception ex)
        {
            _log.Debug(ex, "Honorific export gate unavailable");
        }

        return null;
    }
}
