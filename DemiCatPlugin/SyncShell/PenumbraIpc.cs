using System;
using Dalamud.Plugin.Ipc;
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
            Available = _apiAvailable.InvokeFunc();
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
        if (!Available || _setTemporaryMod == null)
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

    public void Redraw(int objectIndex)
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
}
