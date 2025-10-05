using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Numerics;
using System.Linq;
using ImGuiNET;
using Dalamud.Interface.Textures;
using DiscordHelper;
using DemiCat.UI;
using DemiCatPlugin.Emoji;

namespace DemiCatPlugin;

public static class EmbedRenderer
{
    private const int ThumbnailCacheCapacity = 64;

    private static readonly Dictionary<string, ThumbnailCacheEntry> ThumbnailCache = new();
    private static readonly LinkedList<string> ThumbnailLru = new();

    public static void Draw(EmbedDto dto, Action<string?, Action<ISharedImmediateTexture?>> loadTexture, EmojiManager emojiManager, Action<string>? onButtonClick = null)
    {
        using var emojiFont = emojiManager.PushEmojiFont();
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

        if (!string.IsNullOrEmpty(dto.ThumbnailUrl))
        {
            var entry = GetOrCreateThumbnailEntry(dto.ThumbnailUrl, out var created);
            if (created)
            {
                loadTexture(dto.ThumbnailUrl, t => SetThumbnailTexture(dto.ThumbnailUrl, t));
            }

            var tex = entry.Texture;
            if (tex != null)
            {
                var wrap = tex.GetWrapOrEmpty();
                var handle = wrap.ToImGuiHandle();
                if (handle != 0 && wrap.Width > 0 && wrap.Height > 0)
                {
                    ImGui.Image(handle, new Vector2(wrap.Width, wrap.Height));
                }
            }
        }

        if (dto.Buttons != null)
        {
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

                    if (!string.IsNullOrEmpty(button.Emoji))
                    {
                        EmojiRenderer.Draw(button.Emoji, emojiManager);
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

                    var width = ButtonSizeHelper.ResolveWidth(button.Width, button.Label);
                    if (ImGui.Button($"{button.Label}##{id}{dto.Id}", new Vector2(width, 0)))
                    {
                        if (!string.IsNullOrEmpty(button.Url))
                        {
                            try { Process.Start(new ProcessStartInfo(button.Url) { UseShellExecute = true }); } catch { }
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
    }

    public static void ReleaseTexture(ISharedImmediateTexture? texture)
    {
        if (texture == null)
        {
            return;
        }

        foreach (var (key, entry) in ThumbnailCache.ToList())
        {
            if (!ReferenceEquals(entry.Texture, texture))
            {
                continue;
            }

            ThumbnailLru.Remove(entry.Node);
            ThumbnailCache.Remove(key);
            entry.Texture = null;
        }
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
        foreach (var entry in ThumbnailCache.Values)
        {
            DisposeWrap(entry.Texture);
        }

        ThumbnailCache.Clear();
        ThumbnailLru.Clear();
    }

    private static ThumbnailCacheEntry GetOrCreateThumbnailEntry(string key, out bool created)
    {
        if (ThumbnailCache.TryGetValue(key, out var existing))
        {
            created = false;
            Touch(existing);
            return existing;
        }

        var node = new LinkedListNode<string>(key);
        ThumbnailLru.AddFirst(node);
        var entry = new ThumbnailCacheEntry(node);
        ThumbnailCache[key] = entry;
        EnforceThumbnailCapacity();
        created = true;
        return entry;
    }

    private static void SetThumbnailTexture(string key, ISharedImmediateTexture? texture)
    {
        if (!ThumbnailCache.TryGetValue(key, out var entry))
        {
            DisposeWrap(texture);
            return;
        }

        if (!ReferenceEquals(entry.Texture, texture))
        {
            DisposeWrap(entry.Texture);
        }

        entry.Texture = texture;
        Touch(entry);
        EnforceThumbnailCapacity();
    }

    private static void EnforceThumbnailCapacity()
    {
        while (ThumbnailCache.Count > ThumbnailCacheCapacity)
        {
            var tail = ThumbnailLru.Last;
            if (tail == null)
            {
                break;
            }

            ThumbnailLru.RemoveLast();

            if (ThumbnailCache.Remove(tail.Value, out var entry))
            {
                DisposeWrap(entry.Texture);
            }
        }
    }

    private static void Touch(ThumbnailCacheEntry entry)
    {
        ThumbnailLru.Remove(entry.Node);
        ThumbnailLru.AddFirst(entry.Node);
    }

    private static void DisposeWrap(ISharedImmediateTexture? texture)
    {
        if (texture?.GetWrapOrEmpty() is IDisposable disposable)
        {
            disposable.Dispose();
        }
    }

    private sealed class ThumbnailCacheEntry
    {
        public ThumbnailCacheEntry(LinkedListNode<string> node)
        {
            Node = node;
        }

        public LinkedListNode<string> Node { get; }
        public ISharedImmediateTexture? Texture { get; set; }
    }
}

