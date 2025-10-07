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
        public CacheEntry(ISharedImmediateTexture texture, long estimatedBytes, LinkedListNode<string> node)
        {
            Texture = texture;
            EstimatedBytes = estimatedBytes;
            Node = node;
        }

        public ISharedImmediateTexture Texture { get; }
        public long EstimatedBytes { get; }
        public LinkedListNode<string> Node { get; }

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

    private const long MaxBytes = 64L * 1024 * 1024; // 64 MB soft cap for cached textures

    private static readonly Dictionary<string, CacheEntry> _map = new();
    private static readonly LinkedList<string> _lru = new();
    private static readonly object _lock = new();
    private static long _currentBytes;

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
                MoveToFront(entry);
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
                    _lru.Remove(removed.Node);
                    SubtractBytes(removed.EstimatedBytes);
                    removed.Dispose();
                }
                return;
            }

            if (_map.TryGetValue(url, out var existing))
            {
                if (ReferenceEquals(existing.Texture, texture))
                {
                    MoveToFront(existing);
                    return;
                }

                _lru.Remove(existing.Node);
                SubtractBytes(existing.EstimatedBytes);
                existing.Dispose();
            }

            var bytes = EstimateBytes(texture);
            var node = new LinkedListNode<string>(url);
            _lru.AddFirst(node);
            _map[url] = new CacheEntry(texture, bytes, node);
            _currentBytes = Math.Min(long.MaxValue, _currentBytes + bytes);

            TrimToBudget();
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
            _lru.Clear();
            _currentBytes = 0;
        }
    }

    private static void MoveToFront(CacheEntry entry)
    {
        if (entry.Node.List == _lru)
        {
            _lru.Remove(entry.Node);
            _lru.AddFirst(entry.Node);
        }
    }

    private static void TrimToBudget()
    {
        while (_currentBytes > MaxBytes && _lru.Last is { } tail)
        {
            var key = tail.Value;
            _lru.RemoveLast();
            if (_map.Remove(key, out var entry))
            {
                SubtractBytes(entry.EstimatedBytes);
                try
                {
                    entry.Dispose();
                }
                catch
                {
                    // Disposal must never surface exceptions on eviction.
                }
            }
        }
    }

    private static long EstimateBytes(ISharedImmediateTexture texture)
    {
        try
        {
            var wrap = texture.GetWrapOrEmpty();
            if (wrap.Width <= 0 || wrap.Height <= 0)
            {
                return 0;
            }

            // RGBA32 textures (the format produced by our loader) are 4 bytes per pixel.
            var pixels = (long)wrap.Width * wrap.Height;
            return Math.Min(MaxBytes, pixels * 4L);
        }
        catch
        {
            return 0;
        }
    }

    private static void SubtractBytes(long value)
    {
        if (value <= 0)
        {
            return;
        }

        _currentBytes -= value;
        if (_currentBytes < 0)
        {
            _currentBytes = 0;
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
