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
    private readonly Func<Task<bool>> _refreshRoles;
    private readonly Func<Task> _startNetworking;
    private readonly DeveloperWindow _devWindow;
    private readonly IPluginLog _log;

    private string _apiKey = string.Empty;
    private string _apiBaseUrl = string.Empty;
    private string _syncStatus = string.Empty;
    private bool _syncInProgress;

    public bool IsOpen;

    public MainWindow? MainWindow { get; set; }
    public ChatWindow? ChatWindow { get; set; }
    public OfficerChatWindow? OfficerChatWindow { get; set; }

    public SettingsWindow(Config config, HttpClient httpClient, Func<Task<bool>> refreshRoles, Func<Task> startNetworking, IPluginLog log, IDalamudPluginInterface pluginInterface)
    {
        _config = config;
        _httpClient = httpClient;
        _refreshRoles = refreshRoles;
        _startNetworking = startNetworking;
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

                // Allow room for longer server-generated API keys
                ImGui.InputText("API Key", ref _apiKey, 256);
                ImGui.SameLine();
                ImGui.TextDisabled("\u03C0");
                var io = ImGui.GetIO();
                if (ImGui.IsItemClicked() && io.KeyCtrl && io.KeyShift)
                {
                    _devWindow.IsOpen = true;
                }

                var enableFc = _config.EnableFcChat;
                if (ImGui.Checkbox("Enable FC Chat", ref enableFc))
                {
                    _config.EnableFcChat = enableFc;
                    SaveConfig();
                    if (ChatWindow != null) ChatWindow.ChannelsLoaded = false;
                }

                if (ImGui.Button("Sync") && !_syncInProgress)
                {
                    _syncInProgress = true;
                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            await Sync();
                        }
                        catch (Exception ex)
                        {
                            _log.Error(ex, "Unexpected error during sync");
                        }
                        finally
                        {
                            _syncInProgress = false;
                        }
                    });
                }

                if (!string.IsNullOrEmpty(_syncStatus))
                {
                    if (_syncStatus == "API key validated")
                    {
                        ImGui.TextColored(new Vector4(0, 1, 0, 1), _syncStatus);
                    }
                    else if (_syncStatus == "Authentication failed" || _syncStatus == "Network error" || _syncStatus == "Roles sync failed")
                    {
                        ImGui.TextColored(new Vector4(1, 0, 0, 1), _syncStatus);
                    }
                    else
                    {
                        ImGui.Text(_syncStatus);
                    }
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
        var framework = PluginServices.Instance?.Framework;
        if (framework == null)
        {
            _log.Error("Cannot sync: framework is not available.");
            PluginServices.Instance?.Framework?.RunOnTick(() => _syncStatus = "Network error");
            return;
        }

        if (_httpClient == null)
        {
            _log.Error("Cannot sync: HTTP client is not initialized.");
            _ = framework.RunOnTick(() => _syncStatus = "Network error");
            return;
        }

        if (!ApiHelpers.ValidateApiBaseUrl(_config))
        {
            _ = framework.RunOnTick(() => _syncStatus = "Network error");
            return;
        }

        if (PluginServices.Instance?.PluginInterface == null)
        {
            _log.Error("Cannot sync: plugin interface is not available.");
            _ = framework.RunOnTick(() => _syncStatus = "Network error");
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

            _ = framework.RunOnTick(() => _syncStatus = "Validating API key...");
            var response = await _httpClient.SendAsync(request);
            var responseBody = await response.Content.ReadAsStringAsync();
            _log.Info($"Response Status: {response.StatusCode}");
            _log.Info($"Response Body: {responseBody}");
            if (response.IsSuccessStatusCode)
            {
                _log.Info("API key validated successfully.");
                _config.AuthToken = key;
                _apiKey = key;
                SaveConfig();

                bool rolesRefreshed = false;
                if (_refreshRoles != null)
                {
                    try
                    {
                        rolesRefreshed = await _refreshRoles();
                        if (!rolesRefreshed)
                        {
                            _log.Warning("Role refresh after key validation reported failure.");
                        }
                    }
                    catch (Exception ex)
                    {
                        _log.Error(ex, "Failed to refresh roles after key validation.");
                        rolesRefreshed = false;
                    }
                }
                else
                {
                    _log.Warning("RefreshRoles delegate is not set; roles will not be refreshed.");
                }

                await _startNetworking();
                MainWindow?.Ui.ResetChannels();
                var presence = ChatWindow?.Presence ?? OfficerChatWindow?.Presence;
                presence?.Reset();
                presence?.Reload();
                if (ChatWindow != null) ChatWindow.ChannelsLoaded = false;
                if (OfficerChatWindow != null) OfficerChatWindow.ChannelsLoaded = false;
                _ = framework.RunOnTick(() => _syncStatus = rolesRefreshed ? "API key validated" : "Roles sync failed");
            }
            else if (response.StatusCode == HttpStatusCode.Unauthorized)
            {
                _log.Warning($"API key validation failed: unauthorized. Response Body: {responseBody}");
                _ = framework.RunOnTick(() => _syncStatus = "Authentication failed");
            }
            else
            {
                _log.Warning($"API key validation failed with status {response.StatusCode}. Response Body: {responseBody}");
                _ = framework.RunOnTick(() => _syncStatus = "Network error");
            }
        }
        catch (Exception ex)
        {
            _log.Error(ex, "Error validating API key.");
            _ = framework.RunOnTick(() => _syncStatus = "Network error");
            return;
        }
    }

    private void SaveConfig()
    {
        var services = PluginServices.Instance;
        if (services?.PluginInterface == null)
        {
            _log.Error("Plugin interface is not available; cannot save configuration.");
            return;
        }

        if (services.Framework == null)
        {
            _log.Error("Framework is not available; cannot save configuration.");
            return;
        }

        _ = services.Framework.RunOnTick(() => services.PluginInterface.SavePluginConfig(_config));
    }

    public void Dispose()
    {
    }
}
