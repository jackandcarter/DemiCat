using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility;
using DemiCatPlugin.Emoji;

namespace DemiCatPlugin;

public static class EmbedStyleControls
{
    public sealed class Context
    {
        public string ChannelKindKey { get; init; } = string.Empty;
        public uint EffectiveEmbedColor { get; init; }
        public uint? EmbedColorOverride { get; init; }
        public Config.EmbedBorderSettings Border { get; init; } = Config.EmbedBorderSettings.CreateDefault(global::DemiCatPlugin.ChannelKind.Chat);
        public EmojiManager? EmojiManager { get; init; }
        public EmojiPicker? EmojiPicker { get; init; }
    }

    public sealed class Result
    {
        public bool EmbedColorChanged { get; init; }
        public uint? EmbedColorOverride { get; init; }
        public bool BorderChanged { get; init; }
        public Config.EmbedBorderSettings Border { get; init; } = Config.EmbedBorderSettings.CreateDefault(global::DemiCatPlugin.ChannelKind.Chat);
    }

    public static Result Draw(Context context)
    {
        if (context == null)
        {
            throw new ArgumentNullException(nameof(context));
        }

        var embedColorChanged = false;
        var borderChanged = false;
        var colorOverride = context.EmbedColorOverride;
        var border = context.Border?.Clone() ?? Config.EmbedBorderSettings.CreateDefault(context.ChannelKindKey);

        ImGui.AlignTextToFramePadding();
        ImGui.TextUnformatted("Embed Color:");
        ImGui.SameLine();

        var effectiveColor = ColorUtils.RgbToVector4(context.EffectiveEmbedColor);
        var buttonSize = new Vector2(ImGui.GetFrameHeight() * 2.2f, ImGui.GetFrameHeight());
        var popupId = $"embedColorPicker##{context.ChannelKindKey}";
        if (ImGui.ColorButton($"##embedColorButton_{context.ChannelKindKey}", effectiveColor, ImGuiColorEditFlags.NoAlpha, buttonSize))
        {
            ImGui.OpenPopup(popupId);
        }

        if (ImGui.BeginPopup(popupId))
        {
            var pickerColor = new Vector3(effectiveColor.X, effectiveColor.Y, effectiveColor.Z);
            var pickerFlags = ImGuiColorEditFlags.NoSidePreview | ImGuiColorEditFlags.NoSmallPreview;
            if (ImGui.ColorPicker3("##embedColorPicker", ref pickerColor, pickerFlags))
            {
                var newColor = ColorUtils.ImGuiToRgb(ColorUtils.Vector4ToImGui(new Vector4(pickerColor, 1f)));
                colorOverride = newColor;
                embedColorChanged = true;
            }

            if (colorOverride.HasValue)
            {
                if (ImGui.Button("Reset to Default"))
                {
                    colorOverride = null;
                    embedColorChanged = true;
                    ImGui.CloseCurrentPopup();
                }
            }

            ImGui.EndPopup();
        }

        ImGui.SameLine();
        ImGui.TextUnformatted("Border:");
        ImGui.SameLine();

        var enabled = border.Enabled;
        if (ImGui.Checkbox($"##embedBorderEnabled_{context.ChannelKindKey}", ref enabled))
        {
            border.Enabled = enabled;
            borderChanged = true;
        }

        ImGui.SameLine();
        if (!enabled)
        {
            ImGui.BeginDisabled();
        }

        var glyphSymbol = EmbedBorderBuilder.GetGlyphSymbol(border.Glyph);
        var displayGlyph = glyphSymbol;
        if (context.EmojiManager != null)
        {
            displayGlyph = EmojiFormatter.Normalize(context.EmojiManager, glyphSymbol) ?? glyphSymbol;
        }
        var glyphPopupId = $"embedBorderGlyphPopup##{context.ChannelKindKey}";
        var glyphButtonSize = new Vector2(ImGui.GetFrameHeight() * 1.8f, ImGui.GetFrameHeight());
        using (context.EmojiManager?.PushEmojiFont())
        {
            if (ImGui.Button($"{displayGlyph}##embedBorderGlyph_{context.ChannelKindKey}", glyphButtonSize))
            {
                ImGui.OpenPopup(glyphPopupId);
            }
        }

        var selectedGlyph = DrawGlyphPopup(glyphPopupId, context);
        if (!string.IsNullOrEmpty(selectedGlyph) && !string.Equals(selectedGlyph, glyphSymbol, StringComparison.Ordinal))
        {
            border.Glyph = selectedGlyph;
            borderChanged = true;
            glyphSymbol = selectedGlyph;
            if (context.EmojiManager != null)
            {
                displayGlyph = EmojiFormatter.Normalize(context.EmojiManager, selectedGlyph) ?? selectedGlyph;
            }
            else
            {
                displayGlyph = selectedGlyph;
            }
        }

        var tooltip = TryGetEmojiName(context, glyphSymbol);
        if (!string.IsNullOrEmpty(tooltip) && ImGui.IsItemHovered())
        {
            ImGui.SetTooltip(tooltip);
        }

        ImGui.SameLine();
        var borderColor = ColorUtils.RgbToVector4(border.Color);
        var borderPopupId = $"embedBorderColorPicker##{context.ChannelKindKey}";
        if (ImGui.ColorButton($"##embedBorderColor_{context.ChannelKindKey}", borderColor, ImGuiColorEditFlags.NoAlpha, buttonSize))
        {
            ImGui.OpenPopup(borderPopupId);
        }

        if (ImGui.BeginPopup(borderPopupId))
        {
            var pickerColor = new Vector3(borderColor.X, borderColor.Y, borderColor.Z);
            var pickerFlags = ImGuiColorEditFlags.NoSidePreview | ImGuiColorEditFlags.NoSmallPreview;
            if (ImGui.ColorPicker3("##embedBorderColorPicker", ref pickerColor, pickerFlags))
            {
                var newColor = ColorUtils.ImGuiToRgb(ColorUtils.Vector4ToImGui(new Vector4(pickerColor, 1f)));
                border.Color = Config.SanitizeRgb(newColor, context.EffectiveEmbedColor);
                borderChanged = true;
            }

            if (ImGui.Button("Use Embed Color"))
            {
                border.Color = Config.SanitizeRgb(context.EffectiveEmbedColor, context.EffectiveEmbedColor);
                borderChanged = true;
                ImGui.CloseCurrentPopup();
            }

            ImGui.EndPopup();
        }

        if (!enabled)
        {
            ImGui.EndDisabled();
        }

        return new Result
        {
            EmbedColorChanged = embedColorChanged,
            EmbedColorOverride = colorOverride,
            BorderChanged = borderChanged,
            Border = border
        };
    }

    private static string? DrawGlyphPopup(string popupId, Context context)
    {
        string? selected = null;
        if (!ImGui.BeginPopup(popupId))
        {
            return null;
        }

        var picker = context.EmojiPicker;
        var manager = context.EmojiManager;
        if (picker == null || manager == null)
        {
            ImGui.TextDisabled("Emoji picker unavailable.");
        }
        else
        {
            ImGui.PushID(popupId);
            selected = picker.Draw();
            ImGui.PopID();
        }

        if (!string.IsNullOrEmpty(selected) && manager != null)
        {
            var normalized = EmojiFormatter.Normalize(manager, selected) ?? selected;
            selected = Config.SanitizeEmbedBorderGlyph(normalized);
            ImGui.CloseCurrentPopup();
        }

        ImGui.EndPopup();
        return string.IsNullOrEmpty(selected) ? null : selected;
    }

    private static string? TryGetEmojiName(Context context, string glyph)
    {
        if (string.IsNullOrEmpty(glyph))
        {
            return null;
        }

        var manager = context.EmojiManager;
        if (manager == null)
        {
            return null;
        }

        if (EmojiFormatter.TryParseCustomToken(glyph, out var id, out var name, out _))
        {
            if (manager.TryGetCustomEmoji(id, out var custom) && custom != null)
            {
                return $":{custom.Name}:";
            }

            if (!string.IsNullOrEmpty(name))
            {
                return $":{name}:";
            }

            return id;
        }

        foreach (var emoji in manager.Unicode)
        {
            if (emoji.Emoji.Equals(glyph, StringComparison.Ordinal))
            {
                return emoji.Name;
            }
        }

        return null;
    }
}
