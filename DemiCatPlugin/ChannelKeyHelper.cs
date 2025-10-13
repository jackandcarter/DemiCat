using System;

namespace DemiCatPlugin;

public static class ChannelKeyHelper
{
    private const string DefaultGuildSentinel = "default";

    public static string NormalizeGuildId(string? guildId)
        => string.IsNullOrWhiteSpace(guildId) ? DefaultGuildSentinel : guildId.Trim();

    public static string NormalizeKind(string? kind)
    {
        if (string.IsNullOrWhiteSpace(kind))
        {
            return string.Empty;
        }
        return kind.Trim().ToUpperInvariant();
    }

    public static string BuildSelectionKey(string? guildId, string? kind)
        => $"{NormalizeGuildId(guildId)}:{NormalizeKind(kind)}";

    public static string BuildCursorKey(string? guildId, string? kind, string channelId)
        => $"{NormalizeGuildId(guildId)}:{NormalizeKind(kind)}:{channelId}";

    public static bool IsDefaultGuild(string? guildId)
    {
        if (string.IsNullOrWhiteSpace(guildId))
        {
            return true;
        }

        return string.Equals(guildId.Trim(), DefaultGuildSentinel, StringComparison.OrdinalIgnoreCase);
    }
}
