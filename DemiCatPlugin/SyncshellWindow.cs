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
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Dalamud.Bindings.ImGui;
using Dalamud.Plugin.Ipc;

namespace DemiCatPlugin;

public class SyncshellWindow : IDisposable
{
    public static SyncshellWindow? Instance { get; private set; }

    private readonly Config _config;
    private readonly HttpClient _httpClient;
    private readonly List<Asset> _assets = new();
    private readonly Dictionary<string, Installation> _installations = new();
    private readonly HashSet<string> _updatesAvailable = new();
    private readonly HashSet<string> _seenAssetIds;
    private readonly string _assetsFile;
    private readonly string _installedFile;
    private readonly CancellationTokenSource _cts = new();
    private DateTimeOffset? _lastPullAt;
    private DateTimeOffset _lastRefresh;
    private string? _etag;
    private bool _loading;
    private bool _needsRefresh = true;
    private PenumbraConflict? _penumbraConflict;
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

        Instance = this;

        _ = PeriodicRefresh();
    }

    public void Draw()
    {
        if (!_config.SyncEnabled)
        {
            return;
        }

        if (!_loading && (_needsRefresh || DateTimeOffset.UtcNow - _lastRefresh > TimeSpan.FromMinutes(5)))
            _ = Refresh();

        if (_loading)
        {
            ImGui.TextUnformatted("Loading...");
            return;
        }

        if (_penumbraConflict != null)
            ImGui.OpenPopup("Penumbra Conflict");
        var openConflict = true;
        if (_penumbraConflict != null && ImGui.BeginPopupModal("Penumbra Conflict", ref openConflict, ImGuiWindowFlags.AlwaysAutoResize))
        {
            ImGui.TextUnformatted($"Mod {_penumbraConflict.ModName} already exists. Use vault version or keep mine?");
            if (ImGui.Button("Use vault version"))
            {
                _penumbraConflict?.Tcs.TrySetResult(true);
                _penumbraConflict = null;
            }
            ImGui.SameLine();
            if (ImGui.Button("Keep mine"))
            {
                _penumbraConflict?.Tcs.TrySetResult(false);
                _penumbraConflict = null;
            }
            ImGui.EndPopup();
        }
        else if (_penumbraConflict != null && !openConflict)
        {
            _penumbraConflict?.Tcs.TrySetResult(false);
            _penumbraConflict = null;
        }

        var saveSeen = false;
        if (_updatesAvailable.Count > 0)
            ImGui.TextColored(new Vector4(1f, 0.8f, 0.2f, 1f), $"{_updatesAvailable.Count} update(s) available");
        foreach (var asset in _assets)
        {
            ImGui.PushID(asset.Id);
            var childHeight = 70f;
            if (asset.Kind == "BUNDLE" && asset.Items != null)
                childHeight += ImGui.GetTextLineHeightWithSpacing() * asset.Items.Count;
            ImGui.BeginChild("card", new Vector2(0, childHeight), true);
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
            var update = _updatesAvailable.Contains(asset.Id);
            if (update)
                ImGui.TextColored(new Vector4(1f, 0.8f, 0.2f, 1f), "Update available");
            if (asset.Dependencies.Count > 0)
            {
                var missing = asset.Dependencies
                    .Where(d => !_installations.TryGetValue(d, out var inst) || inst.Status != "APPLIED")
                    .ToList();
                if (missing.Count > 0)
                {
                    var names = missing
                        .Select(d => _assets.FirstOrDefault(a => a.Id == d)?.Name ?? d);
                    ImGui.TextColored(new Vector4(1f, 0.6f, 0.6f, 1f), $"Missing dependencies: {string.Join(", ", names)}");
                    if (ImGui.Button("Install all"))
                    {
                        foreach (var depId in missing)
                        {
                            var depAsset = _assets.FirstOrDefault(a => a.Id == depId);
                            if (depAsset != null)
                                _ = InstallAsset(depAsset);
                        }
                    }
                }
            }
            if (asset.Kind == "BUNDLE" && asset.Items != null)
            {
                foreach (var item in asset.Items)
                    ImGui.BulletText($"{item.Name} ({item.Kind})");
                var btn = update ? $"Update bundle ({asset.Items.Count} items)" : $"Install bundle ({asset.Items.Count} items)";
                if (ImGui.Button(btn))
                    _ = InstallBundle(asset);
            }
            else if (update)
            {
                if (ImGui.Button("Update"))
                    _ = InstallAsset(asset);
            }
            ImGui.EndChild();
            ImGui.PopID();
        }

        if (saveSeen)
            PluginServices.Instance?.PluginInterface.SavePluginConfig(_config);
    }

    private async Task Refresh()
    {
        if (!_config.SyncEnabled || _loading)
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
            var url = $"{baseUrl}/api/fc/{_config.FcChannelId}/assets";
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
            var assetsResp = JsonSerializer.Deserialize<AssetResponse>(json, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            });
            if (assetsResp?.Items != null)
            {
                MergeAssets(assetsResp.Items);
                _etag = resp.Headers.ETag?.Tag;
                foreach (var a in assetsResp.Items)
                    _ = TryAutoApply(a);
            }

            var bundles = await FetchBundles(baseUrl, state.LastPullAt);
            if (bundles != null)
                MergeAssets(bundles);

            SaveAssetsCache();

            await FetchInstallations();
            ComputeUpdates();

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

    private async Task<List<Asset>?> FetchBundles(string baseUrl, DateTimeOffset? since)
    {
        if (!ApiHelpers.ValidateApiBaseUrl(_config))
            return null;

        var url = $"{baseUrl}/api/fc/{_config.FcChannelId}/bundles";
        if (since.HasValue)
            url += $"?since={Uri.EscapeDataString(since.Value.ToString("O"))}";

        var req = new HttpRequestMessage(HttpMethod.Get, url);
        if (!string.IsNullOrEmpty(_config.AuthToken))
            req.Headers.Add("X-Api-Key", _config.AuthToken);

        var resp = await _httpClient.SendAsync(req);
        if (!resp.IsSuccessStatusCode)
            return null;

        var json = await resp.Content.ReadAsStringAsync();
        var bundles = JsonSerializer.Deserialize<BundleResponse>(json, new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        });
        if (bundles?.Items == null)
            return null;

        var list = new List<Asset>();
        foreach (var b in bundles.Items)
        {
            list.Add(new Asset
            {
                Id = b.Id,
                Name = b.Name,
                Kind = "BUNDLE",
                CreatedAt = b.UpdatedAt ?? DateTimeOffset.UtcNow,
                UpdatedAt = b.UpdatedAt ?? DateTimeOffset.UtcNow,
                Size = b.Assets.Sum(a => a.Size),
                Items = b.Assets,
                DownloadUrl = string.Empty,
                Uploader = string.Empty
            });
        }
        return list;
    }

    private async Task TryAutoApply(Asset asset)
    {
        if (_installations.ContainsKey(asset.Id))
            return;

        if (!_config.AutoApply.TryGetValue(asset.Kind, out var auto) || !auto)
            return;

        _ = await InstallAsset(asset);
    }

    private async Task InstallBundle(Asset bundle)
    {
        if (bundle.Items == null || bundle.Items.Count == 0)
            return;

        var ordered = SortByDependencies(bundle.Items);
        var errors = new List<string>();
        foreach (var item in ordered)
        {
            var (ok, err) = await InstallAsset(item);
            if (!ok && err != null)
                errors.Add($"{item.Name}: {err}");
        }

        if (errors.Count > 0)
        {
            PluginServices.Instance?.Log.Error($"Bundle {bundle.Name} install errors: \n{string.Join("\n", errors)}");
        }
        else
        {
            PluginServices.Instance?.Log.Information($"Bundle {bundle.Name} installed successfully ({ordered.Count} items)");
        }
    }

    private static List<Asset> SortByDependencies(IEnumerable<Asset> items)
    {
        var map = items.ToDictionary(a => a.Id);
        var visited = new HashSet<string>();
        var result = new List<Asset>();
        void Visit(Asset a)
        {
            if (visited.Contains(a.Id))
                return;
            visited.Add(a.Id);
            foreach (var dep in a.Dependencies)
                if (map.TryGetValue(dep, out var depAsset))
                    Visit(depAsset);
            result.Add(a);
        }

        foreach (var a in items)
            Visit(a);
        return result;
    }

    private async Task<(bool Success, string? Error)> InstallAsset(Asset asset)
    {
        try
        {
            if (!_config.SyncEnabled)
                return (false, "Sync disabled");
            if (!ApiHelpers.ValidateApiBaseUrl(_config))
                return (false, "Invalid API URL");

            var baseUrl = _config.ApiBaseUrl.TrimEnd('/');
            var url = asset.DownloadUrl.StartsWith("http", StringComparison.OrdinalIgnoreCase)
                ? asset.DownloadUrl
                : $"{baseUrl}{asset.DownloadUrl}";

            var tmp = Path.GetTempFileName();
            using var resp = await _httpClient.GetAsync(url);
            resp.EnsureSuccessStatusCode();
            await using var fs = File.Create(tmp);
            await resp.Content.CopyToAsync(fs);

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

            return (true, null);
        }
        catch (Exception ex)
        {
            PluginServices.Instance?.Log.Error(ex, $"Failed to install asset {asset.Id}");
            await UpdateInstallationStatus(asset.Id, "FAILED");
            return (false, ex.Message);
        }
    }

    private async Task InstallPenumbraPack(string path, Asset asset)
    {
        var pi = PluginServices.Instance?.PluginInterface;
        var success = false;
        if (pi != null)
        {
            string? dest = null;
            try
            {
                var modsDir = pi.GetIpcSubscriber<string>("Penumbra.GetModsDirectory").InvokeFunc();
                dest = Path.Combine(modsDir, asset.Name);
                var proceed = await ResolvePenumbraConflict(asset.Name, dest);
                if (!proceed)
                {
                    await UpdateInstallationStatus(asset.Id, "SKIPPED");
                    return;
                }
            }
            catch
            {
                // ignore
            }

            try
            {
                var import = pi.GetIpcSubscriber<string, bool>("Penumbra.ImportModPack");
                success = import.InvokeFunc(path);
            }
            catch
            {
                success = false;
            }

            if (!success && dest != null)
            {
                try
                {
                    Directory.CreateDirectory(dest);
                    ZipFile.ExtractToDirectory(path, dest, true);
                    pi.GetIpcSubscriber<object>("Penumbra.Reload").InvokeAction();
                    success = true;
                }
                catch
                {
                    // ignore
                }
            }
        }

        if (success)
        {
            await UpdateInstallationStatus(asset.Id, "INSTALLED");
            if (DateTimeOffset.UtcNow - _lastRedraw > TimeSpan.FromSeconds(5))
            {
                try
                {
                    pi?.GetIpcSubscriber<object>("Penumbra.RedrawAll").InvokeAction();
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

    private async Task<bool> ResolvePenumbraConflict(string modName, string dest)
    {
        if (!Directory.Exists(dest))
            return true;
        if (_config.PenumbraChoices.TryGetValue(modName, out var useVault))
        {
            if (!useVault)
                return false;
            Directory.Delete(dest, true);
            return true;
        }
        var tcs = new TaskCompletionSource<bool>();
        _penumbraConflict = new PenumbraConflict { ModName = modName, Tcs = tcs };
        var result = await tcs.Task;
        _config.PenumbraChoices[modName] = result;
        PluginServices.Instance?.PluginInterface.SavePluginConfig(_config);
        if (!result)
            return false;
        Directory.Delete(dest, true);
        return true;
    }

    private void ApplyIpc(string channel, string payload)
    {
        if (!_config.SyncEnabled)
            return;

        try
        {
            var pi = PluginServices.Instance?.PluginInterface;
            pi?.GetIpcSubscriber<string, object?>(channel).InvokeAction(payload);
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

            var url = $"{_config.ApiBaseUrl.TrimEnd('/')}/api/users/me/installations";
            var payload = new { assetId, status };
            var request = new HttpRequestMessage(HttpMethod.Post, url)
            {
                Content = new StringContent(JsonSerializer.Serialize(payload), System.Text.Encoding.UTF8, "application/json")
            };
            request.Headers.Add("X-Api-Key", _config.AuthToken);
            await _httpClient.SendAsync(request);

            _installations[assetId] = new Installation { AssetId = assetId, Status = status, UpdatedAt = DateTimeOffset.UtcNow };
            SaveInstalledCache();
            ComputeUpdates();
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
                var wrapper = JsonSerializer.Deserialize<InstallationsCache>(json);
                if (wrapper != null)
                {
                    _installations.Clear();
                    foreach (var inst in wrapper.Installations)
                        _installations[inst.AssetId] = inst;
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

        ComputeUpdates();
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
            var wrapper = new InstallationsCache { Installations = _installations.Values.ToList() };
            var json = JsonSerializer.Serialize(wrapper);
            File.WriteAllText(_installedFile, json);
        }
        catch
        {
            // ignore
        }
    }

    public void ClearCaches()
    {
        try
        {
            if (File.Exists(_assetsFile))
                File.Delete(_assetsFile);
            if (File.Exists(_installedFile))
                File.Delete(_installedFile);
        }
        catch
        {
            // ignore
        }

        _assets.Clear();
        _installations.Clear();
        _updatesAvailable.Clear();
        _seenAssetIds.Clear();
        _etag = null;
        _needsRefresh = true;
    }

    private async Task FetchInstallations()
    {
        try
        {
            if (string.IsNullOrEmpty(_config.AuthToken) || !ApiHelpers.ValidateApiBaseUrl(_config))
                return;

            var url = $"{_config.ApiBaseUrl.TrimEnd('/')}/api/users/me/installations";
            var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.Add("X-Api-Key", _config.AuthToken);
            var resp = await _httpClient.SendAsync(req);
            if (!resp.IsSuccessStatusCode)
                return;

            var json = await resp.Content.ReadAsStringAsync();
            var list = JsonSerializer.Deserialize<List<Installation>>(json, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            });
            if (list == null)
                return;

            _installations.Clear();
            foreach (var inst in list)
                _installations[inst.AssetId] = inst;
            SaveInstalledCache();
        }
        catch
        {
            // ignore
        }
    }

    private void ComputeUpdates()
    {
        _updatesAvailable.Clear();
        foreach (var asset in _assets)
        {
            if (_installations.TryGetValue(asset.Id, out var inst) && asset.UpdatedAt > inst.UpdatedAt)
                _updatesAvailable.Add(asset.Id);
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
                if (_config.SyncEnabled)
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
        if (Instance == this)
            Instance = null;
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

    private class PenumbraConflict
    {
        public string ModName { get; set; } = string.Empty;
        public TaskCompletionSource<bool> Tcs { get; set; } = new();
    }

    private class Asset
    {
        [JsonPropertyName("id")]
        public string Id { get; set; } = string.Empty;
        [JsonPropertyName("name")]
        public string Name { get; set; } = string.Empty;
        [JsonPropertyName("kind")]
        public string Kind { get; set; } = string.Empty;
        [JsonPropertyName("size")]
        public long Size { get; set; }
        [JsonPropertyName("uploader")]
        public string Uploader { get; set; } = string.Empty;
        [JsonPropertyName("created_at")]
        public DateTimeOffset CreatedAt { get; set; }
        [JsonPropertyName("updated_at")]
        public DateTimeOffset UpdatedAt { get; set; }
        [JsonPropertyName("download_url")]
        public string DownloadUrl { get; set; } = string.Empty;
        [JsonPropertyName("dependencies")]
        public List<string> Dependencies { get; set; } = new();
        [JsonPropertyName("items")]
        public List<Asset>? Items { get; set; }
    }

    private class AssetResponse
    {
        [JsonPropertyName("items")]
        public List<Asset> Items { get; set; } = new();
    }

    private class AssetsCache
    {
        [JsonPropertyName("etag")]
        public string? Etag { get; set; }
        [JsonPropertyName("assets")]
        public List<Asset> Assets { get; set; } = new();
    }

    private class Installation
    {
        [JsonPropertyName("asset_id")]
        public string AssetId { get; set; } = string.Empty;
        [JsonPropertyName("status")]
        public string Status { get; set; } = string.Empty;
        [JsonPropertyName("updated_at")]
        public DateTimeOffset UpdatedAt { get; set; }
    }

    private class InstallationsCache
    {
        [JsonPropertyName("installations")]
        public List<Installation> Installations { get; set; } = new();
    }

    private class BundleResponse
    {
        [JsonPropertyName("items")]
        public List<Bundle> Items { get; set; } = new();
    }

    private class Bundle
    {
        [JsonPropertyName("id")]
        public string Id { get; set; } = string.Empty;
        [JsonPropertyName("name")]
        public string Name { get; set; } = string.Empty;
        [JsonPropertyName("description")]
        public string? Description { get; set; }
        [JsonPropertyName("updated_at")]
        public DateTimeOffset? UpdatedAt { get; set; }
        [JsonPropertyName("assets")]
        public List<Asset> Assets { get; set; } = new();
    }
}

