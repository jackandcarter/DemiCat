using System;
using System.Net.Http;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Numerics;
using System.Threading.Tasks;
using Dalamud.Bindings.ImGui;

namespace DemiCatPlugin;

public class SettingsWindow : IDisposable
{
    private readonly Config _config;
    private readonly HttpClient _httpClient;
    private readonly Func<Task> _refreshRoles;
    private readonly DeveloperWindow _devWindow;

    private string _apiKey = string.Empty;
    private bool _authFailed;
    private bool _networkError;

    public bool IsOpen;

    public SettingsWindow(Config config, HttpClient httpClient, Func<Task> refreshRoles)
    {
        _config = config;
        _httpClient = httpClient;
        _refreshRoles = refreshRoles;
        _apiKey = config.AuthToken ?? string.Empty;
        _devWindow = new DeveloperWindow(config);
    }

    public void Draw()
    {
        if (IsOpen)
        {
            if (ImGui.Begin("DemiCat Settings", ref IsOpen))
            {
                ImGui.InputText("API Key", ref _apiKey, 64);
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

                if (_authFailed)
                {
                    ImGui.TextColored(new Vector4(1, 0, 0, 1), "Authentication failed");
                }
                else if (_networkError)
                {
                    ImGui.TextColored(new Vector4(1, 0, 0, 1), "Network error");
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
        _authFailed = false;
        _networkError = false;

        try
        {
            _apiKey = _apiKey.Trim();
            var key = _apiKey;
            var url = $"{_config.ServerAddress.TrimEnd('/')}/validate";
            var request = new HttpRequestMessage(HttpMethod.Post, url)
            {
                Content = new StringContent(JsonSerializer.Serialize(new { key }), Encoding.UTF8, "application/json")
            };
            if (!string.IsNullOrEmpty(key))
            {
                request.Headers.Add("X-Api-Key", key);
            }

            var response = await _httpClient.SendAsync(request);
            if (response.IsSuccessStatusCode)
            {
                PluginServices.PluginLog.Info("API key validated successfully.");
                _config.AuthToken = key;
                _apiKey = key;
                SaveConfig();
                _ = _refreshRoles();
            }
            else if (response.StatusCode == HttpStatusCode.Unauthorized)
            {
                PluginServices.PluginLog.Warning("API key validation failed: unauthorized.");
                _authFailed = true;
            }
            else
            {
                PluginServices.PluginLog.Warning($"API key validation failed with status {response.StatusCode}.");
                _networkError = true;
            }
        }
        catch (Exception ex)
        {
            PluginServices.PluginLog.Error(ex, "Error validating API key.");
            _networkError = true;
        }
    }

    private void SaveConfig()
    {
        PluginServices.PluginInterface.SavePluginConfig(_config);
    }

    public void Dispose()
    {
    }
}
