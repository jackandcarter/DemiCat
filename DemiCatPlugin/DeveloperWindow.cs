using System;
using Dalamud.Bindings.ImGui;

namespace DemiCatPlugin;

public class DeveloperWindow
{
    private readonly Config _config;
    private string _ip;
    private int _port;

    public bool IsOpen;

    public DeveloperWindow(Config config)
    {
        _config = config;
        var uri = new Uri(config.ServerAddress);
        _ip = uri.Host;
        _port = uri.Port;
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

        if (ImGui.Button("Save"))
        {
            _config.ServerAddress = $"http://{_ip}:{_port}";
            PluginServices.PluginInterface.SavePluginConfig(_config);
        }

        ImGui.End();
    }
}
