using System;
using System.Collections.Generic;
using System.Numerics;
using Dalamud.Bindings.ImGui;

namespace DemiCatPlugin.Emoji;

public static class EmojiRenderer
{
    private static readonly HashSet<string> PendingUnicodeTextures = new(StringComparer.Ordinal);
    private static readonly object PendingLock = new();

    public static void Draw(string? value, EmojiManager manager, float size = 20f)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return;
        }

        if (!EmojiFormatter.TryParseCustomToken(value, out var id, out var fallbackName, out var fallbackAnimated))
        {
            DrawUnicodeEmoji(value, manager, size);
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

    private static void DrawUnicodeEmoji(string value, EmojiManager manager, float size)
    {
        if (manager.TryGetUnicodeEmoji(value, out var unicode) && unicode != null)
        {
            var tooltip = string.IsNullOrEmpty(unicode.Name) ? value : unicode.Name;
            var imageUrl = unicode.ImageUrl;
            if (!string.IsNullOrEmpty(imageUrl) &&
                WebTextureCache.TryGetTexture(imageUrl, out var cached) &&
                cached != null)
            {
                var wrap = cached.GetWrapOrEmpty();
                if (wrap.Width > 0 && wrap.Height > 0)
                {
                    ImGui.Image(wrap.Handle, new Vector2(size, size));
                    if (!string.IsNullOrEmpty(tooltip) && ImGui.IsItemHovered())
                    {
                        ImGui.SetTooltip(tooltip);
                    }
                    return;
                }
            }

            if (!string.IsNullOrEmpty(imageUrl) && !WebTextureCache.TryGetTexture(imageUrl, out _))
            {
                RequestUnicodeTexture(imageUrl);
            }

            using var font = manager.PushEmojiFont();
            ImGui.TextUnformatted(value);
            if (!string.IsNullOrEmpty(tooltip) && ImGui.IsItemHovered())
            {
                ImGui.SetTooltip(tooltip);
            }
            return;
        }

        using var fallbackFont = manager.PushEmojiFont();
        ImGui.TextUnformatted(value);
    }

    private static void RequestUnicodeTexture(string imageUrl)
    {
        lock (PendingLock)
        {
            if (!PendingUnicodeTextures.Add(imageUrl))
            {
                return;
            }
        }

        WebTextureCache.Get(imageUrl, _ =>
        {
            lock (PendingLock)
            {
                PendingUnicodeTextures.Remove(imageUrl);
            }
        });
    }
}
