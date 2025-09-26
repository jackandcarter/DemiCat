using System;

namespace DemiCatPlugin.Emoji;

public static class EmojiFormatter
{
    public const string CustomPrefix = "custom:";
    private const string DefaultCustomName = "emoji";

    public static string CreateUnicodeToken(UnicodeEmoji emoji) => CreateUnicodeToken(emoji.Emoji);

    public static string CreateUnicodeToken(string emoji) => string.IsNullOrEmpty(emoji) ? string.Empty : emoji;

    public static string CreateCustomToken(CustomEmoji emoji) => CreateCustomToken(emoji.Id);

    public static string CreateCustomToken(string id) => string.Concat(CustomPrefix, id);

    public static bool TryParseCustomToken(string value, out string id)
        => TryParseCustomToken(value, out id, out _, out _);

    public static bool TryParseCustomToken(string value, out string id, out string? name, out bool animated)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            if (value.StartsWith(CustomPrefix, StringComparison.OrdinalIgnoreCase))
            {
                var parsedId = value.Substring(CustomPrefix.Length);
                if (!string.IsNullOrEmpty(parsedId))
                {
                    id = parsedId;
                    name = null;
                    animated = false;
                    return true;
                }
            }
            else if (TryParseDiscordToken(value, out name, out id, out animated))
            {
                return true;
            }
        }

        id = string.Empty;
        name = null;
        animated = false;
        return false;
    }

    public static bool IsCustomToken(string value) => TryParseCustomToken(value, out _);

    public static string? Normalize(EmojiManager manager, string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        if (!TryParseCustomToken(value, out var id, out var name, out var animated))
        {
            return value;
        }

        if (manager.TryGetCustomEmoji(id, out var emoji) && emoji != null)
        {
            var prefix = emoji.Animated ? "<a:" : "<:";
            return $"{prefix}{emoji.Name}:{emoji.Id}>";
        }

        if (!string.IsNullOrEmpty(name))
        {
            var prefix = animated ? "<a:" : "<:";
            return $"{prefix}{name}:{id}>";
        }

        return $"<:{DefaultCustomName}:{id}>";
    }

    private static bool TryParseDiscordToken(string value, out string? name, out string id, out bool animated)
    {
        name = null;
        id = string.Empty;
        animated = false;

        if (value.Length < 5 || value[0] != '<' || value[^1] != '>')
        {
            return false;
        }

        var span = value.AsSpan(1, value.Length - 2);
        if (span.Length == 0)
        {
            return false;
        }

        var index = 0;
        if (span[index] == 'a' || span[index] == 'A')
        {
            animated = true;
            index++;
            if (index >= span.Length || span[index] != ':')
            {
                return false;
            }
            index++;
        }
        else
        {
            if (span[index] != ':')
            {
                return false;
            }
            index++;
        }

        if (index >= span.Length)
        {
            return false;
        }

        var remainder = span[index..];
        var colonIndex = remainder.IndexOf(':');
        if (colonIndex < 0)
        {
            return false;
        }

        var nameSpan = remainder[..colonIndex];
        if (nameSpan.IsEmpty)
        {
            return false;
        }

        var idSpan = remainder[(colonIndex + 1)..];
        if (idSpan.IsEmpty)
        {
            return false;
        }

        name = nameSpan.ToString();
        id = idSpan.ToString();
        return true;
    }
}
