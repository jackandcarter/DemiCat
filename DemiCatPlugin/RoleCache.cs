using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;

namespace DemiCatPlugin;

internal static class RoleCache
{
    private static List<RoleDto> _roles = new();
    private static bool _loaded;
    private static string? _lastErrorMessage;

    internal static IReadOnlyList<RoleDto> Roles => _roles;
    internal static string? LastErrorMessage => _lastErrorMessage;

    internal static void Reset()
    {
        _roles = new();
        _loaded = false;
        _lastErrorMessage = null;
    }

    internal static async Task EnsureLoaded(HttpClient httpClient, Config config)
    {
        if (_loaded)
            return;
        if (config.GuildRoles.Count > 0)
        {
            _roles = new List<RoleDto>(config.GuildRoles);
            _loaded = true;
            return;
        }
        await Refresh(httpClient, config);
    }

    internal static async Task Refresh(HttpClient httpClient, Config config)
    {
        if (!ApiHelpers.ValidateApiBaseUrl(config))
        {
            _loaded = true;
            _lastErrorMessage = "Invalid API URL";
            return;
        }
        try
        {
            var request = new HttpRequestMessage(HttpMethod.Get, $"{config.ApiBaseUrl.TrimEnd('/')}/api/guild-roles");
            ApiHelpers.AddAuthHeader(request, TokenManager.Instance!);
            var response = await httpClient.SendAsync(request);
            if (response.StatusCode == HttpStatusCode.Unauthorized || response.StatusCode == HttpStatusCode.Forbidden)
            {
                TokenManager.Instance!.Clear("Authentication failed");
                _lastErrorMessage = "Authentication failed";
                _loaded = true;
                return;
            }
            if (!response.IsSuccessStatusCode)
            {
                PluginServices.Instance!.Log.Warning($"Failed to refresh guild roles. URL: {request.RequestUri}, Status: {response.StatusCode}");
                _lastErrorMessage = "Failed to load roles";
                _loaded = true;
                return;
            }
            var stream = await response.Content.ReadAsStreamAsync();
            var roles = await JsonSerializer.DeserializeAsync<List<RoleDto>>(stream) ?? new List<RoleDto>();
            _roles = roles;
            _lastErrorMessage = null;
            _loaded = true;
            _ = PluginServices.Instance!.Framework.RunOnTick(() =>
            {
                config.GuildRoles = roles;
                PluginServices.Instance!.PluginInterface.SavePluginConfig(config);
            });
        }
        catch
        {
            _lastErrorMessage = "Failed to load roles";
            _loaded = true;
        }
    }
}
