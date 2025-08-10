using System;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Dalamud.Bindings.ImGui;

namespace DemiCatPlugin;

public class SettingsWindow : IDisposable
{
    private readonly Config _config;
    private readonly HttpClient _httpClient = new();
    private string _key = string.Empty;
    public bool IsOpen;

    public SettingsWindow(Config config)
    {
        _config = config;
        _key = config.AuthToken ?? string.Empty;
    }

    public void Draw()
    {
        if (!IsOpen)
        {
            return;
        }

        if (!ImGui.Begin("DemiCat Settings", ref IsOpen))
        {
            ImGui.End();
            return;
        }

        ImGui.InputText("Key", ref _key, 64);
        if (ImGui.Button("Validate"))
        {
            ValidateKey();
        }

        var fc = _config.EnableFcChat;
        if (ImGui.Checkbox("Enable FC Chat", ref fc))
        {
            _config.EnableFcChat = fc;
            SaveConfig();
        }

        ImGui.End();
    }

    private async void ValidateKey()
    {
        try
        {
            var character = PluginServices.ClientState.LocalPlayer?.Name ?? string.Empty;
            var url = $"{_config.HelperBaseUrl.TrimEnd('/')}/validate";
            var request = new HttpRequestMessage(HttpMethod.Post, url)
            {
                Content = new StringContent(
                    JsonSerializer.Serialize(new { key = _key, characterName = character }),
                    Encoding.UTF8,
                    "application/json")
            };

            var response = await Task.Run(() => _httpClient.SendAsync(request));

            if (response.IsSuccessStatusCode)
            {
                PluginServices.Framework.RunOnTick(() =>
                {
                    _config.AuthToken = _key;
                    SaveConfig();
                });
            }
        }
        catch
        {
            // ignored
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

