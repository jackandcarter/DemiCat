using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Threading.Tasks;

namespace DemiCatPlugin;

internal static class ChannelNameResolver
{
    internal static bool Resolve(List<ChannelDto> channels)
    {
        var unresolved = false;
        foreach (var c in channels)
        {
            if (string.IsNullOrWhiteSpace(c.Name))
            {
                PluginServices.Instance!.Log.Warning($"Channel name missing for {c.Id}.");
                c.Name = c.Id;
                unresolved = true;
            }
        }
        return unresolved;
    }

    internal static async Task<bool> Resolve(
        List<ChannelDto> channels,
        HttpClient httpClient,
        Config config,
        bool refreshed,
        Func<Task> reload)
    {
        var unresolved = Resolve(channels);
        if (unresolved && !refreshed && ApiHelpers.ValidateApiBaseUrl(config))
        {
            try
            {
                var refreshReq = new HttpRequestMessage(HttpMethod.Post,
                    $"{config.ApiBaseUrl.TrimEnd('/')}/api/channels/refresh");
                ApiHelpers.AddAuthHeader(refreshReq, config);
                await httpClient.SendAsync(refreshReq);
            }
            catch (Exception ex)
            {
                PluginServices.Instance!.Log.Warning(ex, "Error refreshing channel names");
            }
            await reload();
            return true;
        }

        return false;
    }
}
