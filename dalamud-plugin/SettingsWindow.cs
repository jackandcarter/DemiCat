using System;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using ImGuiNET;

namespace DalamudPlugin;

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

        ImGui.End();
    }

    private async void ValidateKey()
    {
        try
        {
            var url = $"{_config.HelperBaseUrl.TrimEnd('/')}/validate";
            var request = new HttpRequestMessage(HttpMethod.Post, url);
            var character = Service.ClientState.LocalPlayer?.Name ?? string.Empty;
            request.Content = new StringContent(JsonSerializer.Serialize(new { key = _key, characterName = character }), Encoding.UTF8, "application/json");
            var response = await _httpClient.SendAsync(request);
            if (response.IsSuccessStatusCode)
            {
                _config.AuthToken = _key;
                SaveConfig();
            }
        }
        catch
        {
            // ignored
        }
    }

    private void SaveConfig()
    {
        try
        {
            var path = Path.Combine(AppContext.BaseDirectory, "config.json");
            File.WriteAllText(path, JsonSerializer.Serialize(_config));
        }
        catch
        {
            // ignored
        }
    }

    public void Dispose()
    {
        _httpClient.Dispose();
    }
}

