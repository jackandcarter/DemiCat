using System;

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
}

