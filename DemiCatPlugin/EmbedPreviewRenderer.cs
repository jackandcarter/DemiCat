using System;
using System.Collections.Generic;
using System.Numerics;
using System.Linq;
using ImGuiNET;
using Dalamud.Interface.Textures;
using DiscordHelper;
using DemiCat.UI;
using DemiCatPlugin.Emoji;

namespace DemiCatPlugin;

public static class EmbedPreviewRenderer
{
    private const int TextureCacheCapacity = 96;

    private static readonly Dictionary<string, CacheEntry> TextureCache = new(StringComparer.Ordinal);
    private static readonly LinkedList<string> TextureLru = new();

    public static RenderResult Draw(
        EmbedDto dto,
        Action<string?, Action<ISharedImmediateTexture?>> loadTexture,
        EmojiManager emojiManager,
        bool allowAutoLoad,
        Vector2 maxThumbnailSize,
        Vector2 maxImageSize,
        Action<string>? onButtonClick = null)
    {
        using var emojiFont = emojiManager.PushEmojiFont();
        const float stripeWidth = 4f;
        const float contentPadding = 8f;
        const float verticalPadding = 4f;

        var borderInfo = dto.Border;
        var borderEnabled = borderInfo?.Enabled == true;
        var borderColor = borderInfo?.Color ?? 0u;

        var indent = contentPadding + (dto.Color.HasValue ? stripeWidth : 0f);
        var avail = ImGui.GetContentRegionAvail().X;
        if (avail <= 0)
        {
            avail = 400;
        }

        ImGui.BeginChild($"embedprev{dto.Id}", new Vector2(avail, 0), true, ImGuiWindowFlags.None);

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

        if (!string.IsNullOrEmpty(dto.Description))
        {
            BeginSection();
            ImGui.TextWrapped(dto.Description);
        }

        var thumbnailRendered = false;
        var thumbnailDeferred = false;
        if (!string.IsNullOrEmpty(dto.ThumbnailUrl))
        {
            var tex = GetTexture(dto.ThumbnailUrl!, allowAutoLoad, loadTexture, out var suppressed);
            thumbnailDeferred = suppressed;
            if (tex != null)
            {
                BeginSection();
                var wrap = tex.GetWrapOrEmpty();
                var handle = wrap.ToImGuiHandle();
                if (handle != 0 && wrap.Width > 0 && wrap.Height > 0)
                {
                    var size = CalculateDisplaySize(new Vector2(wrap.Width, wrap.Height), maxThumbnailSize);
                    ImGui.Image(handle, size);
                    thumbnailRendered = true;
                }
            }
        }

        if (dto.Fields != null && dto.Fields.Count > 0)
        {
            BeginSection();
            DrawFields(dto);
        }

        var imageRendered = false;
        var imageDeferred = false;
        if (!string.IsNullOrEmpty(dto.ImageUrl))
        {
            var tex = GetTexture(dto.ImageUrl!, allowAutoLoad, loadTexture, out var suppressed);
            imageDeferred = suppressed;
            if (tex != null)
            {
                BeginSection();
                var wrap = tex.GetWrapOrEmpty();
                var handle = wrap.ToImGuiHandle();
                if (handle != 0 && wrap.Width > 0 && wrap.Height > 0)
                {
                    var size = CalculateDisplaySize(new Vector2(wrap.Width, wrap.Height), maxImageSize);
                    ImGui.Image(handle, size);
                    imageRendered = true;
                }
            }
        }

        var footerText = dto.FooterText ?? string.Empty;
        if (dto.Timestamp.HasValue)
        {
            if (footerText.Length > 0)
            {
                footerText += " • ";
            }

            footerText += dto.Timestamp.Value.LocalDateTime.ToString();
        }

        if (!string.IsNullOrEmpty(footerText))
        {
            BeginSection();
            ImGui.TextUnformatted(footerText);
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

        ImGui.EndChild();
        var rectMin = ImGui.GetItemRectMin();
        var rectMax = ImGui.GetItemRectMax();
        if (dto.Color.HasValue)
        {
            var color = ColorUtils.RgbToImGui(dto.Color.Value);
            ImGui.GetWindowDrawList().AddRectFilled(rectMin, new Vector2(rectMin.X + stripeWidth, rectMax.Y), color);
        }

        if (borderEnabled && rectMax.X > rectMin.X && rectMax.Y > rectMin.Y)
        {
            var drawList = ImGui.GetWindowDrawList();
            var color = ColorUtils.RgbToImGui(borderColor);
            var inset = new Vector2(1f, 1f);
            drawList.AddRect(rectMin + inset, rectMax - inset, color, 0f, ImDrawFlags.None, 1.5f);
        }

        return new RenderResult(thumbnailRendered, thumbnailDeferred, imageRendered, imageDeferred);
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

    private static ISharedImmediateTexture? GetTexture(
        string url,
        bool allowAutoLoad,
        Action<string?, Action<ISharedImmediateTexture?>> loadTexture,
        out bool loadSuppressed)
    {
        loadSuppressed = false;

        if (!TextureCache.TryGetValue(url, out var entry))
        {
            entry = CreateEntry(url);
        }
        else
        {
            Touch(entry);
        }

        if (entry.Texture != null)
        {
            return entry.Texture;
        }

        if (!allowAutoLoad)
        {
            loadSuppressed = true;
            entry.IsLoading = false;
            return null;
        }

        if (!entry.IsLoading)
        {
            entry.IsLoading = true;
            loadTexture(url, t => SetTexture(url, t));
        }

        return null;
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
        foreach (var entry in TextureCache.Values)
        {
            DisposeEntry(entry);
        }

        TextureCache.Clear();
        TextureLru.Clear();
    }

    public static void ReleaseTexture(ISharedImmediateTexture? texture)
    {
        if (texture == null)
        {
            return;
        }

        foreach (var (key, entry) in TextureCache.ToList())
        {
            if (!ReferenceEquals(entry.Texture, texture))
            {
                continue;
            }

            TextureLru.Remove(entry.Node);
            TextureCache.Remove(key);
            entry.Texture = null;
            entry.IsLoading = false;
        }
    }

    private static CacheEntry CreateEntry(string key)
    {
        var node = TextureLru.AddFirst(key);
        var entry = new CacheEntry(key, node);
        TextureCache[key] = entry;
        EnforceCapacity();
        return entry;
    }

    private static void SetTexture(string key, ISharedImmediateTexture? texture)
    {
        if (!TextureCache.TryGetValue(key, out var entry))
        {
            DisposeWrap(texture);
            return;
        }

        entry.IsLoading = false;

        if (!ReferenceEquals(entry.Texture, texture))
        {
            DisposeWrap(entry.Texture);
        }

        entry.Texture = texture;
        Touch(entry);
        EnforceCapacity();
    }

    private static void Touch(CacheEntry entry)
    {
        if (entry.Node.List == TextureLru)
        {
            TextureLru.Remove(entry.Node);
            TextureLru.AddFirst(entry.Node);
        }
        else
        {
            entry.Node = TextureLru.AddFirst(entry.Key);
        }
    }

    private static void EnforceCapacity()
    {
        while (TextureCache.Count > TextureCacheCapacity)
        {
            var tail = TextureLru.Last;
            if (tail == null)
            {
                break;
            }

            TextureLru.RemoveLast();
            if (TextureCache.Remove(tail.Value, out var removed))
            {
                DisposeEntry(removed);
            }
        }
    }

    private static void DisposeEntry(CacheEntry entry)
    {
        DisposeWrap(entry.Texture);
        entry.Texture = null;
        entry.IsLoading = false;
    }

    private static void DisposeWrap(ISharedImmediateTexture? texture)
    {
        if (texture?.GetWrapOrEmpty() is IDisposable wrap)
        {
            wrap.Dispose();
        }
    }

    private static Vector2 CalculateDisplaySize(Vector2 originalSize, Vector2 maxSize)
    {
        var width = MathF.Max(1f, originalSize.X);
        var height = MathF.Max(1f, originalSize.Y);

        var maxWidth = maxSize.X > 0f ? maxSize.X : width;
        var maxHeight = maxSize.Y > 0f ? maxSize.Y : height;

        var widthScale = maxWidth / width;
        var heightScale = maxHeight / height;
        var scale = MathF.Min(1f, MathF.Min(widthScale, heightScale));

        if (!float.IsFinite(scale) || scale <= 0f)
        {
            scale = 1f;
        }

        return new Vector2(width * scale, height * scale);
    }

    private sealed class CacheEntry
    {
        public CacheEntry(string key, LinkedListNode<string> node)
        {
            Key = key;
            Node = node;
        }

        public string Key { get; }
        public LinkedListNode<string> Node { get; set; }
        public ISharedImmediateTexture? Texture { get; set; }
        public bool IsLoading { get; set; }
    }

    public readonly struct RenderResult
    {
        public RenderResult(bool thumbnailRendered, bool thumbnailDeferred, bool imageRendered, bool imageDeferred)
        {
            ThumbnailRendered = thumbnailRendered;
            ThumbnailDeferred = thumbnailDeferred;
            ImageRendered = imageRendered;
            ImageDeferred = imageDeferred;
        }

        public bool ThumbnailRendered { get; }
        public bool ThumbnailDeferred { get; }
        public bool ImageRendered { get; }
        public bool ImageDeferred { get; }

        public bool AnyDeferred => ThumbnailDeferred || ImageDeferred;
    }
}

