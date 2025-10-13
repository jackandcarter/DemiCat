using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
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
        public CacheEntry(ISharedImmediateTexture texture, long lastAccess)
        {
            Texture = texture;
            LastAccess = lastAccess;
        }

        public ISharedImmediateTexture Texture { get; }
        public long LastAccess { get; private set; }

        public void Touch(long timestamp)
        {
            LastAccess = timestamp;
        }

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

    private const int DefaultCapacity = 256;

    private static readonly Dictionary<string, CacheEntry> _map = new();
    private static readonly object _lock = new();
    private static int _capacity = DefaultCapacity;

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
                entry.Touch(Environment.TickCount64);
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

            var now = Environment.TickCount64;

            if (_map.TryGetValue(url, out var existing))
            {
                if (ReferenceEquals(existing.Texture, texture))
                {
                    existing.Touch(now);
                    return;
                }

                existing.Dispose();
            }

            _map[url] = new CacheEntry(texture, now);
            TrimExcessLocked();
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

    internal static void SetCapacity(int capacity)
    {
        if (capacity <= 0)
        {
            capacity = 1;
        }

        lock (_lock)
        {
            _capacity = capacity;
            TrimExcessLocked();
        }
    }

    internal static int Capacity
    {
        get
        {
            lock (_lock)
            {
                return _capacity;
            }
        }
    }

    internal static int Count
    {
        get
        {
            lock (_lock)
            {
                return _map.Count;
            }
        }
    }

    private static void TrimExcessLocked()
    {
        var capacity = _capacity;
        if (_map.Count <= capacity)
        {
            return;
        }

        var toRemove = _map
            .OrderBy(pair => pair.Value.LastAccess)
            .Take(_map.Count - capacity)
            .Select(pair => pair.Key)
            .ToList();

        foreach (var key in toRemove)
        {
            if (_map.Remove(key, out var entry))
            {
                entry.Dispose();
            }
        }
    }

    public static void DrawImageButton(string id, ISharedImmediateTexture? tex, Vector2 size, Action onClick)
    {
        if (tex == null) return;
        var wrap = tex.GetWrapOrEmpty();

        // In this binding, ImageButton takes (ImTextureID, Vector2) and
        // IDs are provided via PushID/PopID (not a string label param).
        ImGui.PushID(id);
        if (ImGui.ImageButton(wrap.Handle, size))
            onClick();
        ImGui.PopID();
    }

}
