using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Numerics;
using System.Text.Json;
using System.Threading.Tasks;
using Dalamud.Bindings.ImGui;

namespace DemiCatPlugin;

public class SyncshellWindow
{
    private readonly Config _config;
    private readonly HttpClient _httpClient;
    private readonly List<Asset> _assets = new();
    private DateTimeOffset? _lastPullAt;
    private bool _loading;

    public SyncshellWindow(Config config, HttpClient httpClient)
    {
        _config = config;
        _httpClient = httpClient;
        _ = Refresh();
    }

    public void Draw()
    {
        if (_loading)
        {
            ImGui.TextUnformatted("Loading...");
            return;
        }

        foreach (var asset in _assets)
        {
            ImGui.PushID(asset.Id);
            ImGui.BeginChild("card", new Vector2(0, 70), true);
            ImGui.TextUnformatted(asset.Name);
            if (_lastPullAt.HasValue && asset.CreatedAt > _lastPullAt.Value)
            {
                ImGui.SameLine();
                ImGui.TextColored(new Vector4(0.2f, 1f, 0.2f, 1f), "New");
            }
            ImGui.TextUnformatted($"{asset.Kind} - {FormatSize(asset.Size)}");
            ImGui.TextUnformatted($"{asset.Uploader} - {FormatRelativeTime(asset.CreatedAt)}");
            ImGui.EndChild();
            ImGui.PopID();
        }
    }

    private async Task Refresh()
    {
        try
        {
            _loading = true;
            if (!_config.Categories.TryGetValue("syncshell", out var state))
            {
                state = new Config.CategoryState();
                _config.Categories["syncshell"] = state;
            }
            _lastPullAt = state.LastPullAt;

            var url = $"{_config.ApiBaseUrl.TrimEnd('/')}/api/assets";
            var req = new HttpRequestMessage(HttpMethod.Get, url);
            if (!string.IsNullOrEmpty(_config.AuthToken))
                req.Headers.Add("X-Api-Key", _config.AuthToken);
            var resp = await _httpClient.SendAsync(req);
            if (!resp.IsSuccessStatusCode)
                return;
            var json = await resp.Content.ReadAsStringAsync();
            var assets = JsonSerializer.Deserialize<List<Asset>>(json, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            });
            if (assets != null)
            {
                _assets.Clear();
                _assets.AddRange(assets);
            }
            state.LastPullAt = DateTimeOffset.UtcNow;
            PluginServices.Instance?.PluginInterface.SavePluginConfig(_config);
        }
        finally
        {
            _loading = false;
        }
    }

    private static string FormatSize(long size)
    {
        string[] suffix = { "B", "KB", "MB", "GB" };
        double len = size;
        var order = 0;
        while (len >= 1024 && order < suffix.Length - 1)
        {
            order++;
            len /= 1024;
        }
        return $"{len:0.#} {suffix[order]}";
    }

    private static string FormatRelativeTime(DateTimeOffset time)
    {
        var span = DateTimeOffset.UtcNow - time;
        if (span.TotalSeconds < 60) return $"{span.TotalSeconds:0}s ago";
        if (span.TotalMinutes < 60) return $"{span.TotalMinutes:0}m ago";
        if (span.TotalHours < 24) return $"{span.TotalHours:0}h ago";
        return $"{span.TotalDays:0}d ago";
    }

    private class Asset
    {
        public string Id { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public string Kind { get; set; } = string.Empty;
        public long Size { get; set; }
        public string Uploader { get; set; } = string.Empty;
        public DateTimeOffset CreatedAt { get; set; }
    }
}

