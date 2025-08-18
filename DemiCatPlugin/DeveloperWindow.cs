using System;
using Dalamud.Bindings.ImGui;
using Dalamud.Plugin;

namespace DemiCatPlugin;

public class DeveloperWindow
{
    private readonly Config _config;
    private readonly IDalamudPluginInterface? _pluginInterface;
    private string _apiBaseUrl;
    private string _wsPath;

    public bool IsOpen;

    public DeveloperWindow(Config config, IDalamudPluginInterface? pluginInterface)
    {
        _config = config;
        _pluginInterface = pluginInterface;
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

        if (ImGui.Button("Save"))
        {
            _config.ApiBaseUrl = _apiBaseUrl;
            _config.WebSocketPath = _wsPath;

            if (_pluginInterface is { IsDisposed: false })
                _pluginInterface!.SavePluginConfig(_config);
        }

        ImGui.End();
    }
}
