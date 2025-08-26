using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Numerics;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Dalamud.Bindings.ImGui;
using Dalamud.Plugin.Ipc;

namespace DemiCatPlugin;

public class SyncshellWindow : IDisposable
{
    private readonly Config _config;
    private readonly HttpClient _httpClient;
    private readonly List<Asset> _assets = new();
    private readonly HashSet<string> _installed = new();
    private readonly HashSet<string> _seenAssetIds;
    private readonly string _assetsFile;
    private readonly string _installedFile;
    private readonly CancellationTokenSource _cts = new();
    private DateTimeOffset? _lastPullAt;
    private DateTimeOffset _lastRefresh;
    private string? _etag;
    private bool _loading;
    private bool _needsRefresh = true;
    private static DateTimeOffset _lastRedraw;

    public SyncshellWindow(Config config, HttpClient httpClient)
    {
        _config = config;
        _httpClient = httpClient;

        if (!_config.Categories.TryGetValue("syncshell", out var state))
        {
            state = new Config.CategoryState();
            _config.Categories["syncshell"] = state;
        }
        _lastPullAt = state.LastPullAt;
        _seenAssetIds = state.SeenAssets;

        var dir = PluginServices.Instance!.PluginInterface.GetPluginConfigDirectory();
        _assetsFile = Path.Combine(dir, "assets.json");
        _installedFile = Path.Combine(dir, "installed.json");
        LoadCaches();

        _ = PeriodicRefresh();
    }

    public void Draw()
    {
        if (!_loading && (_needsRefresh || DateTimeOffset.UtcNow - _lastRefresh > TimeSpan.FromMinutes(5)))
            _ = Refresh();

        if (_loading)
        {
            ImGui.TextUnformatted("Loading...");
            return;
        }

        var saveSeen = false;
        foreach (var asset in _assets)
        {
            ImGui.PushID(asset.Id);
            ImGui.BeginChild("card", new Vector2(0, 70), true);
            ImGui.TextUnformatted(asset.Name);
            if (!_seenAssetIds.Contains(asset.Id))
            {
                ImGui.SameLine();
                ImGui.TextColored(new Vector4(0.2f, 1f, 0.2f, 1f), "New");
                _seenAssetIds.Add(asset.Id);
                saveSeen = true;
            }
            ImGui.TextUnformatted($"{asset.Kind} - {FormatSize(asset.Size)}");
            ImGui.TextUnformatted($"{asset.Uploader} - {FormatRelativeTime(asset.CreatedAt)}");
            ImGui.EndChild();
            ImGui.PopID();
        }

        if (saveSeen)
            PluginServices.Instance?.PluginInterface.SavePluginConfig(_config);
    }

    private async Task Refresh()
    {
        if (_loading)
            return;

        try
        {
            _loading = true;

            if (!_config.Categories.TryGetValue("syncshell", out var state))
            {
                state = new Config.CategoryState();
                _config.Categories["syncshell"] = state;
            }

            var baseUrl = _config.ApiBaseUrl.TrimEnd('/');
            var url = $"{baseUrl}/fc/{_config.FcChannelId}/assets";
            if (state.LastPullAt.HasValue)
                url += $"?since={Uri.EscapeDataString(state.LastPullAt.Value.ToString("O"))}";

            var req = new HttpRequestMessage(HttpMethod.Get, url);
            if (!string.IsNullOrEmpty(_config.AuthToken))
                req.Headers.Add("X-Api-Key", _config.AuthToken);
            if (!string.IsNullOrEmpty(_etag))
                req.Headers.IfNoneMatch.Add(new EntityTagHeaderValue(_etag));

            var resp = await _httpClient.SendAsync(req);
            if (resp.StatusCode == HttpStatusCode.NotModified)
            {
                state.LastPullAt = DateTimeOffset.UtcNow;
                _lastPullAt = state.LastPullAt;
                _lastRefresh = DateTimeOffset.UtcNow;
                PluginServices.Instance?.PluginInterface.SavePluginConfig(_config);
                _needsRefresh = false;
                return;
            }
            if (!resp.IsSuccessStatusCode)
                return;

            var json = await resp.Content.ReadAsStringAsync();
            var assets = JsonSerializer.Deserialize<List<Asset>>(json, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            });
            if (assets != null)
            {
                MergeAssets(assets);
                _etag = resp.Headers.ETag?.Tag;
                SaveAssetsCache();
                foreach (var a in assets)
                    _ = TryAutoApply(a);
            }

            state.LastPullAt = DateTimeOffset.UtcNow;
            _lastPullAt = state.LastPullAt;
            _lastRefresh = DateTimeOffset.UtcNow;
            PluginServices.Instance?.PluginInterface.SavePluginConfig(_config);
        }
        finally
        {
            _loading = false;
            _needsRefresh = false;
        }
    }

    private void MergeAssets(IEnumerable<Asset> newAssets)
    {
        var map = _assets.ToDictionary(a => a.Id);
        foreach (var asset in newAssets)
            map[asset.Id] = asset;
        _assets.Clear();
        _assets.AddRange(map.Values.OrderByDescending(a => a.CreatedAt));
    }

    private async Task TryAutoApply(Asset asset)
    {
        if (_installed.Contains(asset.Id))
            return;

        if (!_config.AutoApply.TryGetValue(asset.Kind, out var auto) || !auto)
            return;

        await InstallAsset(asset);
    }

    private async Task InstallAsset(Asset asset)
    {
        try
        {
            if (!ApiHelpers.ValidateApiBaseUrl(_config))
                return;

            var baseUrl = _config.ApiBaseUrl.TrimEnd('/');
            var url = asset.DownloadUrl.StartsWith("http", StringComparison.OrdinalIgnoreCase)
                ? asset.DownloadUrl
                : $"{baseUrl}{asset.DownloadUrl}";

            var tmp = Path.GetTempFileName();
            await using (var resp = await _httpClient.GetAsync(url))
            {
                resp.EnsureSuccessStatusCode();
                await using var fs = File.Create(tmp);
                await resp.Content.CopyToAsync(fs);
            }

            await UpdateInstallationStatus(asset.Id, "DOWNLOADED");

            switch (asset.Kind)
            {
                case "PENUMBRA_PACK":
                    await InstallPenumbraPack(tmp, asset);
                    break;
                case "GLAMOURER_DESIGN":
                    var design = await File.ReadAllTextAsync(tmp);
                    using (JsonDocument.Parse(design)) { }
                    ApplyIpc("Glamourer.Design.Apply", design);
                    await UpdateInstallationStatus(asset.Id, "APPLIED");
                    break;
                case "CUSTOMIZE_PROFILE":
                    var profile = await File.ReadAllTextAsync(tmp);
                    using (JsonDocument.Parse(profile)) { }
                    ApplyIpc("Customize.ApplyProfile", profile);
                    await UpdateInstallationStatus(asset.Id, "APPLIED");
                    break;
                case "SIMPLEHEELS_PROFILE":
                    var heels = await File.ReadAllTextAsync(tmp);
                    using (JsonDocument.Parse(heels)) { }
                    ApplyIpc("SimpleHeels.ApplyProfile", heels);
                    await UpdateInstallationStatus(asset.Id, "APPLIED");
                    break;
            }

            _installed.Add(asset.Id);
            SaveInstalledCache();
        }
        catch (Exception ex)
        {
            PluginServices.Instance?.Log.Error(ex, $"Failed to install asset {asset.Id}");
            await UpdateInstallationStatus(asset.Id, "FAILED");
        }
    }

    private async Task InstallPenumbraPack(string path, Asset asset)
    {
        var pi = PluginServices.Instance?.PluginInterface;
        var success = false;
        if (pi != null)
        {
            try
            {
                var import = pi.GetIpcSubscriber<string, bool>("Penumbra.ImportModPack");
                success = import.InvokeFunc(path);
            }
            catch
            {
                success = false;
            }
        }

        if (!success && pi != null)
        {
            try
            {
                var modsDir = pi.GetIpcSubscriber<string>("Penumbra.GetModsDirectory").InvokeFunc();
                var dest = Path.Combine(modsDir, Path.GetFileNameWithoutExtension(path));
                Directory.CreateDirectory(dest);
                ZipFile.ExtractToDirectory(path, dest, true);
                pi.GetIpcSubscriber<object?>("Penumbra.Reload").InvokeAction(null);
                success = true;
            }
            catch
            {
                // ignore
            }
        }

        if (success)
        {
            await UpdateInstallationStatus(asset.Id, "INSTALLED");
            if (DateTimeOffset.UtcNow - _lastRedraw > TimeSpan.FromSeconds(5))
            {
                try
                {
                    pi?.GetIpcSubscriber<object?>("Penumbra.RedrawAll").InvokeAction(null);
                }
                catch
                {
                    // ignore
                }
                _lastRedraw = DateTimeOffset.UtcNow;
            }
            await UpdateInstallationStatus(asset.Id, "APPLIED");
        }
        else
        {
            await UpdateInstallationStatus(asset.Id, "FAILED");
        }
    }

    private void ApplyIpc(string channel, string payload)
    {
        try
        {
            var pi = PluginServices.Instance?.PluginInterface;
            pi?.GetIpcSubscriber<string>(channel).InvokeAction(payload);
        }
        catch (Exception ex)
        {
            PluginServices.Instance?.Log.Error(ex, $"Failed IPC {channel}");
        }
    }

    private async Task UpdateInstallationStatus(string assetId, string status)
    {
        try
        {
            if (string.IsNullOrEmpty(_config.AuthToken) || !ApiHelpers.ValidateApiBaseUrl(_config))
                return;

            var url = $"{_config.ApiBaseUrl.TrimEnd('/')}/users/me/installations";
            var payload = new { assetId, status };
            var request = new HttpRequestMessage(HttpMethod.Post, url)
            {
                Content = new StringContent(JsonSerializer.Serialize(payload), System.Text.Encoding.UTF8, "application/json")
            };
            request.Headers.Add("X-Api-Key", _config.AuthToken);
            await _httpClient.SendAsync(request);
        }
        catch (Exception ex)
        {
            PluginServices.Instance?.Log.Error(ex, "Failed to update installation status");
        }
    }

    private void LoadCaches()
    {
        try
        {
            if (File.Exists(_assetsFile))
            {
                var json = File.ReadAllText(_assetsFile);
                var wrapper = JsonSerializer.Deserialize<AssetsCache>(json);
                if (wrapper != null)
                {
                    _assets.Clear();
                    _assets.AddRange(wrapper.Assets);
                    _etag = wrapper.Etag;
                }
            }

            if (File.Exists(_installedFile))
            {
                var json = File.ReadAllText(_installedFile);
                var wrapper = JsonSerializer.Deserialize<InstalledCache>(json);
                if (wrapper != null)
                {
                    _installed.Clear();
                    foreach (var id in wrapper.Installed)
                        _installed.Add(id);
                }
            }
            else
            {
                SaveInstalledCache();
            }
        }
        catch
        {
            // ignore
        }
    }

    private void SaveAssetsCache()
    {
        try
        {
            var wrapper = new AssetsCache { Etag = _etag, Assets = _assets };
            var json = JsonSerializer.Serialize(wrapper);
            File.WriteAllText(_assetsFile, json);
        }
        catch
        {
            // ignore
        }
    }

    private void SaveInstalledCache()
    {
        try
        {
            var wrapper = new InstalledCache { Installed = _installed };
            var json = JsonSerializer.Serialize(wrapper);
            File.WriteAllText(_installedFile, json);
        }
        catch
        {
            // ignore
        }
    }

    private async Task PeriodicRefresh()
    {
        while (!_cts.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(TimeSpan.FromMinutes(5), _cts.Token);
                if (_cts.IsCancellationRequested)
                    break;
                await Refresh();
            }
            catch (TaskCanceledException)
            {
                break;
            }
        }
    }

    public void Dispose()
    {
        _cts.Cancel();
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
        public string DownloadUrl { get; set; } = string.Empty;
    }

    private class AssetsCache
    {
        public string? Etag { get; set; }
        public List<Asset> Assets { get; set; } = new();
    }

    private class InstalledCache
    {
        public HashSet<string> Installed { get; set; } = new();
    }
}

