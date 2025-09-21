using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;

namespace DemiCatPlugin.Emoji;

public static class EmojiRenderer
{
    public static void Draw(string? value, EmojiManager manager, float size = 20f)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return;
        }

        if (!EmojiFormatter.TryParseCustomToken(value, out var id, out var fallbackName, out var fallbackAnimated))
        {
            using var font = manager.PushEmojiFont();
            ImGui.TextUnformatted(value);
            return;
        }

        var label = string.IsNullOrEmpty(fallbackName) ? ":emoji:" : $":{fallbackName}:";
        var animated = fallbackAnimated;
        var imageUrl = BuildCdnUrl(id, animated);

        if (manager.TryGetCustomEmoji(id, out var emoji) && emoji != null)
        {
            animated = emoji.Animated;
            label = $":{emoji.Name}:";
            imageUrl = string.IsNullOrEmpty(emoji.ImageUrl)
                ? BuildCdnUrl(emoji.Id, emoji.Animated)
                : emoji.ImageUrl;
        }

        WebTextureCache.Get(imageUrl, tex =>
        {
            if (tex != null)
            {
                var wrap = tex.GetWrapOrEmpty();
                ImGui.Image(wrap.Handle, new Vector2(size, size));
            }
            else
            {
                ImGui.TextUnformatted(label);
            }
        });

        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip(label);
        }
    }

    private static string BuildCdnUrl(string id, bool animated)
        => $"https://cdn.discordapp.com/emojis/{id}.{(animated ? "gif" : "png")}";
}
