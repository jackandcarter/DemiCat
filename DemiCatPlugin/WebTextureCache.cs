using System;
using System.Collections.Generic;
using System.Numerics;
using ImGuiNET;
using Dalamud.Interface.Textures;

namespace DemiCatPlugin;

// Minimal web texture cache used by the emoji picker & tests.
// NOTE: In production we rely on the caller's texture loader; here we only
// provide a cache shim + test override. If no override is set, we just
// invoke the callback with null so callers can fall back gracefully.
public static class WebTextureCache
{
    private sealed class CacheEntry : IDisposable
    {
        public CacheEntry(ISharedImmediateTexture texture)
        {
            Texture = texture;
        }

        public ISharedImmediateTexture Texture { get; }

        public void Dispose()
        {
            try
            {
                (Texture.GetWrapOrEmpty() as IDisposable)?.Dispose();
            }
            catch
            {
                // Suppress disposal failures; Dalamud will clean these up on exit.
            }
        }
    }

    private static readonly Dictionary<string, CacheEntry> _map = new();
    private static readonly object _lock = new();

    // Tests set this to intercept fetches and provide mocked textures.
    public static Func<string, Action<ISharedImmediateTexture?>, object?>? FetchOverride { get; set; }

    public static void Get(string url, Action<ISharedImmediateTexture?> onReady)
    {
        if (FetchOverride != null)
        {
            FetchOverride(url, onReady);
            return;
        }

        if (string.IsNullOrEmpty(url))
        {
            onReady(null);
            return;
        }

        if (TryGetTexture(url, out var tex))
        {
            onReady(tex);
            return;
        }

        // No engine-level URL loader available here; allow caller to render fallback.
        onReady(null);
    }

    internal static bool TryGetTexture(string url, out ISharedImmediateTexture? texture)
    {
        lock (_lock)
        {
            if (_map.TryGetValue(url, out var entry))
            {
                texture = entry.Texture;
                return true;
            }
        }

        texture = null;
        return false;
    }

    internal static void Set(string url, ISharedImmediateTexture? texture)
    {
        lock (_lock)
        {
            if (texture == null)
            {
                if (_map.Remove(url, out var removed))
                {
                    removed.Dispose();
                }
                return;
            }

            if (_map.TryGetValue(url, out var existing))
            {
                if (ReferenceEquals(existing.Texture, texture))
                    return;

                existing.Dispose();
            }

            _map[url] = new CacheEntry(texture);
        }
    }

    internal static void Clear()
    {
        lock (_lock)
        {
            foreach (var entry in _map.Values)
            {
                entry.Dispose();
            }

            _map.Clear();
        }
    }

    public static void DrawImageButton(string id, ISharedImmediateTexture? tex, Vector2 size, Action onClick)
    {
        if (tex == null) return;
        var wrap = tex.GetWrapOrEmpty();

        var handle = wrap.ToImGuiHandle();
        if (handle != 0 && wrap.Width > 0 && wrap.Height > 0 &&
            ImGui.ImageButton(
                $"##{id}",
                handle,
                size,
                Vector2.Zero,
                Vector2.One,
                Vector4.Zero,
                Vector4.One))
        {
            onClick();
        }
    }

}
