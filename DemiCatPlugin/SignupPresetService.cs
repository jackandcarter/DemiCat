using System.Collections.Generic;
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
            var req = new HttpRequestMessage(HttpMethod.Get, $"{config.ApiBaseUrl.TrimEnd('/')}/api/signup-presets");
            if (!string.IsNullOrEmpty(config.AuthToken))
            {
                req.Headers.Add("X-Api-Key", config.AuthToken);
            }
            var resp = await httpClient.SendAsync(req);
            if (resp.IsSuccessStatusCode)
            {
                var stream = await resp.Content.ReadAsStreamAsync();
                var presets = await JsonSerializer.DeserializeAsync<List<SignupPreset>>(stream) ?? new();
                _presets = presets;
                _loaded = true;
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
            var req = new HttpRequestMessage(HttpMethod.Post, $"{config.ApiBaseUrl.TrimEnd('/')}/api/signup-presets");
            if (!string.IsNullOrEmpty(config.AuthToken))
            {
                req.Headers.Add("X-Api-Key", config.AuthToken);
            }
            req.Content = new StringContent(JsonSerializer.Serialize(preset), Encoding.UTF8, "application/json");
            var resp = await httpClient.SendAsync(req);
            if (resp.IsSuccessStatusCode)
            {
                await Refresh(httpClient, config);
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
            var req = new HttpRequestMessage(HttpMethod.Delete, $"{config.ApiBaseUrl.TrimEnd('/')}/api/signup-presets/{id}");
            if (!string.IsNullOrEmpty(config.AuthToken))
            {
                req.Headers.Add("X-Api-Key", config.AuthToken);
            }
            var resp = await httpClient.SendAsync(req);
            if (resp.IsSuccessStatusCode)
            {
                await Refresh(httpClient, config);
            }
        }
        catch
        {
            // ignored
        }
    }
}

