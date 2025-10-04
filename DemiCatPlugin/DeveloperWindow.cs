using System;
using System.Threading.Tasks;
using Dalamud.Bindings.ImGui;
using Dalamud.Plugin;

namespace DemiCatPlugin;

public class DeveloperWindow
{
    private readonly Config _config;
    private readonly IDalamudPluginInterface? _pluginInterface;
    private readonly Func<bool> _isIdentityReady;
    private readonly Func<Task> _hardReload;
    private readonly Action _stopNetworking;
    private string _apiBaseUrl;
    private string _wsPath;

    public bool IsOpen;

    public DeveloperWindow(
        Config config,
        IDalamudPluginInterface? pluginInterface,
        Func<bool> isIdentityReady,
        Func<Task> hardReload,
        Action stopNetworking)
    {
        _config = config;
        _pluginInterface = pluginInterface;
        _isIdentityReady = isIdentityReady;
        _hardReload = hardReload;
        _stopNetworking = stopNetworking;
        _apiBaseUrl = config.ApiBaseUrl;
        _wsPath = config.WebSocketPath;
    }

    public void Draw()
    {
        if (!IsOpen)
        {
            return;
        }

        if (!ImGui.Begin("DemiCat Developer", ref IsOpen))
        {
            ImGui.End();
            return;
        }

        ImGui.InputText("API Base URL", ref _apiBaseUrl, 256);
        ImGui.InputText("WebSocket Path", ref _wsPath, 64);

        if (ImGui.Button("Apply"))
        {
            ApplyApiBaseUrlChange(_apiBaseUrl);
        }

        ImGui.SameLine();

        if (ImGui.Button("Reset to default"))
        {
            ApplyApiBaseUrlChange(Config.DefaultApiBaseUrl);
        }

        ImGui.End();
    }

    private void ApplyApiBaseUrlChange(string candidate)
    {
        var trimmed = candidate.Trim();
        if (string.IsNullOrEmpty(trimmed))
        {
            trimmed = Config.DefaultApiBaseUrl;
        }

        var previousUrl = _config.ApiBaseUrl;
        var urlChanged = !string.Equals(previousUrl, trimmed, StringComparison.OrdinalIgnoreCase);

        _config.ApiBaseUrl = trimmed;
        if (!ApiHelpers.ValidateApiBaseUrl(_config))
        {
            _config.ApiBaseUrl = previousUrl;
            _apiBaseUrl = _config.ApiBaseUrl;
            return;
        }

        _config.WebSocketPath = _wsPath;
        _pluginInterface?.SavePluginConfig(_config);

        _apiBaseUrl = _config.ApiBaseUrl;

        if (!urlChanged)
        {
            return;
        }

        if (_isIdentityReady())
        {
            _ = _hardReload();
        }
        else
        {
            _stopNetworking();
        }
    }
}
