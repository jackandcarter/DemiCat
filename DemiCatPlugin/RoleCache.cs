using System.Collections.Generic;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;

namespace DemiCatPlugin;

internal static class RoleCache
{
    private static List<RoleDto> _roles = new();
    private static bool _loaded;

    internal static IReadOnlyList<RoleDto> Roles => _roles;

    internal static void Reset()
    {
        _roles = new();
        _loaded = false;
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
            return;
        }
        try
        {
            var request = new HttpRequestMessage(HttpMethod.Get, $"{config.ApiBaseUrl.TrimEnd('/')}/api/guild-roles");
            ApiHelpers.AddAuthHeader(request, config);
            var response = await httpClient.SendAsync(request);
            if (!response.IsSuccessStatusCode)
            {
                _loaded = true;
                return;
            }
            var stream = await response.Content.ReadAsStreamAsync();
            var roles = await JsonSerializer.DeserializeAsync<List<RoleDto>>(stream) ?? new List<RoleDto>();
            _roles = roles;
            _loaded = true;
            _ = PluginServices.Instance!.Framework.RunOnTick(() =>
            {
                config.GuildRoles = roles;
                PluginServices.Instance!.PluginInterface.SavePluginConfig(config);
            });
        }
        catch
        {
            _loaded = true;
        }
    }
}
