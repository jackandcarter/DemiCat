using System;

namespace DemiCatPlugin.Emoji;

public static class EmojiFormatter
{
    public const string CustomPrefix = "custom:";
    private const string DefaultCustomName = "emoji";

    public static void InsertUnicode(ref string target, UnicodeEmoji emoji)
        => InsertUnicode(ref target, emoji.Emoji);

    public static void InsertUnicode(ref string target, string emoji)
    {
        if (string.IsNullOrEmpty(emoji))
        {
            return;
        }

        target += emoji;
    }

    public static void InsertCustom(ref string target, CustomEmoji emoji)
        => target += CreateCustomToken(emoji.Id);

    public static string CreateCustomToken(string id) => string.Concat(CustomPrefix, id);

    public static bool TryParseCustomToken(string value, out string id)
    {
        if (!string.IsNullOrWhiteSpace(value) &&
            value.StartsWith(CustomPrefix, StringComparison.OrdinalIgnoreCase))
        {
            id = value.Substring(CustomPrefix.Length);
            return !string.IsNullOrEmpty(id);
        }

        id = string.Empty;
        return false;
    }

    public static bool IsCustomToken(string value) => TryParseCustomToken(value, out _);

    public static string? Normalize(EmojiManager manager, string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        if (!TryParseCustomToken(value, out var id))
        {
            return value;
        }

        if (manager.TryGetCustomEmoji(id, out var emoji) && emoji != null)
        {
            var prefix = emoji.Animated ? "<a:" : "<:";
            return $"{prefix}{emoji.Name}:{emoji.Id}>";
        }

        return $"<:{DefaultCustomName}:{id}>";
    }
}
