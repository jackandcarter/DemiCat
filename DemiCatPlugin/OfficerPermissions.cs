namespace DemiCatPlugin;

internal static class OfficerPermissions
{
    internal static bool HasAccess(Config config)
    {
        return config.Officer && config.IsOfficerToken;
    }
}
