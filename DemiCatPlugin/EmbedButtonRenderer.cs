using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Textures;
using DemiCatPlugin.Emoji;

namespace DemiCatPlugin;

internal static class EmbedButtonRenderer
{
    private sealed class ButtonIcon
    {
        public ISharedImmediateTexture? Texture { get; init; }
        public string? Text { get; init; }
        public string? Tooltip { get; init; }
    }

    public static bool Draw(string? label, string uniqueId, string? emoji, EmojiManager emojiManager, Vector2 size)
    {
        var style = ImGui.GetStyle();
        var padding = style.FramePadding;
        var hasLabel = !string.IsNullOrEmpty(label);
        var textSize = hasLabel ? ImGui.CalcTextSize(label) : Vector2.Zero;

        var icon = ResolveIcon(emoji, emojiManager);
        var estimateHeight = size.Y > 0f ? size.Y - padding.Y * 2f : ImGui.GetFrameHeight() - padding.Y * 2f;
        if (estimateHeight <= 0f)
        {
            estimateHeight = ImGui.GetFrameHeight() - padding.Y * 2f;
        }

        var estimatedIconSize = icon != null
            ? CalculateIconSize(icon.Texture, icon.Text, estimateHeight)
            : Vector2.Zero;

        var width = size.X;
        if (width <= 0f)
        {
            var spacing = hasLabel && estimatedIconSize.X > 0f ? Math.Max(2f, padding.X * 0.6f) : 0f;
            width = estimatedIconSize.X + spacing + textSize.X + padding.X * 2f;
        }

        if (width <= 0f)
        {
            width = (hasLabel ? textSize.X : 0f) + padding.X * 2f;
        }

        var height = size.Y <= 0f ? 0f : size.Y;

        var pressed = ImGui.Button($"##{uniqueId}", new Vector2(width, height));

        var rectMin = ImGui.GetItemRectMin();
        var rectMax = ImGui.GetItemRectMax();
        var buttonSize = rectMax - rectMin;
        if (buttonSize.X <= 0f || buttonSize.Y <= 0f)
        {
            return pressed;
        }

        var iconSize = icon != null
            ? CalculateIconSize(icon.Texture, icon.Text, buttonSize.Y - padding.Y * 2f)
            : Vector2.Zero;

        var drawList = ImGui.GetWindowDrawList();
        var textColor = ImGui.GetColorU32(ImGuiCol.Text);

        drawList.PushClipRect(rectMin, rectMax, true);

        if (icon != null && iconSize.X > 0f && iconSize.Y > 0f)
        {
            var iconPos = CalculateIconPosition(buttonSize, rectMin, padding, iconSize, hasLabel);
            if (icon.Texture != null)
            {
                var wrap = icon.Texture.GetWrapOrEmpty();
                if (wrap.Handle != IntPtr.Zero)
                {
                    drawList.AddImage(wrap.Handle, iconPos, iconPos + iconSize);
                }
            }
            else if (!string.IsNullOrEmpty(icon.Text))
            {
                drawList.AddText(iconPos, textColor, icon.Text);
            }

            if (hasLabel)
            {
                var spacing = Math.Max(2f, padding.X * 0.6f);
                var textPos = new Vector2(iconPos.X + iconSize.X + spacing, rectMin.Y + (buttonSize.Y - textSize.Y) * 0.5f);
                drawList.AddText(textPos, textColor, label);
            }
        }
        else if (hasLabel)
        {
            var textPos = new Vector2(rectMin.X + (buttonSize.X - textSize.X) * 0.5f, rectMin.Y + (buttonSize.Y - textSize.Y) * 0.5f);
            drawList.AddText(textPos, textColor, label);
        }

        drawList.PopClipRect();

        if (icon != null && !string.IsNullOrEmpty(icon.Tooltip) && ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
        {
            ImGui.SetTooltip(icon.Tooltip);
        }

        return pressed;
    }

    private static ButtonIcon? ResolveIcon(string? emoji, EmojiManager emojiManager)
    {
        if (string.IsNullOrWhiteSpace(emoji))
        {
            return null;
        }

        if (!EmojiFormatter.TryParseCustomToken(emoji, out var id, out var fallbackName, out var fallbackAnimated))
        {
            if (emojiManager.TryGetUnicodeEmoji(emoji, out var unicode) && unicode != null)
            {
                ISharedImmediateTexture? texture = null;
                if (!string.IsNullOrEmpty(unicode.ImageUrl))
                {
                    if (!WebTextureCache.TryGetTexture(unicode.ImageUrl, out texture))
                    {
                        WebTextureCache.Get(unicode.ImageUrl, _ => { });
                    }
                }

                var tooltip = string.IsNullOrEmpty(unicode.Name) ? unicode.Emoji : unicode.Name;
                return new ButtonIcon
                {
                    Texture = texture,
                    Text = texture == null ? unicode.Emoji : null,
                    Tooltip = tooltip
                };
            }

            return new ButtonIcon
            {
                Text = emoji,
                Tooltip = emoji
            };
        }

        var label = string.IsNullOrEmpty(fallbackName) ? ":emoji:" : $":{fallbackName}:";
        var animated = fallbackAnimated;
        var imageUrl = BuildCdnUrl(id, animated);

        if (emojiManager.TryGetCustomEmoji(id, out var custom) && custom != null)
        {
            animated = custom.Animated;
            label = $":{custom.Name}:";
            imageUrl = string.IsNullOrEmpty(custom.ImageUrl)
                ? BuildCdnUrl(custom.Id, custom.Animated)
                : custom.ImageUrl;
        }

        ISharedImmediateTexture? customTexture = null;
        if (!string.IsNullOrEmpty(imageUrl))
        {
            if (!WebTextureCache.TryGetTexture(imageUrl, out customTexture))
            {
                WebTextureCache.Get(imageUrl, _ => { });
            }
        }

        return new ButtonIcon
        {
            Texture = customTexture,
            Text = customTexture == null ? label : null,
            Tooltip = label
        };
    }

    private static Vector2 CalculateIconSize(ISharedImmediateTexture? texture, string? fallbackText, float maxSize)
    {
        if (texture != null)
        {
            var wrap = texture.GetWrapOrEmpty();
            if (wrap.Width > 0 && wrap.Height > 0 && maxSize > 0f)
            {
                var width = (float)wrap.Width;
                var height = (float)wrap.Height;
                if (width > height)
                {
                    var scale = maxSize / width;
                    return new Vector2(maxSize, Math.Max(1f, height * scale));
                }
                else
                {
                    var scale = maxSize / height;
                    return new Vector2(Math.Max(1f, width * scale), maxSize);
                }
            }
        }

        if (!string.IsNullOrEmpty(fallbackText))
        {
            return ImGui.CalcTextSize(fallbackText);
        }

        return Vector2.Zero;
    }

    private static Vector2 CalculateIconPosition(Vector2 buttonSize, Vector2 rectMin, Vector2 padding, Vector2 iconSize, bool hasLabel)
    {
        if (hasLabel)
        {
            return new Vector2(rectMin.X + padding.X, rectMin.Y + (buttonSize.Y - iconSize.Y) * 0.5f);
        }

        return new Vector2(
            rectMin.X + (buttonSize.X - iconSize.X) * 0.5f,
            rectMin.Y + (buttonSize.Y - iconSize.Y) * 0.5f);
    }

    private static string BuildCdnUrl(string id, bool animated)
        => $"https://cdn.discordapp.com/emojis/{id}.{(animated ? "gif" : "png")}";
}
