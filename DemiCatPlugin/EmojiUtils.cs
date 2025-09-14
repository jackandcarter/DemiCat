using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;

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

    public static void DrawEmoji(string? emoji, float size = 20f)
    {
        if (string.IsNullOrWhiteSpace(emoji)) return;
        if (emoji.StartsWith("custom:", StringComparison.OrdinalIgnoreCase))
        {
            var id = emoji.Substring("custom:".Length);
            var ext = EmojiAssets.IsGuildEmojiAnimated(id) ? "gif" : "png";
            var url = $"https://cdn.discordapp.com/emojis/{id}.{ext}";
            WebTextureCache.Get(url, tex =>
            {
                if (tex != null)
                {
                    var wrap = tex.GetWrapOrEmpty();
                    ImGui.Image(wrap.Handle, new Vector2(size, size));
                }
            });
        }
        else
        {
            ImGui.TextUnformatted(emoji);
        }
    }
}

