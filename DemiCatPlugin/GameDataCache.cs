using System.Text.Json;
using Dalamud.Plugin.Services;
using Dalamud.Interface.Textures;
using Dalamud.Interface.Textures.TextureWraps;

namespace DemiCatPlugin;

internal sealed class GameDataCache : IDisposable
{
    private readonly IDataManager _dataManager;
    private readonly ITextureProvider _textureProvider;
    private readonly HttpClient _httpClient;
    private readonly string _cacheDir;
    private readonly Dictionary<uint, CachedEntry> _items = new();
    private readonly Dictionary<uint, CachedEntry> _duties = new();

    private readonly TimeSpan _maxAge = TimeSpan.FromHours(6);

    public GameDataCache(HttpClient httpClient)
    {
        _dataManager = PluginServices.Instance!.DataManager;
        _textureProvider = PluginServices.Instance!.TextureProvider;
        _httpClient = httpClient;
        _cacheDir = Path.Combine(PluginServices.Instance!.PluginInterface.GetPluginConfigDirectory(), "cached-gamedata");
        Directory.CreateDirectory(_cacheDir);
        Load();
    }

    public async Task<CachedEntry?> GetItem(uint id)
    {
        if (_items.TryGetValue(id, out var entry) && !IsExpired(entry))
            return entry;

        entry = await ResolveItem(id);
        if (entry != null)
        {
            _items[id] = entry;
            Save();
        }
        return entry;
    }

    public async Task<CachedEntry?> GetDuty(uint id)
    {
        if (_duties.TryGetValue(id, out var entry) && !IsExpired(entry))
            return entry;

        entry = await ResolveDuty(id);
        if (entry != null)
        {
            _duties[id] = entry;
            Save();
        }
        return entry;
    }

    private bool IsExpired(CachedEntry entry)
        => DateTime.UtcNow - entry.Timestamp > _maxAge;

    private async Task<CachedEntry?> ResolveItem(uint id)
    {
        try
        {
            var sheet = _dataManager.GetExcelSheet<Lumina.Excel.GeneratedSheets.Item>();
            var row = sheet?.GetRow(id);
            if (row != null)
            {
                var name = row.Name.ExtractText();
                var iconFile = await GetIconFile(row.Icon, id);
                return new CachedEntry(name, iconFile, DateTime.UtcNow);
            }
        }
        catch
        {
            // fall back
        }

        try
        {
            var json = await _httpClient.GetStringAsync($"https://xivapi.com/item/{id}");
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            var name = root.TryGetProperty("Name", out var nEl) ? nEl.GetString() ?? $"Item {id}" : $"Item {id}";
            var iconRel = root.TryGetProperty("Icon", out var iEl) ? iEl.GetString() ?? string.Empty : string.Empty;
            var iconUrl = string.IsNullOrEmpty(iconRel) ? string.Empty : $"https://xivapi.com{iconRel}";
            var iconFile = await DownloadIcon(iconUrl, id);
            return new CachedEntry(name, iconFile, DateTime.UtcNow);
        }
        catch
        {
            return null;
        }
    }

    private async Task<CachedEntry?> ResolveDuty(uint id)
    {
        try
        {
            var sheet = _dataManager.GetExcelSheet<Lumina.Excel.GeneratedSheets.ContentFinderCondition>();
            var row = sheet?.GetRow(id);
            if (row != null)
            {
                var name = row.Name.ExtractText();
                var iconFile = await GetIconFile(row.Icon, id, "duty");
                return new CachedEntry(name, iconFile, DateTime.UtcNow);
            }
        }
        catch
        {
            // fall back
        }

        try
        {
            var json = await _httpClient.GetStringAsync($"https://xivapi.com/duty/{id}");
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            var name = root.TryGetProperty("Name", out var nEl) ? nEl.GetString() ?? $"Duty {id}" : $"Duty {id}";
            var iconRel = root.TryGetProperty("Icon", out var iEl) ? iEl.GetString() ?? string.Empty : string.Empty;
            var iconUrl = string.IsNullOrEmpty(iconRel) ? string.Empty : $"https://xivapi.com{iconRel}";
            var iconFile = await DownloadIcon(iconUrl, id, "duty");
            return new CachedEntry(name, iconFile, DateTime.UtcNow);
        }
        catch
        {
            return null;
        }
    }

    private async Task<string> GetIconFile(uint iconId, uint id, string prefix = "item")
    {
        var filePath = Path.Combine(_cacheDir, $"{prefix}-{id}.png");
        if (!File.Exists(filePath))
        {
            try
            {
                var texture = _textureProvider.GetFromGameIcon(iconId);
                using var icon = await texture.RentAsync();
                await using var s = icon.EncodeToStream(Dalamud.ImageFormat.Png);
                await using var f = File.Create(filePath);
                await s.CopyToAsync(f);
            }
            catch
            {
                // ignore
            }
        }
        return filePath;
    }

    private async Task<string> DownloadIcon(string url, uint id, string prefix = "item")
    {
        var filePath = Path.Combine(_cacheDir, $"{prefix}-{id}.png");
        if (!File.Exists(filePath) && !string.IsNullOrEmpty(url))
        {
            try
            {
                var data = await _httpClient.GetByteArrayAsync(url);
                await File.WriteAllBytesAsync(filePath, data);
            }
            catch
            {
                // ignore
            }
        }
        return filePath;
    }

    private void Load()
    {
        var file = Path.Combine(_cacheDir, "cache.json");
        if (!File.Exists(file))
            return;
        try
        {
            var json = File.ReadAllText(file);
            var wrapper = JsonSerializer.Deserialize<CacheWrapper>(json);
            if (wrapper == null) return;
            foreach (var kv in wrapper.Items)
                _items[kv.Key] = kv.Value;
            foreach (var kv in wrapper.Duties)
                _duties[kv.Key] = kv.Value;
        }
        catch
        {
            // ignore
        }
    }

    private void Save()
    {
        try
        {
            var wrapper = new CacheWrapper { Items = _items, Duties = _duties };
            var json = JsonSerializer.Serialize(wrapper);
            File.WriteAllText(Path.Combine(_cacheDir, "cache.json"), json);
        }
        catch
        {
            // ignore
        }
    }

    public void Dispose()
    {
    }

    internal record CachedEntry(string Name, string IconPath, DateTime Timestamp);
    private class CacheWrapper
    {
        public Dictionary<uint, CachedEntry> Items { get; set; } = new();
        public Dictionary<uint, CachedEntry> Duties { get; set; } = new();
    }
}
