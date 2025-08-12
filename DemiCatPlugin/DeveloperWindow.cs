using System;
using Dalamud.Bindings.ImGui;

namespace DemiCatPlugin;

public class DeveloperWindow
{
    private readonly Config _config;
    private string _ip;
    private int _port;
    private string _wsPath;

    public bool IsOpen;

    public DeveloperWindow(Config config)
    {
        _config = config;
        var uri = new Uri(config.ServerAddress);
        _ip = uri.Host;
        _port = uri.Port;
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

        ImGui.InputText("Server IP", ref _ip, 64);
        ImGui.InputInt("Port", ref _port);
        ImGui.InputText("WebSocket Path", ref _wsPath, 64);

        if (ImGui.Button("Save"))
        {
            _config.ServerAddress = $"http://{_ip}:{_port}";
            _config.WebSocketPath = _wsPath;
            PluginServices.PluginInterface.SavePluginConfig(_config);
        }

        ImGui.End();
    }
}
