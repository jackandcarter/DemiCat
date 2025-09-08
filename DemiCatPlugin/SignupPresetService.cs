using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

namespace DemiCatPlugin;

internal static class SignupPresetService
{
    private static List<SignupPreset> _presets = new();
    private static bool _loaded;

    internal static IReadOnlyList<SignupPreset> Presets => _presets;

    internal static void Reset()
    {
        _presets = new();
        _loaded = false;
    }

    internal static async Task EnsureLoaded(HttpClient httpClient, Config config)
    {
        if (!_loaded)
        {
            await Refresh(httpClient, config);
        }
    }

    internal static async Task Refresh(HttpClient httpClient, Config config)
    {
        if (!ApiHelpers.ValidateApiBaseUrl(config)) return;
        try
        {
            var url = $"{config.ApiBaseUrl.TrimEnd('/')}/api/signup-presets";
            var req = new HttpRequestMessage(HttpMethod.Get, url);
            if (TokenManager.Instance != null)
                ApiHelpers.AddAuthHeader(req, TokenManager.Instance!);
            var resp = await httpClient.SendAsync(req);
            if (resp.IsSuccessStatusCode)
            {
                var stream = await resp.Content.ReadAsStreamAsync();
                var presets = await JsonSerializer.DeserializeAsync<List<SignupPreset>>(stream) ?? new();
                _presets = presets;
                _loaded = true;
            }
            else
            {
                var responseBody = await resp.Content.ReadAsStringAsync();
                PluginServices.Instance!.Log.Warning($"Failed to refresh signup presets. URL: {url}, Status: {resp.StatusCode}. Response Body: {responseBody}");
                if (resp.StatusCode == HttpStatusCode.Unauthorized || resp.StatusCode == HttpStatusCode.Forbidden)
                {
                    PluginServices.Instance?.ToastGui.ShowError("Signup presets auth failed");
                }
            }
        }
        catch
        {
            // ignored
        }
    }

    internal static async Task Create(SignupPreset preset, HttpClient httpClient, Config config)
    {
        if (!ApiHelpers.ValidateApiBaseUrl(config)) return;
        try
        {
            var url = $"{config.ApiBaseUrl.TrimEnd('/')}/api/signup-presets";
            var req = new HttpRequestMessage(HttpMethod.Post, url);
            if (TokenManager.Instance != null)
                ApiHelpers.AddAuthHeader(req, TokenManager.Instance!);
            req.Content = new StringContent(JsonSerializer.Serialize(preset), Encoding.UTF8, "application/json");
            var resp = await httpClient.SendAsync(req);
            if (resp.IsSuccessStatusCode)
            {
                await Refresh(httpClient, config);
            }
            else
            {
                var responseBody = await resp.Content.ReadAsStringAsync();
                PluginServices.Instance!.Log.Warning($"Failed to create signup preset. URL: {url}, Status: {resp.StatusCode}. Response Body: {responseBody}");
                if (resp.StatusCode == HttpStatusCode.Unauthorized || resp.StatusCode == HttpStatusCode.Forbidden)
                {
                    PluginServices.Instance?.ToastGui.ShowError("Signup presets auth failed");
                }
            }
        }
        catch
        {
            // ignored
        }
    }

    internal static async Task Delete(string id, HttpClient httpClient, Config config)
    {
        if (!ApiHelpers.ValidateApiBaseUrl(config)) return;
        try
        {
            var url = $"{config.ApiBaseUrl.TrimEnd('/')}/api/signup-presets/{id}";
            var req = new HttpRequestMessage(HttpMethod.Delete, url);
            if (TokenManager.Instance != null)
                ApiHelpers.AddAuthHeader(req, TokenManager.Instance!);
            var resp = await httpClient.SendAsync(req);
            if (resp.IsSuccessStatusCode)
            {
                await Refresh(httpClient, config);
            }
            else
            {
                var responseBody = await resp.Content.ReadAsStringAsync();
                PluginServices.Instance!.Log.Warning($"Failed to delete signup preset. URL: {url}, Status: {resp.StatusCode}. Response Body: {responseBody}");
                if (resp.StatusCode == HttpStatusCode.Unauthorized || resp.StatusCode == HttpStatusCode.Forbidden)
                {
                    PluginServices.Instance?.ToastGui.ShowError("Signup presets auth failed");
                }
            }
        }
        catch
        {
            // ignored
        }
    }
}

