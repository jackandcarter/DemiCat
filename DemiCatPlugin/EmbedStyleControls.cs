using System;
using System.Collections.Generic;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility;
using DemiCatPlugin.Emoji;

namespace DemiCatPlugin;

public static class EmbedStyleControls
{
    private static readonly Dictionary<string, string> GlyphSearchTerms = new();
    private static readonly string[] OfflineBorderGlyphs =
    {
        "⬛", "⬜", "🟥", "🟧", "🟨", "🟩", "🟦", "🟪", "🟫",
        "⚫", "⚪", "🔴", "🟠", "🟡", "🟢", "🔵", "🟣", "🟤",
        "◼️", "◻️", "◾", "◽", "▪️", "▫️", "🔶", "🔷", "🔸", "🔹",
        "♦️", "🔺", "🔻"
    };

    public sealed class Context
    {
        public string ChannelKindKey { get; init; } = string.Empty;
        public uint EffectiveEmbedColor { get; init; }
        public uint? EmbedColorOverride { get; init; }
        public Config.EmbedBorderSettings Border { get; init; } = Config.EmbedBorderSettings.CreateDefault(global::DemiCatPlugin.ChannelKind.Chat);
        public EmojiManager? EmojiManager { get; init; }
        public float EmojiTileSize { get; init; } = Config.MinEmojiTileSize;
        public float EmojiGridHeight { get; init; } = Config.MinEmojiGridHeight;
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
        var glyphPopupId = $"embedBorderGlyphPopup##{context.ChannelKindKey}";
        var glyphButtonSize = new Vector2(ImGui.GetFrameHeight() * 1.8f, ImGui.GetFrameHeight());
        using (context.EmojiManager?.PushEmojiFont())
        {
            if (ImGui.Button($"{glyphSymbol}##embedBorderGlyph_{context.ChannelKindKey}", glyphButtonSize))
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

        var manager = context.EmojiManager;
        var searchKey = GetSearchKey(context);
        if (manager == null || !manager.CanLoadStandard)
        {
            if (OfflineBorderGlyphs.Length == 0)
            {
                ImGui.TextDisabled("Emoji list unavailable. Link DemiCat to load emoji.");
            }
            else
            {
                ImGui.TextDisabled("Emoji list unavailable. Using built-in symbols.");
                ImGui.Separator();

                var tileSize = Math.Clamp(context.EmojiTileSize, Config.MinEmojiTileSize, Config.MaxEmojiTileSize);
                var avail = Math.Max(1f, ImGui.GetContentRegionAvail().X);
                var columns = Math.Max(1, (int)Math.Floor((avail + 4f) / (tileSize + 4f)));
                var column = 0;

                for (var i = 0; i < OfflineBorderGlyphs.Length; i++)
                {
                    if (column >= columns)
                    {
                        ImGui.NewLine();
                        column = 0;
                    }

                    var glyph = OfflineBorderGlyphs[i];
                    ImGui.PushID(i);
                    if (ImGui.Button(glyph, new Vector2(tileSize, tileSize)))
                    {
                        selected = glyph;
                    }
                    ImGui.PopID();

                    column++;
                    if (column < columns)
                    {
                        ImGui.SameLine();
                    }

                    if (!string.IsNullOrEmpty(selected))
                    {
                        break;
                    }
                }
            }
        }
        else
        {
            if (!GlyphSearchTerms.TryGetValue(searchKey, out var search))
            {
                search = string.Empty;
            }

            if (ImGui.InputTextWithHint($"##borderEmojiSearch_{searchKey}", "Search…", ref search, 64))
            {
                GlyphSearchTerms[searchKey] = search;
            }

            ImGui.Separator();

            _ = manager.EnsureUnicodeAsync();
            var status = manager.UnicodeStatus;
            var items = manager.Unicode;
            IReadOnlyList<UnicodeEmoji> filtered = items;

            if (!string.IsNullOrWhiteSpace(search) && items.Count > 0)
            {
                var matches = new List<UnicodeEmoji>();
                foreach (var emoji in items)
                {
                    if (emoji.Name.Contains(search, StringComparison.OrdinalIgnoreCase) ||
                        emoji.Emoji.Contains(search, StringComparison.OrdinalIgnoreCase))
                    {
                        matches.Add(emoji);
                    }
                }
                filtered = matches;
            }

            if (filtered.Count == 0)
            {
                if (status.Loading)
                {
                    ImGui.TextDisabled("Loading emoji…");
                }
                else if (status.HasError && !string.IsNullOrEmpty(status.Error))
                {
                    ImGui.TextWrapped(status.Error);
                }
                else
                {
                    ImGui.TextDisabled(string.IsNullOrWhiteSpace(search) ? "No emoji available." : "No matches.");
                }
            }
            else
            {
                var tileSize = Math.Clamp(context.EmojiTileSize, Config.MinEmojiTileSize, Config.MaxEmojiTileSize);
                var childSize = context.EmojiGridHeight > 0f ? new Vector2(0f, context.EmojiGridHeight) : Vector2.Zero;
                ImGui.BeginChild($"##borderEmojiGrid_{searchKey}", childSize, false);

                var avail = Math.Max(1f, ImGui.GetContentRegionAvail().X);
                var columns = Math.Max(1, (int)Math.Floor((avail + 4f) / (tileSize + 4f)));
                var column = 0;

                using var font = manager.PushEmojiFont();
                for (var i = 0; i < filtered.Count; i++)
                {
                    if (column >= columns)
                    {
                        ImGui.NewLine();
                        column = 0;
                    }

                    var emoji = filtered[i];
                    ImGui.PushID(i);
                    if (ImGui.Button(emoji.Emoji, new Vector2(tileSize, tileSize)))
                    {
                        selected = emoji.Emoji;
                    }
                    if (!string.IsNullOrEmpty(emoji.Name) && ImGui.IsItemHovered())
                    {
                        ImGui.SetTooltip(emoji.Name);
                    }
                    ImGui.PopID();

                    column++;
                    if (column < columns)
                    {
                        ImGui.SameLine();
                    }

                    if (!string.IsNullOrEmpty(selected))
                    {
                        break;
                    }
                }

                ImGui.EndChild();
            }
        }

        if (!string.IsNullOrEmpty(selected))
        {
            GlyphSearchTerms[searchKey] = string.Empty;
            ImGui.CloseCurrentPopup();
        }

        ImGui.EndPopup();
        return string.IsNullOrEmpty(selected) ? null : Config.SanitizeEmbedBorderGlyph(selected);
    }

    private static string GetSearchKey(Context context)
        => string.IsNullOrWhiteSpace(context.ChannelKindKey) ? "default" : context.ChannelKindKey;

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
