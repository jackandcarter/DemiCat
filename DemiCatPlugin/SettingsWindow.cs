using System;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using Dalamud.Bindings.ImGui;

namespace DemiCatPlugin;

public class SettingsWindow : IDisposable
{
    private readonly Config _config;
    private readonly HttpClient _httpClient = new();
    private string _key = string.Empty;
    private string _syncKey = string.Empty;
    private readonly Action _refreshRoles;
    public bool IsOpen;

    public SettingsWindow(Config config, Action refreshRoles)
    {
        _config = config;
        _refreshRoles = refreshRoles;
        _key = config.AuthToken ?? string.Empty;
        _syncKey = config.SyncKey;
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
        ImGui.InputText("Sync Key", ref _syncKey, 64);
        if (ImGui.Button("Connect/Sync"))
        {
            ConnectSync();
        }
        ImGui.SameLine();
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
                _ = PluginServices.Framework.RunOnTick(() =>
                {
                    _config.AuthToken = _key;
                    _config.SyncKey = _syncKey;
                    SaveConfig();
                    _refreshRoles();
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

    private async void ConnectSync()
    {
        try
        {
            var character = PluginServices.ClientState.LocalPlayer?.Name ?? string.Empty;
            var url = $"{_config.HelperBaseUrl.TrimEnd('/')}/validate";
            var request = new HttpRequestMessage(HttpMethod.Post, url)
            {
                Content = new StringContent(
                    JsonSerializer.Serialize(new { syncKey = _syncKey, characterName = character }),
                    Encoding.UTF8,
                    "application/json")
            };

            var response = await Task.Run(() => _httpClient.SendAsync(request));
            if (!response.IsSuccessStatusCode)
            {
                return;
            }

            var stream = await response.Content.ReadAsStreamAsync();
            var dto = await JsonSerializer.DeserializeAsync<ValidateResponse>(stream) ?? new ValidateResponse();

            _ = PluginServices.Framework.RunOnTick(() =>
            {
                _key = dto.UserKey;
                _config.AuthToken = dto.UserKey;
                _config.SyncKey = _syncKey;
                _config.GuildId = dto.Guild?.Id ?? string.Empty;
                _config.GuildName = dto.Guild?.Name ?? string.Empty;
                SaveConfig();
                _refreshRoles();
            });
        }
        catch
        {
            // ignored
        }
    }

    private class ValidateResponse
    {
        [JsonPropertyName("userKey")] public string UserKey { get; set; } = string.Empty;
        [JsonPropertyName("guild")] public GuildInfo? Guild { get; set; }
    }

    private class GuildInfo
    {
        [JsonPropertyName("id")] public string Id { get; set; } = string.Empty;
        [JsonPropertyName("name")] public string Name { get; set; } = string.Empty;
    }
}

