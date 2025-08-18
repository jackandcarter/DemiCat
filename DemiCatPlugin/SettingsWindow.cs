using System;
using System.Net.Http;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Numerics;
using System.Threading.Tasks;
using System.Linq;
using Dalamud.Bindings.ImGui;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;

namespace DemiCatPlugin;

public class SettingsWindow : IDisposable
{
    private readonly Config _config;
    private readonly HttpClient _httpClient;
    private readonly Func<Task> _refreshRoles;
    private readonly DeveloperWindow _devWindow;
    private readonly IPluginLog _log;

    private string _apiKey = string.Empty;
    private string _apiBaseUrl = string.Empty;
    private bool _authFailed;
    private bool _networkError;

    public bool IsOpen;

    public SettingsWindow(Config config, HttpClient httpClient, Func<Task> refreshRoles, IPluginLog log, IDalamudPluginInterface pluginInterface)
    {
        _config = config;
        _httpClient = httpClient;
        _refreshRoles = refreshRoles;
        _apiKey = config.AuthToken ?? string.Empty;
        _apiBaseUrl = config.ApiBaseUrl;
        _devWindow = new DeveloperWindow(config, pluginInterface);
        _log = log;
    }

    public void Draw()
    {
        if (IsOpen)
        {
            if (ImGui.Begin("DemiCat Settings", ref IsOpen))
            {
                if (ImGui.InputText("API Base URL", ref _apiBaseUrl, 256))
                {
                    _config.ApiBaseUrl = _apiBaseUrl;
                    SaveConfig();
                }

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
                    Task.Run(Sync).ContinueWith(t =>
                    {
                        _log.Error(t.Exception!, "Unexpected error during sync");
                    }, TaskContinuationOptions.OnlyOnFaulted);
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

        if (_httpClient == null)
        {
            _log.Error("Cannot sync: HTTP client is not initialized.");
            _networkError = true;
            return;
        }

        if (string.IsNullOrEmpty(_config.ApiBaseUrl))
        {
            _log.Error("Cannot sync: API base URL is not configured.");
            _networkError = true;
            return;
        }

        if (PluginServices.Instance?.PluginInterface == null)
        {
            _log.Error("Cannot sync: plugin interface is not available.");
            _networkError = true;
            return;
        }

        try
        {
            _apiKey = _apiKey.Trim();
            var key = _apiKey;
            var url = $"{_config.ApiBaseUrl.TrimEnd('/')}/validate";
            var request = new HttpRequestMessage(HttpMethod.Post, url)
            {
                Content = new StringContent(JsonSerializer.Serialize(new { key }), Encoding.UTF8, "application/json")
            };
            if (!string.IsNullOrEmpty(key))
            {
                request.Headers.Add("X-Api-Key", key);
            }

            _log.Info($"Sync URL: {url}");
            _log.Info($"Headers: {string.Join(", ", request.Headers.Select(h => $"{h.Key}: {string.Join(";", h.Value)}"))}");

            var response = await _httpClient.SendAsync(request);
            _log.Info($"Response Status: {response.StatusCode}");
            if (response.IsSuccessStatusCode)
            {
                _log.Info("API key validated successfully.");
                _config.AuthToken = key;
                _apiKey = key;
                SaveConfig();

                if (_refreshRoles != null)
                {
                    try
                    {
                        await _refreshRoles();
                    }
                    catch (Exception ex)
                    {
                        _log.Error(ex, "Failed to refresh roles after key validation.");
                    }
                }
                else
                {
                    _log.Warning("RefreshRoles delegate is not set; roles will not be refreshed.");
                }
            }
            else if (response.StatusCode == HttpStatusCode.Unauthorized)
            {
                _log.Warning("API key validation failed: unauthorized.");
                _authFailed = true;
            }
            else
            {
                _log.Warning($"API key validation failed with status {response.StatusCode}.");
                _networkError = true;
            }
        }
        catch (Exception ex)
        {
            _log.Error(ex, "Error validating API key.");
            _networkError = true;
            return;
        }
    }

    private void SaveConfig()
    {
        var pluginInterface = PluginServices.Instance?.PluginInterface;
        if (pluginInterface == null)
        {
            _log.Error("Plugin interface is not available; cannot save configuration.");
            return;
        }
        pluginInterface.SavePluginConfig(_config);
    }

    public void Dispose()
    {
    }
}
