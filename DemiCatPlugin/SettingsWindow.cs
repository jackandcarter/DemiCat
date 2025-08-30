using System;
using System.Collections.Generic;
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
    private readonly Dictionary<string, bool> _categoryToggles = new();
    private bool _settingsLoaded;

    public bool IsOpen;

    public MainWindow? MainWindow { get; set; }
    public ChatWindow? ChatWindow { get; set; }
    public OfficerChatWindow? OfficerChatWindow { get; set; }
    public ChannelWatcher? ChannelWatcher { get; set; }
    public RequestWatcher? RequestWatcher { get; set; }

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
                if (!_settingsLoaded)
                {
                    _settingsLoaded = true;
                    _ = Task.Run(LoadSettings);
                }

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
                    _config.EnableFcChatUserSet = true;
                    SaveConfig();
                    if (ChatWindow != null) ChatWindow.ChannelsLoaded = false;
                }

                var syncEnabled = _config.SyncEnabled;
                if (ImGui.Checkbox("Enable Sync", ref syncEnabled))
                {
                    _config.SyncEnabled = syncEnabled;
                    SaveConfig();
                    _ = Task.Run(PushSettings);
                }

                var paused = !_config.Enabled;
                if (ImGui.Checkbox("Pause", ref paused))
                {
                    _config.Enabled = !paused;
                    SaveConfig();
                }

                foreach (var kvp in _categoryToggles.ToList())
                {
                    var enabled = kvp.Value;
                    if (ImGui.Checkbox($"{kvp.Key}##cat", ref enabled))
                    {
                        _categoryToggles[kvp.Key] = enabled;
                        _ = Task.Run(PushSettings);
                    }

                    ImGui.SameLine();
                    var autoApply = _config.AutoApply.TryGetValue(kvp.Key, out var ap) && ap;
                    if (ImGui.Checkbox($"Auto-apply##{kvp.Key}", ref autoApply))
                    {
                        _config.AutoApply[kvp.Key] = autoApply;
                        SaveConfig();
                        _ = Task.Run(PushSettings);
                    }
                }

                if (ImGui.Button("Clear my cached data"))
                {
                    ClearCachedData();
                }

                ImGui.SameLine();
                if (ImGui.Button("Forget me"))
                {
                    _ = Task.Run(ForgetMe);
                }

                ImGui.BeginDisabled(_syncInProgress);
                if (ImGui.Button("Sync"))
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
                    });
                }
                ImGui.EndDisabled();

                if (!string.IsNullOrEmpty(_syncStatus))
                {
                    Vector4 color;
                    if (_syncStatus == "API key validated")
                    {
                        color = new Vector4(0, 1, 0, 1);
                    }
                    else if (_syncStatus == "Authentication failed" || _syncStatus == "Network error")
                    {
                        color = new Vector4(1, 0, 0, 1);
                    }
                    else
                    {
                        color = new Vector4(1, 1, 1, 1);
                    }

                    ImGui.TextColored(color, _syncStatus);
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

    private async Task LoadSettings()
    {
        try
        {
            if (_httpClient == null || string.IsNullOrEmpty(_config.AuthToken) || !ApiHelpers.ValidateApiBaseUrl(_config))
                return;

            var url = $"{_config.ApiBaseUrl.TrimEnd('/')}/api/users/me/settings";
            var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Add("X-Api-Key", _config.AuthToken);
            var response = await _httpClient.SendAsync(request);
            if (!response.IsSuccessStatusCode)
                return;

            var body = await response.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.TryGetProperty("consent_sync", out var consent))
            {
                _config.SyncEnabled = consent.GetBoolean();
            }

            if (doc.RootElement.TryGetProperty("settings", out var settings) && settings.ValueKind == JsonValueKind.Object)
            {
                if (settings.TryGetProperty("autoApply", out var autoApply) && autoApply.ValueKind == JsonValueKind.Object)
                {
                    foreach (var prop in autoApply.EnumerateObject())
                    {
                        _config.AutoApply[prop.Name] = prop.Value.GetBoolean();
                    }
                }

                if (settings.TryGetProperty("categories", out var cats) && cats.ValueKind == JsonValueKind.Object)
                {
                    foreach (var prop in cats.EnumerateObject())
                    {
                        var enabled = prop.Value.ValueKind == JsonValueKind.True
                            || (prop.Value.TryGetProperty("enabled", out var en) && en.GetBoolean());
                        _categoryToggles[prop.Name] = enabled;
                    }
                }
            }

            SaveConfig();
        }
        catch (Exception ex)
        {
            _log.Error(ex, "Failed to load settings");
        }
    }

    private async Task PushSettings()
    {
        try
        {
            if (_httpClient == null || string.IsNullOrEmpty(_config.AuthToken) || !ApiHelpers.ValidateApiBaseUrl(_config))
                return;

            var url = $"{_config.ApiBaseUrl.TrimEnd('/')}/api/users/me/settings";
            var payload = new
            {
                settings = new
                {
                    categories = _categoryToggles,
                    autoApply = _config.AutoApply
                },
                consent_sync = _config.SyncEnabled
            };

            var request = new HttpRequestMessage(HttpMethod.Put, url)
            {
                Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json")
            };
            request.Headers.Add("X-Api-Key", _config.AuthToken);
            await _httpClient.SendAsync(request);
        }
        catch (Exception ex)
        {
            _log.Error(ex, "Failed to push settings");
        }
    }

    private void ClearCachedData()
    {
        _config.Categories.Clear();
        SaveConfig();
        SyncshellWindow.Instance?.ClearCaches();
    }

    private async Task ForgetMe()
    {
        var framework = PluginServices.Instance?.Framework;
        try
        {
            if (_httpClient == null || string.IsNullOrEmpty(_config.AuthToken) || !ApiHelpers.ValidateApiBaseUrl(_config))
                return;

            var url = $"{_config.ApiBaseUrl.TrimEnd('/')}/api/users/me/forget";
            var request = new HttpRequestMessage(HttpMethod.Post, url);
            request.Headers.Add("X-Api-Key", _config.AuthToken);
            var response = await _httpClient.SendAsync(request);
            if (response.IsSuccessStatusCode)
            {
                _config.AuthToken = null;
                _apiKey = string.Empty;
                ClearCachedData();
                if (framework != null)
                    _ = framework.RunOnTick(() => _syncStatus = "User forgotten");
            }
        }
        catch (Exception ex)
        {
            _log.Error(ex, "Failed to forget user");
            if (framework != null)
                _ = framework.RunOnTick(() => _syncStatus = "Network error");
        }
    }

    private async Task Sync()
    {
        var framework = PluginServices.Instance?.Framework;
        try
        {
            if (framework == null)
            {
                _log.Error("Cannot sync: framework is not available.");
                if (framework != null)
                    _ = framework.RunOnTick(() => _syncStatus = "Network error");
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
                if (string.IsNullOrWhiteSpace(_apiKey))
                {
                    _ = framework.RunOnTick(() => _syncStatus = "API key required");
                    return;
                }

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
                    var newKey = key;
                    _config.AuthToken = newKey;
                    _apiKey = newKey;
                    SaveConfig();
                    _ = framework.RunOnTick(() => _syncStatus = "API key validated");

                    try
                    {
                        MainWindow?.ReloadSignupPresets();
                    }
                    catch (Exception ex)
                    {
                        _log.Error(ex, "Failed to reload signup presets after key validation.");
                    }

                    if (_refreshRoles != null)
                    {
                        try
                        {
                            var rolesRefreshed = await _refreshRoles();
                            if (!rolesRefreshed)
                            {
                                _log.Warning("Role refresh after key validation reported failure.");
                            }
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

                    try
                    {
                        await _startNetworking();
                    }
                    catch (Exception ex)
                    {
                        _log.Error(ex, "Failed to start networking after key validation.");
                    }

                    try
                    {
                        if (ChannelWatcher != null)
                        {
                            await ChannelWatcher.Start();
                        }
                        RequestWatcher?.Start();
                    }
                    catch (Exception ex)
                    {
                        _log.Error(ex, "Failed to start ChannelWatcher.");
                    }

                    try
                    {
                        ChatWindow?.StartNetworking();
                    }
                    catch (Exception ex)
                    {
                        _log.Error(ex, "Failed to start ChatWindow networking.");
                    }

                    try
                    {
                        OfficerChatWindow?.StartNetworking();
                    }
                    catch (Exception ex)
                    {
                        _log.Error(ex, "Failed to start OfficerChatWindow networking.");
                    }

                    try
                    {
                        _ = ChatWindow?.RefreshChannels();
                        _ = OfficerChatWindow?.RefreshChannels();
                    }
                    catch (Exception ex)
                    {
                        _log.Error(ex, "Failed to refresh channels.");
                    }

                    try
                    {
                        _ = ChatWindow?.RefreshMessages();
                        _ = OfficerChatWindow?.RefreshMessages();
                    }
                    catch (Exception ex)
                    {
                        _log.Error(ex, "Failed to refresh messages.");
                    }

                    try
                    {
                        MainWindow?.Ui.ResetChannels();
                        MainWindow?.ResetEventCreateRoles();
                    }
                    catch (Exception ex)
                    {
                        _log.Error(ex, "Failed to reset UI or event create roles.");
                    }

                    try
                    {
                        var presence = ChatWindow?.Presence ?? OfficerChatWindow?.Presence;
                        presence?.Reset();
                        presence?.Reload();
                    }
                    catch (Exception ex)
                    {
                        _log.Error(ex, "Failed to reset or reload presence.");
                    }

                    if (MainWindow != null)
                    {
                        _ = framework.RunOnTick(() => MainWindow.IsOpen = true);
                    }
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
        finally
        {
            _syncInProgress = false;
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
        if (ChannelWatcher != null)
        {
            _ = ChannelWatcher.Start();
        }
        RequestWatcher?.Start();
    }

    public void Dispose()
    {
    }
}
