using System;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Numerics;
using System.Threading.Tasks;
using Dalamud.Bindings.ImGui;

namespace DemiCatPlugin;

public class SettingsWindow : IDisposable
{
    private readonly Config _config;
    private readonly HttpClient _httpClient = new();
    private readonly Func<Task> _refreshRoles;
    private readonly DeveloperWindow _devWindow;

    private string _key = string.Empty;
    private bool _invalidKey;

    public bool IsOpen;

    public SettingsWindow(Config config, Func<Task> refreshRoles)
    {
        _config = config;
        _refreshRoles = refreshRoles;
        _key = config.AuthToken ?? string.Empty;
        _devWindow = new DeveloperWindow(config);
    }

    public void Draw()
    {
        if (IsOpen)
        {
            if (ImGui.Begin("DemiCat Settings", ref IsOpen))
            {
                ImGui.InputText("Key", ref _key, 64);
                ImGui.SameLine();
                ImGui.TextDisabled("\u03C0");
                var io = ImGui.GetIO();
                if (ImGui.IsItemClicked() && io.KeyCtrl && io.KeyShift)
                {
                    _devWindow.IsOpen = true;
                }

                if (ImGui.Button("Sync"))
                {
                    _ = Sync();
                }

                if (_invalidKey)
                {
                    ImGui.TextColored(new Vector4(1, 0, 0, 1), "Invalid key");
                }
                ImGui.End();
            }
            else
            {
                ImGui.End();
            }
        }

        _devWindow.Draw();
    }

    private async Task Sync()
    {
        try
        {
            var url = $"{_config.ServerAddress.TrimEnd('/')}/validate";
            var request = new HttpRequestMessage(HttpMethod.Post, url)
            {
                Content = new StringContent(JsonSerializer.Serialize(new { key = _key }), Encoding.UTF8, "application/json")
            };

            var response = await _httpClient.SendAsync(request);
            if (response.IsSuccessStatusCode)
            {
                _invalidKey = false;
                _config.AuthToken = _key;
                SaveConfig();
                _ = _refreshRoles();
            }
            else
            {
                _invalidKey = true;
            }
        }
        catch
        {
            _invalidKey = true;
        }
    }

    private void SaveConfig()
    {
        PluginServices.PluginInterface.SavePluginConfig(_config);
    }

    public void Dispose()
    {
        _httpClient.Dispose();
    }
}
