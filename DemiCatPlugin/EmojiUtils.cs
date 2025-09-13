using System;

namespace DemiCatPlugin;

public static class EmojiUtils
{
    public static string? Normalize(string? emoji)
    {
        if (string.IsNullOrWhiteSpace(emoji)) return null;
        if (emoji.StartsWith("custom:", StringComparison.OrdinalIgnoreCase))
        {
            var id = emoji.Substring("custom:".Length);
            var name = EmojiPopup.LookupGuildName(id) ?? "emoji";
            var animated = EmojiPopup.IsGuildEmojiAnimated(id);
            return $"<{(animated ? "a" : string.Empty)}:{name}:{id}>";
        }
        return emoji;
    }
}
