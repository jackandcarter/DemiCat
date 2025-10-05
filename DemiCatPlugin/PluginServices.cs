using System;
using System.Linq;
using Dalamud.Game.ClientState;
using Dalamud.Game.Command;
using Dalamud.Game.Gui.Toast;
using Dalamud.IoC;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;

namespace DemiCatPlugin;

internal class PluginServices
{
    private IDalamudPluginInterface _pluginInterface = null!;
    private IPluginLog? _log;
    private bool _textureReadbackResolved;
    private Exception? _textureReadbackFailure;

    internal static PluginServices? Instance { get; private set; }

    [PluginService]
    internal IDalamudPluginInterface PluginInterface
    {
        get => _pluginInterface;
        private set
        {
            _pluginInterface = value;
            TryResolveTextureReadbackProvider();
        }
    }

    [PluginService]
    internal IClientState ClientState { get; private set; } = null!;

    [PluginService]
    internal ITextureProvider TextureProvider { get; private set; } = null!;

    internal ITextureReadbackProvider? TextureReadbackProvider { get; private set; }
        = null;

    [PluginService]
    internal IFramework Framework { get; private set; } = null!;

    [PluginService]
    internal IPluginLog Log
    {
        get => _log ?? throw new InvalidOperationException("Plugin log has not been initialized yet.");
        private set
        {
            _log = value;
            TryResolveTextureReadbackProvider();
            FlushDeferredTextureReadbackFailure();
        }
    }

    [PluginService]
    internal IDataManager DataManager { get; private set; } = null!;

    [PluginService]
    internal IToastGui ToastGui { get; private set; } = null!;

    [PluginService]
    internal IChatGui ChatGui { get; private set; } = null!;

    [PluginService]
    internal ICommandManager CommandManager { get; private set; } = null!;

    internal ProgressOverlay? ProgressOverlay { get; set; }

    public PluginServices()
    {
        Instance = this;
    }

    private void TryResolveTextureReadbackProvider()
    {
        if (_textureReadbackResolved || _pluginInterface == null)
            return;

        _textureReadbackResolved = true;

        try
        {
            TextureReadbackProvider = _pluginInterface.Create<ITextureReadbackProvider>();
        }
        catch (Exception ex) when (IsServiceUnavailable(ex))
        {
            TextureReadbackProvider = null;
        }
        catch (Exception ex)
        {
            TextureReadbackProvider = null;
            _textureReadbackFailure = ex;
            FlushDeferredTextureReadbackFailure();
        }
    }

    private void FlushDeferredTextureReadbackFailure()
    {
        if (_textureReadbackFailure == null || _log == null)
            return;

        _log.Warning(
            _textureReadbackFailure,
            "Failed to resolve ITextureReadbackProvider; falling back to HTTP-based icons.");
        _textureReadbackFailure = null;
    }

    private static bool IsServiceUnavailable(Exception exception)
    {
        Exception current = exception;
        while (current is AggregateException aggregate && aggregate.InnerExceptions.Count == 1)
            current = aggregate.InnerExceptions.Single();

        return current is InvalidOperationException invalidOperation
               && invalidOperation.Message.IndexOf("could not be found", StringComparison.OrdinalIgnoreCase) >= 0;
    }
}
