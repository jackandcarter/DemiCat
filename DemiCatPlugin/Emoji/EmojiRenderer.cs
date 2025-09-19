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

        if (!TryGetEmojiInfo(value, out var id, out var label, out var animated))
        {
            ImGui.TextUnformatted(value);
            return;
        }

        var imageUrl = BuildCdnUrl(id, animated);

        if (manager.TryGetCustomEmoji(id, out var emoji) && emoji != null)
        {
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

    private static bool TryGetEmojiInfo(string value, out string id, out string label, out bool animated)
    {
        if (EmojiFormatter.TryParseCustomToken(value, out var customId))
        {
            id = customId;
            label = ":emoji:";
            animated = false;
            return true;
        }

        if (TryParseDiscordMarkup(value, out var markupId, out var markupLabel, out var markupAnimated))
        {
            id = markupId;
            label = markupLabel;
            animated = markupAnimated;
            return true;
        }

        id = string.Empty;
        label = string.Empty;
        animated = false;
        return false;
    }

    private static bool TryParseDiscordMarkup(string value, out string id, out string label, out bool animated)
    {
        id = string.Empty;
        label = string.Empty;
        animated = false;

        if (string.IsNullOrEmpty(value) || value.Length < 5)
        {
            return false;
        }

        if (value[0] != '<' || value[^1] != '>')
        {
            return false;
        }

        var content = value.AsSpan(1, value.Length - 2);
        var firstColon = content.IndexOf(':');
        if (firstColon < 0)
        {
            return false;
        }

        var prefix = content.Slice(0, firstColon);
        var remainder = content.Slice(firstColon + 1);
        var secondColon = remainder.IndexOf(':');
        if (secondColon < 0)
        {
            return false;
        }

        var nameSpan = remainder.Slice(0, secondColon);
        var idSpan = remainder.Slice(secondColon + 1);
        if (idSpan.Length == 0)
        {
            return false;
        }

        if (idSpan.IndexOf(':') >= 0)
        {
            return false;
        }

        if (prefix.Length > 0)
        {
            if (prefix.Length != 1)
            {
                return false;
            }

            var prefixChar = prefix[0];
            if (prefixChar != 'a' && prefixChar != 'A')
            {
                return false;
            }

            animated = true;
        }

        var name = nameSpan.Length > 0 ? nameSpan.ToString() : "emoji";
        label = $":{name}:";
        id = idSpan.ToString();
        return true;
    }
}
