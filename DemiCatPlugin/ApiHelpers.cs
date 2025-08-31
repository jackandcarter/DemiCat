using System;
using System.Net.Http;
using System.Net.WebSockets;

namespace DemiCatPlugin;

internal static class ApiHelpers
{
    internal static bool ValidateApiBaseUrl(Config config)
    {
        if (!Uri.TryCreate(config.ApiBaseUrl, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            PluginServices.Instance!.Log.Error($"Invalid API base URL: {config.ApiBaseUrl}");
            return false;
        }
        return true;
    }

    internal static void AddAuthHeader(HttpRequestMessage request, Config config)
        => AddAuthHeader(request, config.AuthToken);

    internal static void AddAuthHeader(HttpRequestMessage request, string? token)
    {
        if (!string.IsNullOrEmpty(token))
        {
            request.Headers.Add("X-Api-Key", token);
        }
    }

    internal static void AddAuthHeader(ClientWebSocket socket, Config config)
        => AddAuthHeader(socket, config.AuthToken);

    internal static void AddAuthHeader(ClientWebSocket socket, string? token)
    {
        if (!string.IsNullOrEmpty(token))
        {
            socket.Options.SetRequestHeader("X-Api-Key", token);
        }
    }
}

