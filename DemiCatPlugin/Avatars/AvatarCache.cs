using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Dalamud.Interface.Textures;
using Dalamud.Plugin.Services;

namespace DemiCatPlugin.Avatars;

public class AvatarCache : IDisposable
{
    private readonly ITextureProvider _textureProvider;
    private readonly Dictionary<string, CacheEntry> _cache = new();
    private readonly TimeSpan _ttl = TimeSpan.FromHours(12);
    private readonly object _lock = new();

    private class CacheEntry
    {
        public ISharedImmediateTexture? Texture;
        public DateTime Expiration;
        public Task<ISharedImmediateTexture?>? Pending;
    }

    public AvatarCache(ITextureProvider textureProvider)
    {
        _textureProvider = textureProvider;
    }

    public Task<ISharedImmediateTexture?> GetAsync(string? avatarUrl, string? userId)
    {
        var url = string.IsNullOrEmpty(avatarUrl) ? DefaultAvatarUrl(userId) : avatarUrl;
        if (string.IsNullOrEmpty(url))
            return Task.FromResult<ISharedImmediateTexture?>(null);

        lock (_lock)
        {
            if (_cache.TryGetValue(url, out var entry))
            {
                if (entry.Texture != null && entry.Expiration > DateTime.UtcNow)
                    return Task.FromResult(entry.Texture);
                if (entry.Pending != null)
                    return entry.Pending;
            }

            var task = FetchAsync(url);
            _cache[url] = new CacheEntry { Pending = task };
            return task;
        }
    }

    private async Task<ISharedImmediateTexture?> FetchAsync(string url)
    {
        var tex = await _textureProvider.GetFromUrlAsync(url);
        lock (_lock)
        {
            _cache[url] = new CacheEntry
            {
                Texture = tex,
                Expiration = DateTime.UtcNow + _ttl
            };
        }
        return tex;
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
            foreach (var entry in _cache.Values)
            {
                if (entry.Texture?.GetWrapOrEmpty() is IDisposable wrap)
                    wrap.Dispose();
            }
            _cache.Clear();
        }
    }
}

