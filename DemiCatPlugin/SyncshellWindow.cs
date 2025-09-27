using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Numerics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Dalamud.Bindings.ImGui;
using Dalamud.Plugin.Ipc;
using Dalamud.Plugin.Services;
using DemiCatPlugin.SyncShell;

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
    private readonly FileBlobStore _blobStore;
    private readonly Resolver _resolver;
    private readonly SyncClient _syncClient;
    private readonly ProgressOverlay _progressOverlay;
    private readonly ConcurrentQueue<Action> _uiThreadActions = new();
    private readonly Dictionary<string, PeerInventory> _peerInventories = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _inventoryLock = new();
    private CancellationTokenSource? _refreshCts;
    private DateTimeOffset? _lastPullAt;
    private DateTimeOffset _lastRefresh;
    private string? _etag;
    private bool _loading;
    private volatile bool _needsRefresh = true;
    private PenumbraConflict? _penumbraConflict;
    private static DateTimeOffset _lastRedraw;

    private bool _autoSyncAllUsers;
    private bool _manualSyncAllUsers;
    private bool _manualSyncCustom;
    private float _fileSizeLimitMb = 100f;
    private bool _syncPaused;
    private DateTimeOffset? _lastResyncAt;
    private bool _peerSyncEnabled;
    private int _cacheSizeLimitMb;
    private int _installationsRefreshRequested;
    private int _installationsRefreshInProgress;

    public SyncshellWindow(Config config, HttpClient httpClient)
    {
        if (!config.FCSyncShell)
            throw new InvalidOperationException("Syncshell disabled");

        _config = config;
        _httpClient = httpClient;

        var services = PluginServices.Instance;
        _progressOverlay = new ProgressOverlay();
        if (services != null)
            services.ProgressOverlay = _progressOverlay;

        string configDir;
        if (services?.PluginInterface != null)
        {
            configDir = services.PluginInterface.GetPluginConfigDirectory();
            _blobStore = new FileBlobStore();
        }
        else
        {
            configDir = Path.Combine(Path.GetTempPath(), "DemiCat", "config");
            Directory.CreateDirectory(configDir);
            _blobStore = new FileBlobStore(Path.Combine(configDir, ".syncshell", "cache"));
        }

        var tokenManager = TokenManager.Instance ?? throw new InvalidOperationException("Token manager unavailable");
        var log = services?.Log ?? new NullPluginLog();
        _resolver = new Resolver(_blobStore, log, services?.PluginInterface);
        _syncClient = new SyncClient(_config, tokenManager, _resolver, _blobStore);
        _syncClient.TransferProgress += HandleTransferProgress;
        _syncClient.ApplyCompleted += HandleApplyCompleted;
        _syncClient.PeerManifestReceived += HandlePeerManifestReceived;
        _syncClient.PeerDeltaReceived += HandlePeerDeltaReceived;

        _peerSyncEnabled = _config.SyncshellPeerSyncEnabled;
        _cacheSizeLimitMb = Math.Clamp(_config.SyncshellCacheLimitMb, 256, 16384);
        if (_cacheSizeLimitMb != _config.SyncshellCacheLimitMb)
        {
            _config.SyncshellCacheLimitMb = _cacheSizeLimitMb;
            services?.PluginInterface?.SavePluginConfig(_config);
        }

        if (!_config.Categories.TryGetValue("syncshell", out var state))
        {
            state = new Config.CategoryState();
            _config.Categories["syncshell"] = state;
        }
        _lastPullAt = state.LastPullAt;
        _seenAssetIds = state.SeenAssets;
        _syncPaused = state.Paused;
        _lastResyncAt = state.LastResyncAt;

        _assetsFile = Path.Combine(configDir, "assets.json");
        _installedFile = Path.Combine(configDir, "installed.json");
        LoadCaches();

        _ = TrimCacheAsync();

        Instance = this;

        TokenManager.Instance?.RegisterWatcher(HandleTokenLinked, HandleTokenUnlinked);
    }

    public void Draw()
    {
        PumpClientEvents();

        if (!_config.FCSyncShell)
        {
            const string message = "SyncShell is under development";
            var size = ImGui.CalcTextSize(message);
            var avail = ImGui.GetContentRegionAvail();
            ImGui.SetCursorPos(new Vector2((avail.X - size.X) / 2, (avail.Y - size.Y) / 2));
            ImGui.TextUnformatted(message);
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

        ImGui.BeginChild("sync-settings", new Vector2(-1, 170), true);
        ImGui.TextUnformatted("Sync Settings");
        ImGui.Checkbox("Auto Sync to all Connected Users", ref _autoSyncAllUsers);
        ImGui.Checkbox("Manual Sync (All Users)", ref _manualSyncAllUsers);
        ImGui.Checkbox("Manual Sync (Custom)", ref _manualSyncCustom);
        ImGui.SliderFloat("File-size limit (MB)", ref _fileSizeLimitMb, 100f, 5120f, "%.0f MB");
        var peerSync = _peerSyncEnabled;
        if (ImGui.Checkbox("Enable Peer Sync", ref peerSync))
        {
            _peerSyncEnabled = peerSync;
            _config.SyncshellPeerSyncEnabled = peerSync;
            PluginServices.Instance?.PluginInterface?.SavePluginConfig(_config);
            UpdateSyncClientState();
        }

        var cacheLimit = _cacheSizeLimitMb;
        if (ImGui.SliderInt("Cache size limit (MB)", ref cacheLimit, 256, 16384))
        {
            cacheLimit = Math.Clamp(cacheLimit, 256, 16384);
            if (cacheLimit != _cacheSizeLimitMb)
            {
                _cacheSizeLimitMb = cacheLimit;
                _config.SyncshellCacheLimitMb = cacheLimit;
                PluginServices.Instance?.PluginInterface?.SavePluginConfig(_config);
                _ = TrimCacheAsync();
            }
        }
        if (ImGui.Button("Resync All"))
        {
            _ = ResyncAll();
        }
        ImGui.SameLine();
        if (ImGui.Button("Clear Cache"))
        {
            _ = ClearRemoteCache();
        }
        ImGui.SameLine();
        var pauseLabel = _syncPaused ? "Resume Sync" : "Pause Sync";
        if (ImGui.Button(pauseLabel))
        {
            _syncPaused = !_syncPaused;
            if (_config.Categories.TryGetValue("syncshell", out var st))
            {
                st.Paused = _syncPaused;
                PluginServices.Instance?.PluginInterface.SavePluginConfig(_config);
            }
            UpdateSyncClientState();
        }
        ImGui.EndChild();
        ImGui.Separator();

        var saveSeen = false;
        if (_updatesAvailable.Count > 0)
            ImGui.TextColored(new Vector4(1f, 0.8f, 0.2f, 1f), $"{_updatesAvailable.Count} update(s) available");
        foreach (var asset in _assets)
        {
            ImGui.PushID($"{asset.PeerId}:{asset.Id}");
            var childHeight = 70f;
            if (asset.Kind == "BUNDLE" && asset.Items != null)
                childHeight += ImGui.GetTextLineHeightWithSpacing() * asset.Items.Count;
            ImGui.BeginChild("card", new Vector2(-1, childHeight), true);
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

    internal void PumpClientEvents()
    {
        while (_uiThreadActions.TryDequeue(out var action))
        {
            try
            {
                action();
            }
            catch (Exception ex)
            {
                PluginServices.Instance?.Log.Error(ex, "Failed to process SyncShell UI action");
            }
        }

        if (Interlocked.CompareExchange(ref _installationsRefreshRequested, 0, 1) == 1)
        {
            _ = RefreshInstallationsAsync();
        }
    }

    private async Task RefreshInstallationsAsync()
    {
        if (Interlocked.Exchange(ref _installationsRefreshInProgress, 1) == 1)
        {
            Interlocked.Exchange(ref _installationsRefreshRequested, 1);
            return;
        }

        try
        {
            do
            {
                try
                {
                    await FetchInstallations().ConfigureAwait(false);
                    ComputeUpdates();
                }
                catch (Exception ex)
                {
                    PluginServices.Instance?.Log.Warning("Failed to refresh SyncShell installations", ex);
                }
            }
            while (Interlocked.CompareExchange(ref _installationsRefreshRequested, 0, 1) == 1);
        }
        finally
        {
            Interlocked.Exchange(ref _installationsRefreshInProgress, 0);
        }
    }

    private async Task Refresh()
    {
        if (!_config.FCSyncShell || _loading || _syncPaused)
            return;
        if (TokenManager.Instance?.IsReady() != true)
            return;

        try
        {
            _loading = true;

            if (!_config.Categories.TryGetValue("syncshell", out var state))
            {
                state = new Config.CategoryState();
                _config.Categories["syncshell"] = state;
            }

            List<Asset> snapshot;
            lock (_inventoryLock)
            {
                snapshot = _peerInventories.Values
                    .SelectMany(static inv => inv.Assets.Values)
                    .Select(CloneAsset)
                    .ToList();
            }

            _assets.Clear();
            _assets.AddRange(snapshot.OrderByDescending(static a => a.CreatedAt));

            foreach (var asset in _assets)
                _ = TryAutoApply(asset);

            _etag = null;
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

    private void HandlePeerManifestReceived(object? sender, PeerManifestEventArgs e)
    {
        _uiThreadActions.Enqueue(() => ApplyPeerManifest(e));
    }

    private void HandlePeerDeltaReceived(object? sender, PeerDeltaEventArgs e)
    {
        _uiThreadActions.Enqueue(() => ApplyPeerDelta(e));
    }

    private void ApplyPeerManifest(PeerManifestEventArgs e)
    {
        lock (_inventoryLock)
        {
            if (!_peerInventories.TryGetValue(e.PeerId, out var inventory))
            {
                inventory = new PeerInventory();
                _peerInventories[e.PeerId] = inventory;
            }

            inventory.Assets.Clear();
            foreach (var asset in e.Assets)
            {
                var converted = ConvertDiscoveryAsset(asset, e.PeerId, e.Timestamp);
                inventory.Assets[MakeAssetKey(e.PeerId, converted.Id)] = converted;
            }

            inventory.LastUpdated = e.Timestamp;
        }

        Volatile.Write(ref _needsRefresh, true);
    }

    private void ApplyPeerDelta(PeerDeltaEventArgs e)
    {
        lock (_inventoryLock)
        {
            if (!_peerInventories.TryGetValue(e.PeerId, out var inventory))
            {
                inventory = new PeerInventory();
                _peerInventories[e.PeerId] = inventory;
            }

            foreach (var id in e.Removed)
            {
                var key = MakeAssetKey(e.PeerId, id);
                inventory.Assets.Remove(key);
            }

            foreach (var updated in e.Updated)
            {
                var converted = ConvertDiscoveryAsset(updated, e.PeerId, e.Timestamp);
                inventory.Assets[MakeAssetKey(e.PeerId, converted.Id)] = converted;
            }

            inventory.LastUpdated = e.Timestamp;
        }

        Volatile.Write(ref _needsRefresh, true);
    }

    private static Asset ConvertDiscoveryAsset(DiscoveryAsset source, string peerId, DateTimeOffset timestamp)
    {
        var asset = new Asset
        {
            Id = string.IsNullOrWhiteSpace(source.Id) ? Guid.NewGuid().ToString("N") : source.Id,
            Name = string.IsNullOrWhiteSpace(source.Name) ? source.Id ?? "Untitled" : source.Name!,
            Kind = string.IsNullOrWhiteSpace(source.Kind) ? "UNKNOWN" : source.Kind!,
            Size = source.Size,
            Uploader = string.IsNullOrWhiteSpace(source.Uploader) ? peerId : source.Uploader!,
            CreatedAt = source.CreatedAt ?? timestamp,
            UpdatedAt = source.UpdatedAt ?? source.CreatedAt ?? timestamp,
            DownloadUrl = source.DownloadUrl ?? string.Empty,
            Dependencies = source.Dependencies.Where(static d => !string.IsNullOrWhiteSpace(d)).Select(static d => d).ToList(),
            PeerId = peerId,
        };

        if (source.Items.Count > 0)
        {
            asset.Items = source.Items.Select(child => ConvertDiscoveryAsset(child, peerId, timestamp)).ToList();
        }

        return asset;
    }

    private static Asset CloneAsset(Asset asset)
    {
        var clone = new Asset
        {
            Id = asset.Id,
            Name = asset.Name,
            Kind = asset.Kind,
            Size = asset.Size,
            Uploader = asset.Uploader,
            CreatedAt = asset.CreatedAt,
            UpdatedAt = asset.UpdatedAt,
            DownloadUrl = asset.DownloadUrl,
            Dependencies = new List<string>(asset.Dependencies),
            PeerId = asset.PeerId,
        };

        if (asset.Items != null)
        {
            clone.Items = asset.Items.Select(CloneAsset).ToList();
        }

        return clone;
    }

    private static string MakeAssetKey(string peerId, string assetId)
        => string.IsNullOrWhiteSpace(assetId) ? peerId : $"{peerId}::{assetId}";

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
            if (!_config.FCSyncShell)
                return (false, "Sync disabled");
            if (!ApiHelpers.ValidateApiBaseUrl(_config))
                return (false, "Invalid API URL");

            var baseUrl = _config.ApiBaseUrl.TrimEnd('/');
            var url = asset.DownloadUrl.StartsWith("http", StringComparison.OrdinalIgnoreCase)
                ? asset.DownloadUrl
                : $"{baseUrl}{asset.DownloadUrl}";

            var tmp = Path.GetTempFileName();
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            ApiHelpers.AddAuthHeader(req, TokenManager.Instance!);
            using var resp = await _httpClient.SendAsync(req);
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
        if (!_config.FCSyncShell)
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
            if (!TokenManager.Instance!.IsReady() || !ApiHelpers.ValidateApiBaseUrl(_config))
                return;

            var url = $"{_config.ApiBaseUrl.TrimEnd('/')}/api/users/me/installations";
            var payload = new { assetId, status };
            var request = new HttpRequestMessage(HttpMethod.Post, url)
            {
                Content = new StringContent(JsonSerializer.Serialize(payload), System.Text.Encoding.UTF8, "application/json")
            };
            ApiHelpers.AddAuthHeader(request, TokenManager.Instance!);
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
                    {
                        if (inst == null || string.IsNullOrEmpty(inst.AssetId))
                            continue;
                        _installations[inst.AssetId] = inst;
                    }
                }
            }
            else
            {
                SaveInstalledCache();
            }
        }
        catch (Exception ex)
        {
            PluginServices.Instance?.Log.Error(ex, "Failed to load caches");
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
        catch (Exception ex)
        {
            PluginServices.Instance?.Log.Error(ex, "Failed to save assets cache");
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
        catch (Exception ex)
        {
            PluginServices.Instance?.Log.Error(ex, "Failed to save installed cache");
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
        catch (Exception ex)
        {
            PluginServices.Instance?.Log.Error(ex, "Failed to clear caches");
        }

        _assets.Clear();
        _installations.Clear();
        _updatesAvailable.Clear();
        _seenAssetIds.Clear();
        lock (_inventoryLock)
            _peerInventories.Clear();
        _etag = null;
        _needsRefresh = true;
    }

    private async Task PostAsync(string path)
    {
        try
        {
            if (!TokenManager.Instance!.IsReady() || !ApiHelpers.ValidateApiBaseUrl(_config))
                return;
            var url = $"{_config.ApiBaseUrl.TrimEnd('/')}{path}";
            var req = new HttpRequestMessage(HttpMethod.Post, url);
            ApiHelpers.AddAuthHeader(req, TokenManager.Instance!);
            await _httpClient.SendAsync(req);
        }
        catch
        {
            // ignore
        }
    }

    private async Task UploadManifest()
    {
        if (!_peerSyncEnabled)
            return;

        if (TokenManager.Instance?.IsReady() != true)
            return;

        try
        {
            await _syncClient.PushManifestAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            PluginServices.Instance?.Log.Warning("Failed to push SyncShell manifest", ex);
        }
    }

    private async Task ResyncAll()
    {
        ClearCaches();
        await PostAsync("/api/syncshell/resync");
        await UploadManifest();
        _lastResyncAt = DateTimeOffset.UtcNow;
        if (_config.Categories.TryGetValue("syncshell", out var st))
        {
            st.LastResyncAt = _lastResyncAt;
            PluginServices.Instance?.PluginInterface.SavePluginConfig(_config);
        }
    }

    private async Task ClearRemoteCache()
    {
        ClearCaches();
        await PostAsync("/api/syncshell/cache");
    }

    private async Task FetchInstallations()
    {
        try
        {
            if (!TokenManager.Instance!.IsReady() || !ApiHelpers.ValidateApiBaseUrl(_config))
                return;

            var url = $"{_config.ApiBaseUrl.TrimEnd('/')}/api/users/me/installations";
            var req = new HttpRequestMessage(HttpMethod.Get, url);
            ApiHelpers.AddAuthHeader(req, TokenManager.Instance!);
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
            {
                if (inst == null || string.IsNullOrEmpty(inst.AssetId))
                    continue;
                _installations[inst.AssetId] = inst;
            }
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

    private async Task TrimCacheAsync()
    {
        try
        {
            var limitBytes = Math.Max(0, _cacheSizeLimitMb) * 1024L * 1024L;
            await _blobStore.TrimTo(limitBytes).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            PluginServices.Instance?.Log.Warning("Failed to trim SyncShell cache", ex);
        }
    }

    private void HandleTransferProgress(object? sender, TransferProgressEventArgs e)
    {
        _uiThreadActions.Enqueue(() => _progressOverlay.Update(e.PeerId, e.Completed, e.Total));
    }

    private void HandleApplyCompleted(object? sender, ApplyResultEventArgs e)
    {
        if (e.Success)
        {
            Volatile.Write(ref _needsRefresh, true);
            Interlocked.Exchange(ref _installationsRefreshRequested, 1);
        }
        else if (e.Error != null)
        {
            PluginServices.Instance?.Log.Error(e.Error, $"Failed to apply SyncShell manifest from {e.PeerId}");
        }
    }

    private void HandleTokenLinked()
    {
        StartPeriodicRefresh();
        UpdateSyncClientState();
    }

    private void HandleTokenUnlinked()
    {
        _ = StopSyncClientAsync();
        StopPeriodicRefresh();
    }

    private void UpdateSyncClientState()
    {
        if (TokenManager.Instance?.IsReady() == true && _config.FCSyncShell && !_syncPaused && _peerSyncEnabled)
        {
            _syncClient.Start();
            return;
        }

        _ = StopSyncClientAsync();
    }

    private Task StopSyncClientAsync()
        => _syncClient.StopAsync();

    private void StartPeriodicRefresh()
    {
        StopPeriodicRefresh();
        _refreshCts = new CancellationTokenSource();
        _ = PeriodicRefresh(_refreshCts.Token);
    }

    private void StopPeriodicRefresh()
    {
        _refreshCts?.Cancel();
        _refreshCts = null;
    }

    private async Task PeriodicRefresh(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(TimeSpan.FromMinutes(5), token);
                if (token.IsCancellationRequested)
                    break;
                if (_config.FCSyncShell)
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
        StopPeriodicRefresh();
        _syncClient.TransferProgress -= HandleTransferProgress;
        _syncClient.ApplyCompleted -= HandleApplyCompleted;
        _syncClient.PeerManifestReceived -= HandlePeerManifestReceived;
        _syncClient.PeerDeltaReceived -= HandlePeerDeltaReceived;
        try
        {
            _syncClient.StopAsync().GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            PluginServices.Instance?.Log.Warning("Failed to stop SyncShell client", ex);
        }
        _syncClient.Dispose();
        if (PluginServices.Instance?.ProgressOverlay == _progressOverlay)
            PluginServices.Instance.ProgressOverlay = null;
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

    private sealed class PeerInventory
    {
        public Dictionary<string, Asset> Assets { get; } = new(StringComparer.OrdinalIgnoreCase);

        public DateTimeOffset LastUpdated { get; set; }
    }

    private sealed class NullPluginLog : IPluginLog
    {
        public void Verbose(string message) { }
        public void Verbose(string message, Exception exception) { }
        public void Debug(string message) { }
        public void Debug(string message, Exception exception) { }
        public void Info(string message) { }
        public void Info(string message, Exception exception) { }
        public void Warning(string message) { }
        public void Warning(string message, Exception exception) { }
        public void Error(string message) { }
        public void Error(Exception exception, string message) { }
        public void Fatal(string message) { }
        public void Fatal(string message, Exception exception) { }
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
        [JsonPropertyName("peer_id")]
        public string PeerId { get; set; } = string.Empty;
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

    [JsonConverter(typeof(InstallationConverter))]
    private class Installation
    {
        [JsonPropertyName("assetId")]
        public string AssetId { get; set; } = string.Empty;
        [JsonPropertyName("status")]
        public string Status { get; set; } = string.Empty;
        [JsonPropertyName("updatedAt")]
        public DateTimeOffset UpdatedAt { get; set; }
    }

    private sealed class InstallationConverter : JsonConverter<Installation>
    {
        public override Installation? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            if (reader.TokenType == JsonTokenType.Null)
                return null;

            if (reader.TokenType != JsonTokenType.StartObject)
                throw new JsonException();

            var installation = new Installation();

            while (reader.Read())
            {
                if (reader.TokenType == JsonTokenType.EndObject)
                    return installation;

                if (reader.TokenType != JsonTokenType.PropertyName)
                    throw new JsonException();

                var propertyName = reader.GetString();
                if (!reader.Read())
                    throw new JsonException();

                if (PropertyMatches(propertyName, "assetId"))
                {
                    installation.AssetId = ReadStringValue(ref reader);
                    continue;
                }

                if (PropertyMatches(propertyName, "status"))
                {
                    installation.Status = ReadStringValue(ref reader);
                    continue;
                }

                if (PropertyMatches(propertyName, "updatedAt"))
                {
                    installation.UpdatedAt = ReadDateTimeOffset(ref reader);
                    continue;
                }

                reader.Skip();
            }

            throw new JsonException("Invalid installation payload.");
        }

        public override void Write(Utf8JsonWriter writer, Installation value, JsonSerializerOptions options)
        {
            writer.WriteStartObject();
            writer.WriteString("assetId", value.AssetId ?? string.Empty);
            writer.WriteString("status", value.Status ?? string.Empty);
            writer.WriteString("updatedAt", value.UpdatedAt);
            writer.WriteEndObject();
        }

        private static bool PropertyMatches(string? propertyName, string expected)
        {
            if (string.IsNullOrEmpty(propertyName))
                return false;

            var normalized = propertyName.Replace("_", string.Empty);
            return string.Equals(normalized, expected, StringComparison.OrdinalIgnoreCase);
        }

        private static string ReadStringValue(ref Utf8JsonReader reader)
        {
            if (reader.TokenType == JsonTokenType.String)
                return reader.GetString() ?? string.Empty;

            if (reader.TokenType == JsonTokenType.Null)
                return string.Empty;

            reader.Skip();
            return string.Empty;
        }

        private static DateTimeOffset ReadDateTimeOffset(ref Utf8JsonReader reader)
        {
            if (reader.TokenType == JsonTokenType.String)
            {
                if (reader.TryGetDateTimeOffset(out var dto))
                    return dto;

                var raw = reader.GetString();
                if (!string.IsNullOrEmpty(raw) && DateTimeOffset.TryParse(raw, CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed))
                    return parsed;
            }
            else if (reader.TokenType == JsonTokenType.Number)
            {
                if (reader.TryGetInt64(out var seconds))
                    return DateTimeOffset.FromUnixTimeSeconds(seconds);
            }
            else if (reader.TokenType == JsonTokenType.Null)
            {
                return default;
            }
            else
            {
                reader.Skip();
            }

            return default;
        }
    }

    private class InstallationsCache
    {
        [JsonPropertyName("installations")]
        public List<Installation> Installations { get; set; } = new();
    }

}

