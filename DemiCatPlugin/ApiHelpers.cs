namespace DemiCatPlugin;

internal static class ApiHelpers
{
    internal static bool ValidateApiBaseUrl(Config config)
    {
        if (string.IsNullOrWhiteSpace(config.ApiBaseUrl))
        {
            PluginServices.Instance!.Log.Error("API base URL is not configured.");
            return false;
        }
        return true;
    }
}

