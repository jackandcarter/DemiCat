using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility;

namespace DemiCatPlugin;

public static class EmbedStyleControls
{
    public sealed class Context
    {
        public string ChannelKind { get; init; } = string.Empty;
        public uint EffectiveEmbedColor { get; init; }
        public uint? EmbedColorOverride { get; init; }
        public Config.EmbedBorderSettings Border { get; init; } = Config.EmbedBorderSettings.CreateDefault(ChannelKind.Chat);
    }

    public sealed class Result
    {
        public bool EmbedColorChanged { get; init; }
        public uint? EmbedColorOverride { get; init; }
        public bool BorderChanged { get; init; }
        public Config.EmbedBorderSettings Border { get; init; } = Config.EmbedBorderSettings.CreateDefault(ChannelKind.Chat);
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
        var border = context.Border?.Clone() ?? Config.EmbedBorderSettings.CreateDefault(context.ChannelKind);

        ImGui.AlignTextToFramePadding();
        ImGui.TextUnformatted("Embed Color:");
        ImGui.SameLine();

        var effectiveColor = ColorUtils.RgbToVector4(context.EffectiveEmbedColor);
        var buttonSize = new Vector2(ImGui.GetFrameHeight() * 2.2f, ImGui.GetFrameHeight());
        var popupId = $"embedColorPicker##{context.ChannelKind}";
        if (ImGui.ColorButton($"##embedColorButton_{context.ChannelKind}", effectiveColor, ImGuiColorEditFlags.NoAlpha, buttonSize))
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
        if (ImGui.Checkbox($"##embedBorderEnabled_{context.ChannelKind}", ref enabled))
        {
            border.Enabled = enabled;
            borderChanged = true;
        }

        ImGui.SameLine();
        var comboWidth = 120f * ImGuiHelpers.GlobalScale;
        if (!enabled)
        {
            ImGui.BeginDisabled();
        }

        ImGui.SetNextItemWidth(comboWidth);
        var label = GetGlyphLabel(border.Glyph);
        if (ImGui.BeginCombo($"##embedBorderGlyph_{context.ChannelKind}", label))
        {
            foreach (Config.EmbedBorderGlyph glyph in Enum.GetValues<Config.EmbedBorderGlyph>())
            {
                var isSelected = border.Glyph == glyph;
                if (ImGui.Selectable(GetGlyphLabel(glyph), isSelected))
                {
                    border.Glyph = glyph;
                    borderChanged = true;
                }
            }

            ImGui.EndCombo();
        }

        ImGui.SameLine();
        var borderColor = ColorUtils.RgbToVector4(border.Color);
        var borderPopupId = $"embedBorderColorPicker##{context.ChannelKind}";
        if (ImGui.ColorButton($"##embedBorderColor_{context.ChannelKind}", borderColor, ImGuiColorEditFlags.NoAlpha, buttonSize))
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

    private static string GetGlyphLabel(Config.EmbedBorderGlyph glyph)
        => glyph switch
        {
            Config.EmbedBorderGlyph.Circle => "Circle",
            Config.EmbedBorderGlyph.Triangle => "Triangle",
            _ => "Square"
        };
}
