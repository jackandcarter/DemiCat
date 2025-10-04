using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Threading.Tasks;
using Dalamud.Interface.Textures;
using Dalamud.Plugin.Services;
using StbImageSharp;

namespace DemiCatPlugin.Avatars;

public class AvatarCache : IDisposable
{
    private readonly ITextureProvider _textureProvider;
    private readonly HttpClient _httpClient;
    private readonly Dictionary<string, CacheEntry> _cache = new();
    private readonly LinkedList<string> _lru = new();
    private readonly int _capacity = 256;
    private readonly TimeSpan _ttl = TimeSpan.FromHours(12);
    private readonly object _lock = new();

    private class CacheEntry
    {
        public ISharedImmediateTexture? Texture;
        public DateTime Expiration;
        public Task<ISharedImmediateTexture?>? Pending;
        public LinkedListNode<string>? Node;
    }

    public AvatarCache(ITextureProvider textureProvider, HttpClient httpClient)
    {
        _textureProvider = textureProvider;
        _httpClient = httpClient;
    }

    public Task<ISharedImmediateTexture?> GetAsync(string? avatarUrl, string? userId)
    {
        var url = string.IsNullOrEmpty(avatarUrl) ? DefaultAvatarUrl(userId) : avatarUrl;
        if (string.IsNullOrEmpty(url))
            return Task.FromResult<ISharedImmediateTexture?>(null);

        lock (_lock)
        {
            RemoveExpiredEntriesLocked();

            if (_cache.TryGetValue(url, out var entry))
            {
                if (entry.Texture != null && entry.Expiration > DateTime.UtcNow)
                {
                    MoveToFrontLocked(entry);
                    return Task.FromResult<ISharedImmediateTexture?>(entry.Texture);
                }
                if (entry.Pending != null)
                    return entry.Pending;
            }

            var task = FetchAsync(url);
            var node = _lru.AddFirst(url);
            _cache[url] = new CacheEntry
            {
                Pending = task,
                Node = node
            };
            EvictIfNeededLocked();
            return task;
        }
    }

    private async Task<ISharedImmediateTexture?> FetchAsync(string url)
    {
        try
        {
            var bytes = await _httpClient.GetByteArrayAsync(url).ConfigureAwait(false);
            using var stream = new MemoryStream(bytes);
            var image = ImageResult.FromStream(stream, ColorComponents.RedGreenBlueAlpha);
            var wrap = _textureProvider.CreateFromRaw(
                RawImageSpecification.Rgba32(image.Width, image.Height),
                image.Data);
            var tex = new ForwardingSharedImmediateTexture(wrap);
            lock (_lock)
            {
                if (!_cache.TryGetValue(url, out var existing))
                    return tex;

                if (existing.Texture?.GetWrapOrEmpty() is IDisposable oldWrap)
                    oldWrap.Dispose();

                existing.Texture = tex;
                existing.Pending = null;
                existing.Expiration = DateTime.UtcNow + _ttl;
                MoveToFrontLocked(existing);
            }
            return tex;
        }
        catch
        {
            lock (_lock)
            {
                if (_cache.TryGetValue(url, out var entry))
                {
                    RemoveEntryLocked(url, entry, disposeTexture: true);
                }
            }
            return null;
        }
    }

    private void RemoveExpiredEntriesLocked()
    {
        var now = DateTime.UtcNow;
        List<string>? expired = null;

        foreach (var (key, entry) in _cache)
        {
            if (entry.Pending != null)
                continue;

            if (entry.Expiration != default && entry.Expiration <= now)
            {
                expired ??= new List<string>();
                expired.Add(key);
            }
        }

        if (expired == null)
            return;

        foreach (var key in expired)
        {
            if (_cache.TryGetValue(key, out var entry))
                RemoveEntryLocked(key, entry, disposeTexture: true);
        }
    }

    private void EvictIfNeededLocked()
    {
        while (_cache.Count > _capacity && _lru.Last is { } last)
        {
            var key = last.Value;
            _lru.RemoveLast();
            if (_cache.TryGetValue(key, out var entry))
                RemoveEntryLocked(key, entry, disposeTexture: true);
        }
    }

    private void MoveToFrontLocked(CacheEntry entry)
    {
        if (entry.Node == null)
            return;

        if (entry.Node.List != _lru)
        {
            entry.Node = _lru.AddFirst(entry.Node.Value);
            return;
        }

        _lru.Remove(entry.Node);
        _lru.AddFirst(entry.Node);
    }

    private void RemoveEntryLocked(string key, CacheEntry entry, bool disposeTexture)
    {
        if (disposeTexture && entry.Texture?.GetWrapOrEmpty() is IDisposable wrap)
            wrap.Dispose();

        if (entry.Node != null && entry.Node.List == _lru)
            _lru.Remove(entry.Node);

        _cache.Remove(key);
    }

    private static string DefaultAvatarUrl(string? userId)
    {
        if (string.IsNullOrEmpty(userId))
            return "https://cdn.discordapp.com/embed/avatars/0.png";
        if (ulong.TryParse(userId, out var id))
            return $"https://cdn.discordapp.com/embed/avatars/{id % 5}.png";
        return "https://cdn.discordapp.com/embed/avatars/0.png";
    }

    public void Dispose()
    {
        lock (_lock)
        {
            var keys = new List<string>(_cache.Keys);
            foreach (var key in keys)
            {
                if (_cache.TryGetValue(key, out var entry))
                    RemoveEntryLocked(key, entry, disposeTexture: true);
            }

            _lru.Clear();
            _cache.Clear();
        }
    }
}

