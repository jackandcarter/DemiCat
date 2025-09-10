using System.Collections.Generic;

namespace DemiCatPlugin;

public static class EmojiAssets
{
    private static readonly Dictionary<string, (string Name, bool IsAnimated)> _guildInfos = new();
    private static readonly Dictionary<string, string> _unicodeUrls = new();

    public static void SetGuildEmoji(string id, string name, bool isAnimated) => _guildInfos[id] = (name, isAnimated);
    public static string? LookupGuildName(string id) => _guildInfos.TryGetValue(id, out var v) ? v.Name : null;
    public static bool IsGuildEmojiAnimated(string id) => _guildInfos.TryGetValue(id, out var v) && v.IsAnimated;

    public static void SetUnicodeEmoji(string emoji, string url) => _unicodeUrls[emoji] = url;
    public static string? LookupUnicodeUrl(string emoji) => _unicodeUrls.TryGetValue(emoji, out var v) ? v : null;

    public static void Clear()
    {
        _guildInfos.Clear();
        _unicodeUrls.Clear();
    }
}

