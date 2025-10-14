using System;
using System.Collections.Generic;
using System.Numerics;
using System.Linq;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Textures;
using DiscordHelper;
using DemiCatPlugin.Emoji;

namespace DemiCatPlugin;

public static class EmbedPreviewRenderer
{
    private static readonly Dictionary<string, ISharedImmediateTexture?> TextureCache = new();

    public static void Draw(EmbedDto dto, Action<string?, Action<ISharedImmediateTexture?>> loadTexture, EmojiManager emojiManager, Action<string>? onButtonClick = null)
    {
        using var emojiFont = emojiManager.PushEmojiFont();
        var style = ImGui.GetStyle();
        var stripeWidth = dto.Color.HasValue ? Math.Max(6f, style.FramePadding.X * 0.9f) : 0f;
        const float contentPadding = 8f;
        const float verticalPadding = 4f;

        var borderInfo = dto.Border;
        var borderEnabled = borderInfo?.Enabled == true;
        var borderColor = borderInfo?.Color ?? 0u;

        var indent = contentPadding + stripeWidth;
        var avail = ImGui.GetContentRegionAvail().X;
        if (avail <= 0)
        {
            avail = 400;
        }

        ImGui.BeginChild($"embedprev{dto.Id}", new Vector2(avail, 0), true);

        ImGui.Indent(indent);
        ImGui.Dummy(new Vector2(0f, verticalPadding));

        var hasContent = false;
        void BeginSection()
        {
            if (hasContent)
            {
                ImGui.Spacing();
            }
            else
            {
                hasContent = true;
            }
        }

        if (!string.IsNullOrEmpty(dto.ProviderName))
        {
            BeginSection();
            ImGui.TextUnformatted(dto.ProviderName);
        }

        if (dto.Authors != null && dto.Authors.Count > 0)
        {
            var names = dto.Authors
                .Where(a => !string.IsNullOrWhiteSpace(a.Name))
                .Select(a => a.Name!)
                .ToList();

            if (names.Count > 0)
            {
                BeginSection();
                ImGui.TextUnformatted(string.Join(", ", names));
            }
        }
        else if (!string.IsNullOrWhiteSpace(dto.AuthorName))
        {
            BeginSection();
            ImGui.TextUnformatted(dto.AuthorName);
        }

        if (!string.IsNullOrEmpty(dto.Title))
        {
            BeginSection();
            ImGui.TextUnformatted(dto.Title);
        }

        var description = EventEmbedHelpers.RemoveAppendedStartLine(dto.Description, dto.Timestamp, out _);
        if (!string.IsNullOrEmpty(description))
        {
            BeginSection();
            ImGui.TextWrapped(description);
        }

        if (!string.IsNullOrEmpty(dto.ThumbnailUrl))
        {
            var tex = GetTexture(dto.ThumbnailUrl!, loadTexture);
            if (tex != null)
            {
                BeginSection();
                var wrap = tex.GetWrapOrEmpty();
                ImGui.Image(wrap.Handle, new Vector2(wrap.Width, wrap.Height));
            }
        }

        if (dto.Fields != null && dto.Fields.Count > 0)
        {
            BeginSection();
            DrawFields(dto);
        }

        if (!string.IsNullOrEmpty(dto.ImageUrl))
        {
            var tex = GetTexture(dto.ImageUrl!, loadTexture);
            if (tex != null)
            {
                BeginSection();
                var wrap = tex.GetWrapOrEmpty();
                ImGui.Image(wrap.Handle, new Vector2(wrap.Width, wrap.Height));
            }
        }

        var footerText = dto.FooterText ?? string.Empty;

        if (!string.IsNullOrEmpty(footerText))
        {
            BeginSection();
            ImGui.TextUnformatted(footerText);
        }

        var showStartTime = EventEmbedHelpers.ShouldDisplayStartTime(dto);
        if (showStartTime)
        {
            BeginSection();
            var label = $"Starts: {EventEmbedHelpers.FormatLocalStartTime(dto.Timestamp!.Value)}";
            ImGui.TextUnformatted(label);
        }

        if (dto.Buttons != null && dto.Buttons.Count > 0)
        {
            BeginSection();
            foreach (var group in dto.Buttons
                         .GroupBy(b => b.RowIndex ?? 0)
                         .OrderBy(g => g.Key))
            {
                var i = 0;
                foreach (var button in group)
                {
                    if (i > 0 && i % 5 != 0)
                    {
                        ImGui.SameLine();
                    }

                    var id = button.CustomId ?? button.Label;
                    var styled = button.Style.HasValue && button.Style.Value != ButtonStyle.Link;
                    if (styled)
                    {
                        var color = GetStyleColor(button.Style!.Value);
                        ImGui.PushStyleColor(ImGuiCol.Button, color);
                        ImGui.PushStyleColor(ImGuiCol.ButtonHovered, Lighten(color, 1.1f));
                        ImGui.PushStyleColor(ImGuiCol.ButtonActive, Lighten(color, 1.2f));
                    }

                    var w = button.Width ?? -1;
                    if (EmbedButtonRenderer.Draw(button.Label, $"{id}{dto.Id}", button.Emoji, emojiManager, new Vector2(w, 0)))
                    {
                        if (!string.IsNullOrEmpty(button.Url))
                        {
                            try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(button.Url) { UseShellExecute = true }); } catch { }
                        }
                        else if (!string.IsNullOrEmpty(button.CustomId))
                        {
                            onButtonClick?.Invoke(button.CustomId);
                        }
                    }

                    if (styled)
                    {
                        ImGui.PopStyleColor(3);
                    }

                    i++;
                }
            }
        }

        ImGui.Dummy(new Vector2(0f, verticalPadding));
        ImGui.Unindent(indent);

        if (dto.Color.HasValue)
        {
            var dl = ImGui.GetWindowDrawList();
            var cardMin = ImGui.GetWindowPos();
            var cardMax = cardMin + ImGui.GetWindowSize();
            var cardRounding = style.ChildRounding;
            var borderInset = Math.Max(0f, style.ChildBorderSize > 0f ? style.ChildBorderSize : style.FrameBorderSize);
            var stripeMin = new Vector2(cardMin.X + borderInset, cardMin.Y + borderInset);
            var stripeMax = new Vector2(cardMin.X + borderInset + stripeWidth, cardMax.Y - borderInset);
            if (stripeMax.X > stripeMin.X && stripeMax.Y > stripeMin.Y)
            {
                var colorVec = ColorUtils.RgbToVector4(dto.Color.Value);
                var color = ImGui.ColorConvertFloat4ToU32(colorVec);
                dl.AddRectFilled(stripeMin, stripeMax, color);

                if (cardRounding > 0f)
                {
                    var stripeRounding = Math.Min(cardRounding, Math.Min(stripeWidth, stripeMax.Y - stripeMin.Y) * 0.5f);
                    dl.AddRectFilled(
                        stripeMin,
                        stripeMax,
                        color,
                        stripeRounding,
                        ImDrawFlags.RoundCornersTopLeft | ImDrawFlags.RoundCornersBottomLeft);
                }
            }
        }

        ImGui.EndChild();
        var rectMin = ImGui.GetItemRectMin();
        var rectMax = ImGui.GetItemRectMax();

        if (borderEnabled && rectMax.X > rectMin.X && rectMax.Y > rectMin.Y)
        {
            var drawList = ImGui.GetWindowDrawList();
            var colorVec = ColorUtils.RgbToVector4(borderColor);
            var color = ImGui.ColorConvertFloat4ToU32(colorVec);
            var inset = new Vector2(1f, 1f);
            drawList.AddRect(rectMin + inset, rectMax - inset, color, 0f, ImDrawFlags.None, 1.5f);
        }
    }

    private static void DrawFields(EmbedDto dto)
    {
        var fields = dto.Fields!;
        var index = 0;
        while (index < fields.Count)
        {
            if (fields[index].Inline == true)
            {
                var group = new List<EmbedFieldDto>();
                while (index < fields.Count && fields[index].Inline == true)
                {
                    group.Add(fields[index]);
                    index++;
                }

                var cols = Math.Min(3, group.Count);
                if (ImGui.BeginTable($"ifields{dto.Id}{index}", cols, ImGuiTableFlags.Borders))
                {
                    for (var i = 0; i < group.Count; i++)
                    {
                        if (i % cols == 0)
                        {
                            ImGui.TableNextRow();
                        }

                        ImGui.TableSetColumnIndex(i % cols);
                        var f = group[i];
                        if (!string.IsNullOrEmpty(f.Name))
                        {
                            ImGui.TextUnformatted(f.Name);
                        }
                        if (!string.IsNullOrEmpty(f.Value))
                        {
                            ImGui.TextWrapped(f.Value);
                        }
                    }

                    ImGui.EndTable();
                }
            }
            else
            {
                var f = fields[index];
                index++;
                if (!string.IsNullOrEmpty(f.Name))
                {
                    ImGui.TextUnformatted(f.Name);
                }
                if (!string.IsNullOrEmpty(f.Value))
                {
                    ImGui.TextWrapped(f.Value);
                }
            }
        }
    }

    private static ISharedImmediateTexture? GetTexture(string url, Action<string?, Action<ISharedImmediateTexture?>> loadTexture)
    {
        if (!TextureCache.TryGetValue(url, out var tex))
        {
            TextureCache[url] = null;
            loadTexture(url, t => TextureCache[url] = t);
            tex = null;
        }
        return tex;
    }

    internal static Vector4 GetStyleColor(ButtonStyle style) => style switch
    {
        ButtonStyle.Primary => new Vector4(0.345f, 0.396f, 0.949f, 1f),
        ButtonStyle.Secondary => new Vector4(0.31f, 0.329f, 0.361f, 1f),
        ButtonStyle.Success => new Vector4(0.341f, 0.949f, 0.529f, 1f),
        ButtonStyle.Danger => new Vector4(0.929f, 0.258f, 0.27f, 1f),
        _ => new Vector4(0.345f, 0.396f, 0.949f, 1f),
    };

    internal static Vector4 Lighten(Vector4 color, float amount)
        => new(MathF.Min(color.X * amount, 1f), MathF.Min(color.Y * amount, 1f), MathF.Min(color.Z * amount, 1f), color.W);

    public static void ClearCache()
    {
        TextureCache.Clear();
    }
}

