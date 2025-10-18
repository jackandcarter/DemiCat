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
    private static long _version;

    internal static IReadOnlyList<RoleDto> Roles => _roles;
    internal static string? LastErrorMessage => _lastErrorMessage;
    internal static bool IsLoaded => _loaded;
    internal static long Version => _version;

    private static void IncrementVersion()
    {
        if (_version == long.MaxValue)
        {
            _version = 0;
        }
        else
        {
            _version++;
        }
    }

    internal static void Reset()
    {
        _roles = new();
        _loaded = false;
        _lastErrorMessage = null;
        IncrementVersion();
    }

    internal static async Task EnsureLoaded(HttpClient httpClient, Config config)
    {
        if (_loaded)
            return;
        if (config.GuildRoles.Count > 0)
        {
            _roles = new List<RoleDto>(config.GuildRoles);
            _loaded = true;
            IncrementVersion();
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
            var payload = await JsonSerializer.DeserializeAsync<GuildRolesResponseDto>(stream)
                ?? new GuildRolesResponseDto();
            var roles = payload.Roles ?? new List<RoleDto>();
            var mentionRoleIds = payload.MentionRoleIds ?? new List<string>();

            _roles = roles;
            _lastErrorMessage = null;
            _loaded = true;
            IncrementVersion();
            _ = PluginServices.Instance!.Framework.RunOnTick(() =>
            {
                config.GuildRoles = roles;
                config.MentionRoleIds = mentionRoleIds;
                PluginServices.Instance!.PluginInterface.SavePluginConfig(config);
            });
        }
        catch
        {
            _lastErrorMessage = "Failed to load roles";
            _loaded = true;
            IncrementVersion();
        }
    }
}
