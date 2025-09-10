using System;
using System.Collections.Generic;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Textures;
using DiscordHelper;

namespace DemiCatPlugin;

public static class EmbedPreviewRenderer
{
    private static readonly Dictionary<string, ISharedImmediateTexture?> TextureCache = new();

    public static void Draw(EmbedDto dto, Action<string?, Action<ISharedImmediateTexture?>> loadTexture, Action<string>? onButtonClick = null)
    {
        var stripeWidth = 4f;
        var indent = stripeWidth + 4f;
        var avail = ImGui.GetContentRegionAvail().X;
        if (avail <= 0)
        {
            avail = 400;
        }

        ImGui.BeginChild($"embedprev{dto.Id}", new Vector2(avail, 0), true);
        ImGui.SetCursorPosX(ImGui.GetCursorPosX() + indent);

        if (!string.IsNullOrEmpty(dto.Title))
        {
            ImGui.TextUnformatted(dto.Title);
        }

        if (!string.IsNullOrEmpty(dto.Description))
        {
            ImGui.TextWrapped(dto.Description);
        }

        if (dto.Fields != null && dto.Fields.Count > 0)
        {
            var fields = dto.Fields;
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
                            ImGui.TextUnformatted(f.Name);
                            ImGui.TextWrapped(f.Value);
                        }
                        ImGui.EndTable();
                    }
                }
                else
                {
                    var f = fields[index];
                    index++;
                    ImGui.TextUnformatted(f.Name);
                    ImGui.TextWrapped(f.Value);
                }
            }
        }

        if (!string.IsNullOrEmpty(dto.ImageUrl))
        {
            var tex = GetTexture(dto.ImageUrl!, loadTexture);
            if (tex != null)
            {
                var wrap = tex.GetWrapOrEmpty();
                ImGui.Image(wrap.Handle, new Vector2(wrap.Width, wrap.Height));
            }
        }

        if (!string.IsNullOrEmpty(dto.ThumbnailUrl))
        {
            var tex = GetTexture(dto.ThumbnailUrl!, loadTexture);
            if (tex != null)
            {
                var wrap = tex.GetWrapOrEmpty();
                ImGui.Image(wrap.Handle, new Vector2(wrap.Width, wrap.Height));
            }
        }

        if (!string.IsNullOrEmpty(dto.FooterText) || dto.Timestamp.HasValue)
        {
            var text = dto.FooterText ?? string.Empty;
            if (dto.Timestamp.HasValue)
            {
                if (text.Length > 0)
                {
                    text += " • ";
                }
                text += dto.Timestamp.Value.LocalDateTime.ToString();
            }
            ImGui.Separator();
            ImGui.TextUnformatted(text);
        }

        if (dto.Buttons != null)
        {
            foreach (var button in dto.Buttons)
            {
                var id = button.CustomId ?? button.Label;
                var text = string.IsNullOrEmpty(button.Emoji) ? button.Label : $"{button.Emoji} {button.Label}";
                var styled = button.Style.HasValue && button.Style.Value != ButtonStyle.Link;
                if (styled)
                {
                    var color = GetStyleColor(button.Style!.Value);
                    ImGui.PushStyleColor(ImGuiCol.Button, color);
                    ImGui.PushStyleColor(ImGuiCol.ButtonHovered, Lighten(color, 1.1f));
                    ImGui.PushStyleColor(ImGuiCol.ButtonActive, Lighten(color, 1.2f));
                }
                var w = button.Width ?? -1;
                var h = button.Height ?? 0;
                if (ImGui.Button($"{text}##{id}{dto.Id}", new Vector2(w, h)))
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
            }
        }

        ImGui.EndChild();
        if (dto.Color.HasValue)
        {
            var min = ImGui.GetItemRectMin();
            var max = ImGui.GetItemRectMax();
            var color = ColorUtils.RgbToImGui(dto.Color.Value);
            ImGui.GetWindowDrawList().AddRectFilled(min, new Vector2(min.X + stripeWidth, max.Y), color);
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

    private static Vector4 GetStyleColor(ButtonStyle style) => style switch
    {
        ButtonStyle.Primary => new Vector4(0.345f, 0.396f, 0.949f, 1f),
        ButtonStyle.Secondary => new Vector4(0.31f, 0.329f, 0.361f, 1f),
        ButtonStyle.Success => new Vector4(0.341f, 0.949f, 0.529f, 1f),
        ButtonStyle.Danger => new Vector4(0.929f, 0.258f, 0.27f, 1f),
        _ => new Vector4(0.345f, 0.396f, 0.949f, 1f),
    };

    private static Vector4 Lighten(Vector4 color, float amount)
        => new(MathF.Min(color.X * amount, 1f), MathF.Min(color.Y * amount, 1f), MathF.Min(color.Z * amount, 1f), color.W);

    public static void ClearCache()
    {
        TextureCache.Clear();
    }
}

