using System;
using Dalamud.Bindings.ImGui;

namespace DemiCatPlugin;

public class DeveloperWindow
{
    private readonly Config _config;
    private string _apiBaseUrl;
    private string _wsPath;

    public bool IsOpen;

    public DeveloperWindow(Config config)
    {
        _config = config;
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
            PluginServices.PluginInterface.SavePluginConfig(_config);
        }

        ImGui.End();
    }
}
