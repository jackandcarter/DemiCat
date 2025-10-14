using System;
using Dalamud.Plugin;
using Dalamud.Plugin.Ipc;
using Dalamud.Plugin.Ipc.Exceptions;
using Dalamud.Plugin.Services;
using Penumbra.Api.Enums;

namespace DemiCatPlugin.SyncShell;

public sealed class PenumbraIpc
{
    private readonly IDalamudPluginInterface _pluginInterface;
    private readonly IPluginLog _log;

    private readonly ICallGateSubscriber<bool>? _apiAvailable;
    private readonly ICallGateSubscriber<string>? _modDirectory;
    private readonly ICallGateSubscriber<string, object?>? _setTemporaryMod;
    private readonly ICallGateSubscriber<(int, int), object?>? _redrawObject;

    public PenumbraIpc(IDalamudPluginInterface pluginInterface, IPluginLog log)
    {
        _pluginInterface = pluginInterface ?? throw new ArgumentNullException(nameof(pluginInterface));
        _log = log ?? throw new ArgumentNullException(nameof(log));

        try
        {
            _apiAvailable = _pluginInterface.GetIpcSubscriber<bool>("Penumbra.ApiEnabled");
            _modDirectory = _pluginInterface.GetIpcSubscriber<string>("Penumbra.ModDirectory");
            _setTemporaryMod = _pluginInterface.GetIpcSubscriber<string, object?>("Penumbra.Api/SetTemporaryMod");
            _redrawObject = _pluginInterface.GetIpcSubscriber<(int, int), object?>("Penumbra.Api/RedrawObject");

            var apiEnabled = TryInvokeApiEnabled();
            Available = apiEnabled
                && _setTemporaryMod != null
                && _redrawObject != null;
        }
        catch (IpcNotReadyError)
        {
            Available = false;
            _log.Information("Penumbra IPC is not ready yet; integration features will stay disabled until Penumbra finishes loading.");
        }
        catch (Dalamud.Plugin.Ipc.Exceptions.IpcError)
        {
            Available = false;
            _log.Information("Penumbra IPC is unavailable. Penumbra may not be installed or its API support is disabled.");
        }
        catch (Exception ex)
        {
            Available = false;
            _log.Information(ex, "Penumbra IPC unavailable");
        }
    }

    public bool Available { get; }

    public string? GetModDirectory()
    {
        if (!Available || _modDirectory == null)
        {
            return null;
        }

        try
        {
            return _modDirectory.InvokeFunc();
        }
        catch (Exception ex)
        {
            _log.Warning(ex, "Failed to query Penumbra mod directory");
            return null;
        }
    }

    public void SetTemporaryMod(string modPath)
    {
        if (!Available || _setTemporaryMod == null || string.IsNullOrWhiteSpace(modPath))
        {
            return;
        }

        try
        {
            _setTemporaryMod.InvokeAction(modPath);
        }
        catch (Exception ex)
        {
            _log.Warning(ex, "Failed to set temporary Penumbra mod");
        }
    }

    public void RedrawObject(int objectIndex)
    {
        if (!Available || _redrawObject == null)
        {
            return;
        }

        try
        {
            _redrawObject.InvokeAction((objectIndex, (int)RedrawType.Redraw));
        }
        catch (Exception ex)
        {
            _log.Warning(ex, "Failed to redraw object {ObjectIndex}", objectIndex);
        }
    }

    private bool TryInvokeApiEnabled()
    {
        if (_apiAvailable == null)
        {
            return false;
        }

        try
        {
            return _apiAvailable.InvokeFunc();
        }
        catch (IpcNotReadyError)
        {
            throw;
        }
        catch (Dalamud.Plugin.Ipc.Exceptions.IpcError)
        {
            throw;
        }
        catch (Exception ex)
        {
            _log.Information(ex, "Failed to determine Penumbra IPC availability");
            return false;
        }
    }
}
