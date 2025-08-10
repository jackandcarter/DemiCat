using System;
using System.Net.Http;
using System.Net.Http.Headers;
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
    private string _syncKey = string.Empty;
    public bool IsOpen;

    public SettingsWindow(Config config)
    {
        _config = config;
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
                    _config.SyncKey = _syncKey;
                    SaveConfig();
                    CheckRoles();
                });
            }
        }
        catch
        {
            // ignored
        }
    }

    private async void CheckRoles()
    {
        try
        {
            var url = $"{_config.HelperBaseUrl.TrimEnd('/')}/api/me/roles?syncKey={_syncKey}";
            var request = new HttpRequestMessage(HttpMethod.Get, url);
            if (!string.IsNullOrEmpty(_config.AuthToken))
            {
                request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", _config.AuthToken);
            }
            await Task.Run(() => _httpClient.SendAsync(request));
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

