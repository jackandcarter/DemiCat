using System;
using System.Buffers;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
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
using Glamourer.Api.IpcSubscribers;
using Penumbra.Api.Enums;

namespace DemiCatPlugin;

public class SyncshellWindow : IDisposable
{
    public static SyncshellWindow? Instance { get; private set; }

    private static readonly TimeSpan DefaultPairingLifetime = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan PairingRefreshSkew = TimeSpan.FromSeconds(15);
    private static readonly string[] PairingLifetimeProperties = { "expiresIn", "expires_in", "ttl" };

    private readonly Config _config;
    private readonly HttpClient _httpClient;
    private readonly TokenManager _tokenManager;
    private readonly List<Asset> _assets = new();
    private readonly Dictionary<string, Installation> _installations = new();
    private readonly HashSet<string> _updatesAvailable = new();
    private readonly HashSet<string> _seenAssetIds;
    private readonly string _assetsFile;
    private readonly string _installedFile;
    private readonly string _bundlesFile;
    private readonly FileBlobStore _blobStore;
    private readonly Resolver _resolver;
    private readonly SyncClient _syncClient;
    private readonly ProgressOverlay _progressOverlay;
    private readonly Config.CategoryState _syncshellState;
    private readonly ConcurrentQueue<Action> _uiThreadActions = new();
    private readonly Dictionary<string, PeerInventory> _peerInventories = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _inventoryLock = new();
    private readonly SemaphoreSlim _pairingLock = new(1, 1);
    private CancellationTokenSource? _refreshCts;
    private DateTimeOffset? _lastPullAt;
    private DateTimeOffset _lastRefresh;
    private string? _etag;
    private string? _bundleEtag;
    private bool _loading;
    private volatile bool _needsRefresh = true;
    private PenumbraConflict? _penumbraConflict;
    private static DateTimeOffset _lastRedraw;
    private DateTimeOffset? _pairingExpiresAt;
    private TimeSpan _pairingLifetime = DefaultPairingLifetime;

    private bool _autoSyncAllUsers;
    private bool _manualSyncAllUsers;
    private bool _manualSyncCustom;
    private readonly List<Action> _ipcUnsubscribers = new();
    private readonly SemaphoreSlim _manifestPushLock = new(1, 1);
    private readonly object _manualSyncStateLock = new();
    private int _autoSyncPendingRequests;
    private int _manualSyncPendingFlag;
    private LocalStateChangeSource? _lastManualChangeSource;
    private DateTimeOffset? _lastManualChangeAt;
    private bool _disposed;
    private float _fileSizeLimitMb = 100f;
    private long _sessionBytesDownloaded;
    private long _sessionBytesReserved;
    private readonly object _budgetLock = new();
    private readonly Dictionary<string, string> _budgetReasons = new(StringComparer.OrdinalIgnoreCase);
    private readonly Queue<PendingDownload> _budgetQueue = new();
    private readonly Dictionary<string, bool> _budgetAutoResume = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _budgetQueueKeys = new(StringComparer.OrdinalIgnoreCase);
    private string? _budgetStatusMessage;
    private bool _syncPaused;
    private readonly Dictionary<string, CancellationTokenSource> _activeDownloads = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _downloadLock = new();
    private DateTimeOffset? _lastResyncAt;
    private bool _peerSyncEnabled;
    private int _cacheSizeLimitMb;
    private int _installationsRefreshRequested;
    private int _installationsRefreshInProgress;
    private readonly Dictionary<string, BundleCacheEntry> _bundleCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _bundleCacheLock = new();
    private readonly SemaphoreSlim _bundleFetchLock = new(1, 1);

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

        _tokenManager = TokenManager.Instance ?? throw new InvalidOperationException("Token manager unavailable");
        var log = services?.Log ?? new NullPluginLog();
        _resolver = new Resolver(_blobStore, log, services?.PluginInterface);
        _syncClient = new SyncClient(_config, _tokenManager, _resolver, _blobStore);
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
        _syncshellState = state;
        _lastPullAt = state.LastPullAt;
        _seenAssetIds = state.SeenAssets;
        _syncPaused = state.Paused;
        _lastResyncAt = state.LastResyncAt;
        _pairingExpiresAt = state.PairingExpiresAt;

        _autoSyncAllUsers = _config.SyncshellAutoSyncAllUsers;
        _manualSyncAllUsers = _config.SyncshellManualSyncAllUsers;
        _manualSyncCustom = _config.SyncshellManualSyncCustom;
        EnforceSyncPreferenceInvariant(saveIfChanged: true);

        _assetsFile = Path.Combine(configDir, "assets.json");
        _installedFile = Path.Combine(configDir, "installed.json");
        _bundlesFile = Path.Combine(configDir, "bundles.json");
        LoadCaches();

        _ = TrimCacheAsync();

        Instance = this;

        SubscribeToStateChanges();
        _tokenManager.RegisterWatcher(HandleTokenLinked, HandleTokenUnlinked);
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
        var autoSync = _autoSyncAllUsers;
        if (ImGui.Checkbox("Auto Sync to all Connected Users", ref autoSync))
        {
            SetSyncPreferences(autoSync, _manualSyncAllUsers, _manualSyncCustom);
        }

        var manualAll = _manualSyncAllUsers;
        if (ImGui.Checkbox("Manual Sync (All Users)", ref manualAll))
        {
            SetSyncPreferences(manualAll ? false : _autoSyncAllUsers, manualAll, _manualSyncCustom);
        }

        var manualCustom = _manualSyncCustom;
        if (ImGui.Checkbox("Manual Sync (Custom)", ref manualCustom))
        {
            SetSyncPreferences(manualCustom ? false : _autoSyncAllUsers, _manualSyncAllUsers, manualCustom);
        }

        var manualModeActive = IsManualModeActive;
        ImGui.BeginDisabled(!manualModeActive);
        if (ImGui.Button("Sync now"))
        {
            _ = RunManualSyncAsync();
        }
        ImGui.EndDisabled();
        if (!manualModeActive && ImGui.IsItemHovered())
            ImGui.SetTooltip("Enable a manual sync mode to push pending changes.");

        if (ManualSyncPending)
        {
            ImGui.SameLine();
            ImGui.TextColored(new Vector4(1f, 0.8f, 0.2f, 1f), GetManualPendingLabel());
        }
        var limitChanged = ImGui.SliderFloat("File-size limit (MB)", ref _fileSizeLimitMb, 100f, 5120f, "%.0f MB");
        long sessionBytes;
        long reservedBytes;
        long limitBytes;
        string? budgetStatus;
        lock (_budgetLock)
        {
            sessionBytes = _sessionBytesDownloaded;
            reservedBytes = _sessionBytesReserved;
            limitBytes = GetFileSizeLimitBytes();
            budgetStatus = _budgetStatusMessage;
        }

        var usageLabel = reservedBytes > 0
            ? $"Session usage: {FormatSize(sessionBytes)} / {FormatSize(limitBytes)} ({FormatSize(reservedBytes)} pending)"
            : $"Session usage: {FormatSize(sessionBytes)} / {FormatSize(limitBytes)}";
        ImGui.TextUnformatted(usageLabel);
        if (!string.IsNullOrEmpty(budgetStatus))
        {
            ImGui.TextColored(new Vector4(1f, 0.6f, 0.6f, 1f), budgetStatus);
        }

        if (limitChanged)
        {
            OnFileSizeLimitChanged();
        }
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
            SetSyncPaused(!_syncPaused);
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
            if (TryGetBudgetReason(asset.Id, out var budgetReason))
            {
                ImGui.TextColored(new Vector4(1f, 0.6f, 0.6f, 1f), budgetReason);
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

    private bool IsManualModeActive
        => !_autoSyncAllUsers && (_manualSyncAllUsers || _manualSyncCustom);

    private bool ManualSyncPending
        => Volatile.Read(ref _manualSyncPendingFlag) == 1;

    private void SetSyncPreferences(bool auto, bool manualAll, bool manualCustom)
    {
        var previousAuto = _autoSyncAllUsers;
        var previousManualAll = _manualSyncAllUsers;
        var previousManualCustom = _manualSyncCustom;
        var hadPending = ManualSyncPending;

        _autoSyncAllUsers = auto;
        _manualSyncAllUsers = manualAll;
        _manualSyncCustom = manualCustom;

        var changedByInvariant = EnforceSyncPreferenceInvariant(saveIfChanged: false);
        if (changedByInvariant ||
            previousAuto != _autoSyncAllUsers ||
            previousManualAll != _manualSyncAllUsers ||
            previousManualCustom != _manualSyncCustom)
        {
            SaveSyncPreferences();
        }

        if (_autoSyncAllUsers && !previousAuto && hadPending)
        {
            ScheduleManifestPush();
        }
    }

    private bool EnforceSyncPreferenceInvariant(bool saveIfChanged)
    {
        var initialAuto = _autoSyncAllUsers;
        var initialManualAll = _manualSyncAllUsers;
        var initialManualCustom = _manualSyncCustom;

        if (_autoSyncAllUsers)
        {
            _manualSyncAllUsers = false;
            _manualSyncCustom = false;
        }
        else if (!_manualSyncAllUsers && !_manualSyncCustom)
        {
            _autoSyncAllUsers = true;
        }

        var changed = initialAuto != _autoSyncAllUsers ||
                      initialManualAll != _manualSyncAllUsers ||
                      initialManualCustom != _manualSyncCustom;

        if (changed && saveIfChanged)
        {
            SaveSyncPreferences();
        }

        return changed;
    }

    private void SaveSyncPreferences()
    {
        _config.SyncshellAutoSyncAllUsers = _autoSyncAllUsers;
        _config.SyncshellManualSyncAllUsers = _manualSyncAllUsers;
        _config.SyncshellManualSyncCustom = _manualSyncCustom;
        PluginServices.Instance?.PluginInterface?.SavePluginConfig(_config);
    }

    private string GetManualPendingLabel()
    {
        LocalStateChangeSource? source;
        DateTimeOffset? changedAt;
        lock (_manualSyncStateLock)
        {
            source = _lastManualChangeSource;
            changedAt = _lastManualChangeAt;
        }

        var parts = new List<string>();
        if (source.HasValue)
        {
            parts.Add(source.Value switch
            {
                LocalStateChangeSource.Penumbra => "Penumbra",
                LocalStateChangeSource.Glamourer => "Glamourer",
                _ => source.Value.ToString() ?? string.Empty,
            });
        }

        if (changedAt.HasValue)
        {
            parts.Add(FormatRelativeTime(changedAt.Value));
        }

        return parts.Count switch
        {
            0 => "Pending changes",
            _ => $"Pending changes ({string.Join(" • ", parts)})",
        };
    }

    private Task RunManualSyncAsync()
    {
        if (_disposed || !IsManualModeActive)
        {
            return Task.CompletedTask;
        }

        return Task.Run(async () =>
        {
            var success = await PushManifestWithLockAsync().ConfigureAwait(false);
            if (success)
            {
                ClearManualPending();
                _uiThreadActions.Enqueue(() => PluginServices.Instance?.ToastGui.ShowNormal("SyncShell manifest uploaded."));
            }
            else
            {
                _uiThreadActions.Enqueue(() => PluginServices.Instance?.ToastGui.ShowError("SyncShell manual sync failed – check logs."));
            }
        });
    }

    private void HandleLocalStateChanged(LocalStateChangeSource source)
    {
        if (_disposed || !_config.FCSyncShell)
        {
            return;
        }

        if (_autoSyncAllUsers && !_syncPaused && _peerSyncEnabled)
        {
            ScheduleManifestPush();
            return;
        }

        if (IsManualModeActive)
        {
            MarkManualPending(source);
        }
        else if (_autoSyncAllUsers)
        {
            MarkManualPending(source);
        }
    }

    private void MarkManualPending(LocalStateChangeSource source)
    {
        Interlocked.Exchange(ref _manualSyncPendingFlag, 1);
        lock (_manualSyncStateLock)
        {
            _lastManualChangeSource = source;
            _lastManualChangeAt = DateTimeOffset.UtcNow;
        }
    }

    private void ClearManualPending()
    {
        Interlocked.Exchange(ref _manualSyncPendingFlag, 0);
        lock (_manualSyncStateLock)
        {
            _lastManualChangeSource = null;
            _lastManualChangeAt = null;
        }
    }

    private void ScheduleManifestPush()
    {
        if (_disposed)
        {
            return;
        }

        if (Interlocked.Increment(ref _autoSyncPendingRequests) > 1)
        {
            return;
        }

        Task.Run(async () =>
        {
            try
            {
                while (true)
                {
                    var success = await PushManifestWithLockAsync().ConfigureAwait(false);
                    if (success && !IsManualModeActive && ManualSyncPending)
                    {
                        ClearManualPending();
                    }

                    if (Interlocked.Decrement(ref _autoSyncPendingRequests) <= 0)
                    {
                        break;
                    }
                }
            }
            catch (Exception ex)
            {
                PluginServices.Instance?.Log.Warning(ex, "Failed to push SyncShell manifest automatically");
                Interlocked.Exchange(ref _autoSyncPendingRequests, 0);
            }
        });
    }

    private async Task<bool> PushManifestWithLockAsync()
    {
        await _manifestPushLock.WaitAsync().ConfigureAwait(false);
        try
        {
            return await UploadManifestAsync().ConfigureAwait(false);
        }
        finally
        {
            _manifestPushLock.Release();
        }
    }

    private void SubscribeToStateChanges()
    {
        var pi = PluginServices.Instance?.PluginInterface;
        if (pi == null)
        {
            return;
        }

        TrySubscribe(() =>
        {
            var subscriber = pi.GetIpcSubscriber<ModSettingChange, Guid, string, bool, object?>(Penumbra.Api.IpcSubscribers.ModSettings.ModSettingChanged.Label);
            void Handler(ModSettingChange _, Guid __, string ___, bool ____) => HandleLocalStateChanged(LocalStateChangeSource.Penumbra);
            subscriber.Subscribe(Handler);
            _ipcUnsubscribers.Add(() =>
            {
                try
                {
                    subscriber.Unsubscribe(Handler);
                }
                catch (Exception ex)
                {
                    PluginServices.Instance?.Log.Debug(ex, "Failed to unsubscribe from Penumbra.ModSettingChanged");
                }
            });
        }, "Penumbra.ModSettingChanged");

        TrySubscribe(() =>
        {
            var subscriber = pi.GetIpcSubscriber<bool, object?>(Penumbra.Api.IpcSubscribers.PluginState.EnabledChange.Label);
            void Handler(bool _) => HandleLocalStateChanged(LocalStateChangeSource.Penumbra);
            subscriber.Subscribe(Handler);
            _ipcUnsubscribers.Add(() =>
            {
                try
                {
                    subscriber.Unsubscribe(Handler);
                }
                catch (Exception ex)
                {
                    PluginServices.Instance?.Log.Debug(ex, "Failed to unsubscribe from Penumbra.EnabledChange");
                }
            });
        }, "Penumbra.EnabledChange");

        TrySubscribe(() =>
        {
            var subscriber = pi.GetIpcSubscriber<nint, object?>(State.StateChanged.Label);
            void Handler(nint _) => HandleLocalStateChanged(LocalStateChangeSource.Glamourer);
            subscriber.Subscribe(Handler);
            _ipcUnsubscribers.Add(() =>
            {
                try
                {
                    subscriber.Unsubscribe(Handler);
                }
                catch (Exception ex)
                {
                    PluginServices.Instance?.Log.Debug(ex, "Failed to unsubscribe from Glamourer.StateChanged");
                }
            });
        }, "Glamourer.StateChanged");
    }

    private void TrySubscribe(Action subscribe, string description)
    {
        try
        {
            subscribe();
        }
        catch (Exception ex)
        {
            PluginServices.Instance?.Log.Debug(ex, $"Failed to subscribe to {description} IPC event.");
        }
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
        if (!_tokenManager.IsReady())
            return;

        try
        {
            _loading = true;

            var state = _syncshellState;

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
        if (!await EnsureBundleItemsAsync(bundle).ConfigureAwait(false))
            return;

        if (bundle.Items == null || bundle.Items.Count == 0)
            return;

        if (!HasBudgetForBundle(bundle, out var bundleReason))
        {
            QueueBudgetDeferred(bundle, true, bundleReason ?? $"Deferred bundle {bundle.Name} due to download limit.");
            return;
        }

        ClearBudgetReason(bundle.Id);

        var ordered = SortByDependencies(bundle.Items);
        var errors = new List<string>();
        foreach (var item in ordered)
        {
            var (ok, err) = await InstallAsset(item);
            if (!ok && err != null)
            {
                if (TryGetBudgetReason(item.Id, out _))
                    continue;
                errors.Add($"{item.Name}: {err}");
            }
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
        if (!_config.FCSyncShell)
            return (false, "Sync disabled");
        if (!ApiHelpers.ValidateApiBaseUrl(_config))
            return (false, "Invalid API URL");
        if (_syncPaused)
        {
            var pausedMessage = GetSyncPausedReason(asset);
            QueueBudgetDeferred(asset, false, pausedMessage, autoResume: false);
            return (false, pausedMessage);
        }

        var reservedBytes = GetAssetSize(asset);
        if (!TryReserveBudget(asset, reservedBytes, out var budgetReason))
        {
            var message = budgetReason ?? $"Deferred {asset.Name} due to download limit.";
            QueueBudgetDeferred(asset, false, message);
            return (false, message);
        }

        var reservation = reservedBytes;
        var committed = false;
        CancellationTokenSource? downloadCts = null;
        string? tmp = null;

        try
        {
            if (!TryRegisterDownload(asset, out downloadCts, out var downloadToken))
            {
                var pausedMessage = GetSyncPausedReason(asset);
                ReleaseReservedBytes(reservation, processQueue: false);
                QueueBudgetDeferred(asset, false, pausedMessage, autoResume: false);
                return (false, pausedMessage);
            }

            var baseUrl = _config.ApiBaseUrl.TrimEnd('/');
            var url = asset.DownloadUrl.StartsWith("http", StringComparison.OrdinalIgnoreCase)
                ? asset.DownloadUrl
                : $"{baseUrl}{asset.DownloadUrl}";

            tmp = Path.GetTempFileName();
            var response = await SendWithPairingRetryAsync(() =>
            {
                var request = new HttpRequestMessage(HttpMethod.Get, url);
                ApiHelpers.AddAuthHeader(request, _tokenManager);
                return request;
            }, HttpCompletionOption.ResponseHeadersRead, downloadToken).ConfigureAwait(false);

            if (response == null)
            {
                throw new HttpRequestException("SyncShell pairing is unavailable.");
            }

            using var resp = response;
            if (resp.StatusCode == HttpStatusCode.Unauthorized)
            {
                throw new HttpRequestException("Unauthorized", null, resp.StatusCode);
            }

            resp.EnsureSuccessStatusCode();
            await using var fs = File.Create(tmp);
            await using var stream = await resp.Content.ReadAsStreamAsync(downloadToken).ConfigureAwait(false);

            var buffer = ArrayPool<byte>.Shared.Rent(81920);
            var totalRead = 0L;
            var expectedTotal = resp.Content.Headers.ContentLength;
            ReportDownloadProgress(asset.PeerId, totalRead, expectedTotal);
            try
            {
                while (true)
                {
                    var read = await stream.ReadAsync(buffer.AsMemory(0, buffer.Length), downloadToken).ConfigureAwait(false);
                    if (read == 0)
                    {
                        break;
                    }

                    totalRead += read;

                    if (!TryEnsureDownloadBudget(asset, totalRead, ref reservation, out var throttleReason))
                    {
                        throw new DownloadDeferredException(throttleReason ?? $"Paused {asset.Name} due to session limits.");
                    }

                    await fs.WriteAsync(buffer.AsMemory(0, read), downloadToken).ConfigureAwait(false);
                    ReportDownloadProgress(asset.PeerId, totalRead, expectedTotal);
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }

            reservation = Math.Max(reservation, totalRead);
            var finalTotal = expectedTotal.HasValue
                ? Math.Max(expectedTotal.Value, totalRead)
                : totalRead;
            ReportDownloadProgress(asset.PeerId, totalRead, finalTotal);

            CommitReservedBytes(reservation);
            committed = true;

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
        catch (OperationCanceledException) when (downloadCts?.IsCancellationRequested ?? false)
        {
            ReleaseReservedBytes(reservation, processQueue: false);
            if (_syncPaused)
            {
                var pausedMessage = GetSyncPausedReason(asset);
                QueueBudgetDeferred(asset, false, pausedMessage, autoResume: false);
                return (false, pausedMessage);
            }

            const string canceledMessage = "Download canceled.";
            return (false, canceledMessage);
        }
        catch (DownloadDeferredException ex)
        {
            ReleaseReservedBytes(reservation);
            QueueBudgetDeferred(asset, false, ex.Reason, autoResume: false);
            return (false, ex.Reason);
        }
        catch (Exception ex)
        {
            if (!committed)
                ReleaseReservedBytes(reservation);
            PluginServices.Instance?.Log.Error(ex, $"Failed to install asset {asset.Id}");
            await UpdateInstallationStatus(asset.Id, "FAILED");
            return (false, ex.Message);
        }
        finally
        {
            UnregisterDownload(asset.Id, downloadCts);
            if (!committed && !string.IsNullOrEmpty(tmp))
            {
                try
                {
                    File.Delete(tmp);
                }
                catch
                {
                    // ignored
                }
            }
        }
    }

    private bool TryRegisterDownload(Asset asset, out CancellationTokenSource? cts, out CancellationToken token)
    {
        lock (_downloadLock)
        {
            if (_syncPaused)
            {
                cts = null;
                token = default;
                return false;
            }

            cts = new CancellationTokenSource();
            _activeDownloads[asset.Id] = cts;
            token = cts.Token;
            return true;
        }
    }

    private void UnregisterDownload(string assetId, CancellationTokenSource? cts)
    {
        CancellationTokenSource? toDispose = null;
        lock (_downloadLock)
        {
            if (cts != null && _activeDownloads.TryGetValue(assetId, out var existing) && ReferenceEquals(existing, cts))
            {
                _activeDownloads.Remove(assetId);
                toDispose = existing;
            }
        }

        toDispose?.Dispose();
    }

    private void ReportDownloadProgress(string peerId, long downloaded, long? total)
    {
        if (string.IsNullOrWhiteSpace(peerId))
        {
            return;
        }

        var downloadedInt = ClampToInt(downloaded);
        var totalInt = total.HasValue ? ClampToInt(total.Value) : 0;
        var hasTotal = total.HasValue;

        _uiThreadActions.Enqueue(() => _progressOverlay.Update(peerId, downloadedInt, hasTotal ? totalInt : 0));
    }

    private static int ClampToInt(long value)
        => value <= 0 ? 0 : value >= int.MaxValue ? int.MaxValue : (int)value;

    private static string GetSyncPausedReason(Asset asset)
        => $"Paused {asset.Name}. Resume sync to continue downloading.";

    private static long GetAssetSize(Asset asset)
        => asset.Size < 0 ? 0 : asset.Size;

    private bool TryReserveBudget(Asset asset, long bytes, out string? reason)
    {
        if (bytes <= 0)
        {
            ClearBudgetReason(asset.Id);
            reason = null;
            return true;
        }

        lock (_budgetLock)
        {
            var limit = GetFileSizeLimitBytes();
            if (bytes > limit)
            {
                reason = $"{asset.Name} is {FormatSize(bytes)}, exceeding the {FormatSize(limit)} download cap. Increase the file-size limit to download it.";
                return false;
            }

            var used = _sessionBytesDownloaded + _sessionBytesReserved;
            if (used + bytes > limit)
            {
                var remaining = Math.Max(0, limit - used);
                var limitLabel = FormatSize(limit);
                var usedLabel = FormatSize(used);
                reason = remaining == 0
                    ? $"Deferred {asset.Name}: download budget reached ({usedLabel} of {limitLabel} used). Increase the file-size limit to resume."
                    : $"Deferred {asset.Name}: requires {FormatSize(bytes)} but only {FormatSize(remaining)} remains in the session budget. Increase the file-size limit to continue.";
                return false;
            }

            _sessionBytesReserved += bytes;
            _budgetReasons.Remove(asset.Id);
            UpdateBudgetStatusMessageLocked();
        }

        reason = null;
        return true;
    }

    private bool TryEnsureDownloadBudget(Asset asset, long downloadedBytes, ref long reservation, out string? reason)
    {
        lock (_budgetLock)
        {
            var limit = GetFileSizeLimitBytes();
            var usedWithoutCurrent = _sessionBytesDownloaded + _sessionBytesReserved - reservation;
            var required = Math.Max(reservation, downloadedBytes);

            if (limit > 0 && usedWithoutCurrent + required > limit)
            {
                var remaining = Math.Max(0, limit - usedWithoutCurrent);
                var limitLabel = FormatSize(limit);
                var usedWithCurrent = Math.Max(0, Math.Min(limit, usedWithoutCurrent + required));
                var usedLabel = FormatSize(usedWithCurrent);
                reason = remaining == 0
                    ? $"Paused {asset.Name}: download budget reached ({usedLabel} of {limitLabel} used). Increase the file-size limit to resume."
                    : $"Paused {asset.Name}: requires {FormatSize(required)} but only {FormatSize(remaining)} remains in the session budget. Increase the file-size limit to continue.";
                return false;
            }

            if (required > reservation)
            {
                _sessionBytesReserved += required - reservation;
                reservation = required;
                UpdateBudgetStatusMessageLocked();
            }
        }

        reason = null;
        return true;
    }

    private void QueueBudgetDeferred(Asset asset, bool isBundle, string reason, bool autoResume = true)
    {
        var key = MakeBudgetQueueKey(asset.Id, isBundle);
        lock (_budgetLock)
        {
            _budgetReasons[asset.Id] = reason;
            _budgetAutoResume[key] = autoResume;
            if (_budgetQueueKeys.Add(key))
                _budgetQueue.Enqueue(new PendingDownload(key, asset, isBundle, autoResume));
            _budgetStatusMessage = reason;
        }
    }

    private void ClearBudgetReason(string assetId)
    {
        lock (_budgetLock)
        {
            if (_budgetReasons.Remove(assetId))
                UpdateBudgetStatusMessageLocked();
        }
    }

    private bool TryGetBudgetReason(string assetId, out string reason)
    {
        lock (_budgetLock)
        {
            return _budgetReasons.TryGetValue(assetId, out reason);
        }
    }

    private void CommitReservedBytes(long bytes)
    {
        if (bytes <= 0)
            return;

        lock (_budgetLock)
        {
            _sessionBytesReserved = Math.Max(0, _sessionBytesReserved - bytes);
            _sessionBytesDownloaded += bytes;
            UpdateBudgetStatusMessageLocked();
        }
    }

    private void ReleaseReservedBytes(long bytes, bool processQueue = true)
    {
        if (bytes <= 0)
            return;

        var shouldProcess = false;
        lock (_budgetLock)
        {
            _sessionBytesReserved = Math.Max(0, _sessionBytesReserved - bytes);
            UpdateBudgetStatusMessageLocked();
            shouldProcess = _budgetQueue.Count > 0;
        }

        if (processQueue && shouldProcess)
            ProcessBudgetQueue();
    }

    private bool HasBudgetForBundle(Asset bundle, out string? reason)
    {
        var bundleSize = GetAssetSize(bundle);
        if (bundleSize <= 0 && bundle.Items != null)
            bundleSize = bundle.Items.Sum(GetAssetSize);

        if (bundleSize <= 0)
        {
            reason = null;
            return true;
        }

        lock (_budgetLock)
        {
            var limit = GetFileSizeLimitBytes();
            if (bundleSize > limit)
            {
                reason = $"Bundle {bundle.Name} is {FormatSize(bundleSize)}, exceeding the {FormatSize(limit)} download cap. Increase the file-size limit to download it.";
                return false;
            }

            var used = _sessionBytesDownloaded + _sessionBytesReserved;
            if (used + bundleSize > limit)
            {
                var remaining = Math.Max(0, limit - used);
                var limitLabel = FormatSize(limit);
                var usedLabel = FormatSize(used);
                reason = remaining == 0
                    ? $"Deferred bundle {bundle.Name}: download budget reached ({usedLabel} of {limitLabel} used). Increase the file-size limit to resume."
                    : $"Deferred bundle {bundle.Name}: requires {FormatSize(bundleSize)} but only {FormatSize(remaining)} remains in the session budget. Increase the file-size limit to continue.";
                return false;
            }
        }

        reason = null;
        return true;
    }

    private long GetFileSizeLimitBytes()
        => (long)Math.Round(Math.Max(0f, _fileSizeLimitMb) * 1024f * 1024f);

    private static string MakeBudgetQueueKey(string assetId, bool isBundle)
        => isBundle ? $"bundle::{assetId}" : assetId;

    private void OnFileSizeLimitChanged()
    {
        lock (_budgetLock)
        {
            UpdateBudgetStatusMessageLocked();
        }
        ProcessBudgetQueue(force: true);
    }

    private void ProcessBudgetQueue(bool force = false)
    {
        PendingDownload[] pending;
        Dictionary<string, bool> autoResumeStates;
        lock (_budgetLock)
        {
            if (_budgetQueue.Count == 0)
            {
                UpdateBudgetStatusMessageLocked();
                return;
            }

            pending = _budgetQueue.ToArray();
            _budgetQueue.Clear();
            _budgetQueueKeys.Clear();
            autoResumeStates = new Dictionary<string, bool>(_budgetAutoResume);
            _budgetAutoResume.Clear();
            UpdateBudgetStatusMessageLocked();
        }

        List<PendingDownload>? deferred = null;
        foreach (var item in pending)
        {
            var autoResume = autoResumeStates.TryGetValue(item.Key, out var value) ? value : item.AutoResume;
            if (!force && !autoResume)
            {
                deferred ??= new List<PendingDownload>();
                deferred.Add(new PendingDownload(item.Key, item.Asset, item.IsBundle, autoResume));
                continue;
            }

            if (item.IsBundle)
                _ = InstallBundle(item.Asset);
            else
                _ = InstallAsset(item.Asset);
        }

        if (deferred is { Count: > 0 })
        {
            lock (_budgetLock)
            {
                foreach (var item in deferred)
                {
                    if (_budgetQueueKeys.Add(item.Key))
                    {
                        _budgetQueue.Enqueue(new PendingDownload(item.Key, item.Asset, item.IsBundle, item.AutoResume));
                    }

                    _budgetAutoResume[item.Key] = item.AutoResume;
                }

                UpdateBudgetStatusMessageLocked();
            }
        }
    }

    private void CancelActiveDownloads()
    {
        CancellationTokenSource[] tokens;
        lock (_downloadLock)
        {
            if (_activeDownloads.Count == 0)
            {
                return;
            }

            tokens = _activeDownloads.Values.ToArray();
        }

        foreach (var token in tokens)
        {
            try
            {
                token.Cancel();
            }
            catch
            {
                // ignored
            }
        }
    }

    private void UpdateBudgetStatusMessageLocked()
    {
        var limit = GetFileSizeLimitBytes();
        var used = _sessionBytesDownloaded + _sessionBytesReserved;

        if (_budgetQueue.Count > 0)
            return;

        if (limit <= 0)
        {
            _budgetStatusMessage = null;
            return;
        }

        if (used >= limit)
        {
            _budgetStatusMessage = $"Download budget reached ({FormatSize(used)} of {FormatSize(limit)} used). Increase the file-size limit to resume.";
        }
        else
        {
            _budgetStatusMessage = null;
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
            if (!_tokenManager.IsReady() || !ApiHelpers.ValidateApiBaseUrl(_config))
                return;

            var url = $"{_config.ApiBaseUrl.TrimEnd('/')}/api/users/me/installations";
            var payload = new { assetId, status };
            var request = new HttpRequestMessage(HttpMethod.Post, url)
            {
                Content = new StringContent(JsonSerializer.Serialize(payload), System.Text.Encoding.UTF8, "application/json")
            };
            ApiHelpers.AddAuthHeader(request, _tokenManager);
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

            LoadBundleCache();
            ApplyBundleCacheToAssets();
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

    private void LoadBundleCache()
    {
        try
        {
            if (!File.Exists(_bundlesFile))
            {
                return;
            }

            var json = File.ReadAllText(_bundlesFile);
            var wrapper = JsonSerializer.Deserialize<BundlesCache>(json);
            if (wrapper == null)
            {
                return;
            }

            _bundleEtag = wrapper.Etag;

            lock (_bundleCacheLock)
            {
                _bundleCache.Clear();
                if (wrapper.Bundles != null)
                {
                    foreach (var entry in wrapper.Bundles)
                    {
                        if (entry == null || string.IsNullOrEmpty(entry.Id))
                        {
                            continue;
                        }

                        entry.Items ??= new List<Asset>();
                        _bundleCache[entry.Id] = CloneBundleCacheEntry(entry);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            PluginServices.Instance?.Log.Error(ex, "Failed to load bundles cache");
        }
    }

    private void SaveBundleCache()
    {
        try
        {
            List<BundleCacheEntry> snapshot;
            lock (_bundleCacheLock)
            {
                snapshot = _bundleCache.Values.Select(CloneBundleCacheEntry).ToList();
            }

            var wrapper = new BundlesCache
            {
                Etag = _bundleEtag,
                Bundles = snapshot,
            };

            var json = JsonSerializer.Serialize(wrapper);
            File.WriteAllText(_bundlesFile, json);
        }
        catch (Exception ex)
        {
            PluginServices.Instance?.Log.Error(ex, "Failed to save bundles cache");
        }
    }

    private void ApplyBundleCacheToAssets()
    {
        lock (_bundleCacheLock)
        {
            if (_bundleCache.Count == 0)
            {
                return;
            }

            foreach (var asset in _assets.Where(static a => string.Equals(a.Kind, "BUNDLE", StringComparison.OrdinalIgnoreCase)))
            {
                if (asset.Items != null && asset.Items.Count > 0)
                {
                    continue;
                }

                if (_bundleCache.TryGetValue(asset.Id, out var entry) && entry.Items.Count > 0)
                {
                    asset.Items = entry.Items.Select(CloneAsset).ToList();
                }
            }
        }
    }

    private async Task<bool> EnsureBundleItemsAsync(Asset bundle)
    {
        if (bundle.Items != null && bundle.Items.Count > 0)
        {
            return true;
        }

        BundleCacheEntry? cached;
        lock (_bundleCacheLock)
        {
            _bundleCache.TryGetValue(bundle.Id, out cached);
        }

        if (cached != null && cached.Items.Count > 0)
        {
            bundle.Items = cached.Items.Select(CloneAsset).ToList();
            return true;
        }

        var fetched = await FetchBundlesAsync().ConfigureAwait(false);
        if (!fetched)
        {
            return false;
        }

        lock (_bundleCacheLock)
        {
            _bundleCache.TryGetValue(bundle.Id, out cached);
        }

        if (cached != null && cached.Items.Count > 0)
        {
            bundle.Items = cached.Items.Select(CloneAsset).ToList();
            return true;
        }

        return false;
    }

    private async Task<bool> FetchBundlesAsync()
    {
        if (!_tokenManager.IsReady() || !ApiHelpers.ValidateApiBaseUrl(_config))
        {
            return false;
        }

        await _bundleFetchLock.WaitAsync().ConfigureAwait(false);
        try
        {
            var baseUrl = _config.ApiBaseUrl.TrimEnd('/');
            var url = $"{baseUrl}/api/syncshell/bundles";
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            ApiHelpers.AddAuthHeader(request, _tokenManager);

            if (!string.IsNullOrEmpty(_bundleEtag))
            {
                request.Headers.TryAddWithoutValidation("If-None-Match", _bundleEtag);
            }

            using var response = await _httpClient.SendAsync(request).ConfigureAwait(false);

            if (response.StatusCode == HttpStatusCode.NotModified)
            {
                if (UpdateBundleEtag(response))
                {
                    SaveBundleCache();
                }

                return true;
            }

            if (!response.IsSuccessStatusCode)
            {
                PluginServices.Instance?.Log.Warning($"Failed to fetch SyncShell bundles: {(int)response.StatusCode} {response.ReasonPhrase}");
                return false;
            }

            UpdateBundleEtag(response);
            var json = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
            BundlesResponse? payload = null;

            try
            {
                payload = JsonSerializer.Deserialize<BundlesResponse>(json, new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true,
                });
            }
            catch (JsonException ex)
            {
                PluginServices.Instance?.Log.Error(ex, "Failed to deserialize SyncShell bundles response");
                return false;
            }

            if (payload == null)
            {
                return false;
            }

            var map = new Dictionary<string, BundleCacheEntry>(StringComparer.OrdinalIgnoreCase);
            foreach (var bundle in payload.Items)
            {
                if (bundle == null || string.IsNullOrWhiteSpace(bundle.Id))
                {
                    continue;
                }

                var entry = new BundleCacheEntry
                {
                    Id = bundle.Id!,
                    Name = bundle.Name ?? string.Empty,
                    Description = bundle.Description ?? string.Empty,
                    UpdatedAt = bundle.UpdatedAt,
                };

                if (bundle.Assets != null)
                {
                    foreach (var asset in bundle.Assets)
                    {
                        if (asset == null)
                        {
                            continue;
                        }

                        entry.Items.Add(CreateBundleAsset(asset));
                    }
                }

                map[entry.Id] = entry;
            }

            lock (_bundleCacheLock)
            {
                _bundleCache.Clear();
                foreach (var pair in map)
                {
                    _bundleCache[pair.Key] = pair.Value;
                }
            }

            ApplyBundleCacheToAssets();
            SaveBundleCache();

            return true;
        }
        catch (Exception ex)
        {
            PluginServices.Instance?.Log.Error(ex, "Failed to fetch SyncShell bundles");
            return false;
        }
        finally
        {
            _bundleFetchLock.Release();
        }
    }

    private static Asset CreateBundleAsset(BundleAssetResponse asset)
    {
        var createdAt = asset.CreatedAt ?? DateTimeOffset.UtcNow;
        var updatedAt = asset.UpdatedAt ?? createdAt;
        var dependencies = asset.Dependencies?.Where(static d => !string.IsNullOrWhiteSpace(d)).Select(static d => d).ToList() ?? new List<string>();

        return new Asset
        {
            Id = asset.Id ?? string.Empty,
            Name = string.IsNullOrWhiteSpace(asset.Name) ? asset.Id ?? string.Empty : asset.Name!,
            Kind = string.IsNullOrWhiteSpace(asset.Kind) ? "UNKNOWN" : asset.Kind!,
            Size = asset.Size,
            Uploader = string.Empty,
            CreatedAt = createdAt,
            UpdatedAt = updatedAt,
            DownloadUrl = asset.DownloadUrl ?? string.Empty,
            Dependencies = dependencies,
            PeerId = string.Empty,
        };
    }

    private static BundleCacheEntry CloneBundleCacheEntry(BundleCacheEntry entry)
    {
        var items = entry.Items ?? new List<Asset>();
        return new BundleCacheEntry
        {
            Id = entry.Id,
            Name = entry.Name,
            Description = entry.Description,
            UpdatedAt = entry.UpdatedAt,
            Items = items.Select(CloneAsset).ToList(),
        };
    }

    private bool UpdateBundleEtag(HttpResponseMessage response)
    {
        string? newTag = null;
        if (response.Headers.TryGetValues("ETag", out var etags))
        {
            newTag = etags.LastOrDefault();
        }

        if (string.IsNullOrEmpty(newTag) && response.Content?.Headers?.LastModified is DateTimeOffset lastModified)
        {
            newTag = lastModified.ToString("R");
        }

        if (string.Equals(_bundleEtag, newTag, StringComparison.Ordinal))
        {
            return false;
        }

        _bundleEtag = newTag;
        return true;
    }

    public void ClearCaches()
    {
        try
        {
            if (File.Exists(_assetsFile))
                File.Delete(_assetsFile);
            if (File.Exists(_installedFile))
                File.Delete(_installedFile);
            if (File.Exists(_bundlesFile))
                File.Delete(_bundlesFile);
        }
        catch (Exception ex)
        {
            PluginServices.Instance?.Log.Error(ex, "Failed to clear caches");
        }

        _assets.Clear();
        _installations.Clear();
        _updatesAvailable.Clear();
        _seenAssetIds.Clear();
        lock (_bundleCacheLock)
        {
            _bundleCache.Clear();
        }
        lock (_inventoryLock)
            _peerInventories.Clear();
        _etag = null;
        _bundleEtag = null;
        _needsRefresh = true;
    }

    private async Task<bool> EnsurePairingAsync(bool force = false, CancellationToken cancellationToken = default)
    {
        if (!_tokenManager.IsReady() || !ApiHelpers.ValidateApiBaseUrl(_config))
        {
            return false;
        }

        var now = DateTimeOffset.UtcNow;
        var expiresAt = _pairingExpiresAt;
        if (!force && expiresAt.HasValue && expiresAt.Value - PairingRefreshSkew > now)
        {
            return true;
        }

        await _pairingLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            now = DateTimeOffset.UtcNow;
            expiresAt = _pairingExpiresAt;
            if (!force && expiresAt.HasValue && expiresAt.Value - PairingRefreshSkew > now)
            {
                return true;
            }

            var url = $"{_config.ApiBaseUrl.TrimEnd('/')}/api/syncshell/pair";
            using var request = new HttpRequestMessage(HttpMethod.Post, url);
            ApiHelpers.AddAuthHeader(request, _tokenManager);
            using var response = await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);

            if (response.StatusCode == HttpStatusCode.Unauthorized)
            {
                PluginServices.Instance?.Log.Warning("SyncShell pairing request was unauthorized; authentication may be invalid.");
                return false;
            }

            if (!response.IsSuccessStatusCode)
            {
                var detail = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                throw new HttpRequestException($"Pairing failed with status {(int)response.StatusCode}: {detail}", null, response.StatusCode);
            }

            var payload = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            var lifetime = DeterminePairingLifetime(payload, now);
            if (lifetime <= TimeSpan.Zero)
            {
                lifetime = DefaultPairingLifetime;
            }

            var newExpiry = now + lifetime;
            _pairingLifetime = lifetime;
            _pairingExpiresAt = newExpiry;

            if (_syncshellState.PairingExpiresAt != newExpiry)
            {
                _syncshellState.PairingExpiresAt = newExpiry;
                PluginServices.Instance?.PluginInterface?.SavePluginConfig(_config);
            }

            return true;
        }
        catch (Exception ex)
        {
            PluginServices.Instance?.Log.Error(ex, "Failed to refresh SyncShell pairing token");
            return false;
        }
        finally
        {
            _pairingLock.Release();
        }
    }

    private async Task<HttpResponseMessage?> SendWithPairingRetryAsync(
        Func<HttpRequestMessage> requestFactory,
        HttpCompletionOption completionOption = HttpCompletionOption.ResponseContentRead,
        CancellationToken cancellationToken = default)
    {
        if (!_tokenManager.IsReady() || !ApiHelpers.ValidateApiBaseUrl(_config))
        {
            return null;
        }

        HttpResponseMessage? response = null;
        for (var attempt = 0; attempt < 2; attempt++)
        {
            var paired = await EnsurePairingAsync(force: attempt > 0, cancellationToken).ConfigureAwait(false);
            if (!paired)
            {
                break;
            }

            using var request = requestFactory();
            response = await _httpClient.SendAsync(request, completionOption, cancellationToken).ConfigureAwait(false);
            if (response.StatusCode != HttpStatusCode.Unauthorized)
            {
                return response;
            }

            PluginServices.Instance?.Log.Warning("SyncShell request returned 401; attempting to refresh pairing token.");

            if (attempt == 0)
            {
                response.Dispose();
                response = null;
                continue;
            }

            return response;
        }

        return response;
    }

    private TimeSpan DeterminePairingLifetime(string? payload, DateTimeOffset now)
    {
        if (!string.IsNullOrWhiteSpace(payload))
        {
            try
            {
                using var document = JsonDocument.Parse(payload);
                var root = document.RootElement;

                if (TryExtractExpiration(root, "expiresAt", now, out var expiresAt) ||
                    TryExtractExpiration(root, "expires_at", now, out expiresAt))
                {
                    var lifetime = expiresAt - now;
                    if (lifetime > TimeSpan.Zero)
                    {
                        return lifetime;
                    }
                }

                if (TryExtractLifetimeSeconds(root, out var seconds) && seconds > 0)
                {
                    return TimeSpan.FromSeconds(seconds);
                }
            }
            catch (JsonException ex)
            {
                PluginServices.Instance?.Log.Debug("Failed to parse SyncShell pairing response payload", ex);
            }
            catch (FormatException ex)
            {
                PluginServices.Instance?.Log.Debug("Failed to parse SyncShell pairing response timestamp", ex);
            }
        }

        return _pairingLifetime > TimeSpan.Zero ? _pairingLifetime : DefaultPairingLifetime;
    }

    private static bool TryExtractExpiration(JsonElement root, string propertyName, DateTimeOffset now, out DateTimeOffset expiresAt)
    {
        expiresAt = default;
        if (!root.TryGetProperty(propertyName, out var element))
        {
            return false;
        }

        switch (element.ValueKind)
        {
            case JsonValueKind.String when DateTimeOffset.TryParse(
                     element.GetString(),
                     CultureInfo.InvariantCulture,
                     DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                     out var parsed):
                expiresAt = parsed;
                return expiresAt > now;
            case JsonValueKind.Number when element.TryGetInt64(out var seconds):
                expiresAt = DateTimeOffset.FromUnixTimeSeconds(seconds);
                return expiresAt > now;
            case JsonValueKind.Number when element.TryGetDouble(out var secondsDouble):
                expiresAt = DateTimeOffset.FromUnixTimeSeconds((long)Math.Floor(secondsDouble));
                return expiresAt > now;
            default:
                return false;
        }
    }

    private static bool TryExtractLifetimeSeconds(JsonElement root, out double seconds)
    {
        foreach (var propertyName in PairingLifetimeProperties)
        {
            if (!root.TryGetProperty(propertyName, out var element))
            {
                continue;
            }

            switch (element.ValueKind)
            {
                case JsonValueKind.Number when element.TryGetDouble(out seconds) && seconds > 0:
                    return true;
                case JsonValueKind.String when double.TryParse(element.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out seconds) && seconds > 0:
                    return true;
            }
        }

        seconds = 0;
        return false;
    }

    private async Task PostAsync(string path)
    {
        try
        {
            var url = $"{_config.ApiBaseUrl.TrimEnd('/')}{path}";
            var response = await SendWithPairingRetryAsync(() =>
            {
                var request = new HttpRequestMessage(HttpMethod.Post, url);
                ApiHelpers.AddAuthHeader(request, _tokenManager);
                return request;
            }).ConfigureAwait(false);

            if (response == null)
            {
                throw new HttpRequestException("SyncShell pairing is unavailable.");
            }

            using var resp = response;
            if (resp.StatusCode == HttpStatusCode.Unauthorized)
            {
                throw new HttpRequestException("Unauthorized", null, resp.StatusCode);
            }

            if (!resp.IsSuccessStatusCode)
            {
                var detail = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
                throw new HttpRequestException($"Request failed with status {(int)resp.StatusCode}: {detail}", null, resp.StatusCode);
            }
        }
        catch (Exception ex)
        {
            var services = PluginServices.Instance;
            services?.Log.Error(ex, $"Failed to call SyncShell endpoint {path}");

            var action = path.Split('/', StringSplitOptions.RemoveEmptyEntries).LastOrDefault() ?? path;
            _uiThreadActions.Enqueue(() => services?.ToastGui.ShowError($"SyncShell {action} failed – check logs."));
        }
    }

    private async Task<bool> UploadManifestAsync()
    {
        if (!_peerSyncEnabled)
            return false;

        if (!_tokenManager.IsReady())
            return false;

        try
        {
            var paired = await EnsurePairingAsync().ConfigureAwait(false);
            if (!paired)
                return false;

            await _syncClient.PushManifestAsync().ConfigureAwait(false);
            return true;
        }
        catch (Exception ex)
        {
            PluginServices.Instance?.Log.Warning("Failed to push SyncShell manifest", ex);
            return false;
        }
    }

    private async Task ResyncAll()
    {
        ClearCaches();
        await PostAsync("/api/syncshell/resync");
        var manifestUploaded = await UploadManifestAsync().ConfigureAwait(false);
        if (manifestUploaded)
            ClearManualPending();
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
            if (!_tokenManager.IsReady() || !ApiHelpers.ValidateApiBaseUrl(_config))
                return;

            var url = $"{_config.ApiBaseUrl.TrimEnd('/')}/api/users/me/installations";
            var req = new HttpRequestMessage(HttpMethod.Get, url);
            ApiHelpers.AddAuthHeader(req, _tokenManager);
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
        _ = EnsurePairingAsync();
        StartPeriodicRefresh();
        UpdateSyncClientState();
    }

    private void HandleTokenUnlinked()
    {
        _ = StopSyncClientAsync();
        StopPeriodicRefresh();
        _pairingExpiresAt = null;
        if (_syncshellState.PairingExpiresAt != null)
        {
            _syncshellState.PairingExpiresAt = null;
            PluginServices.Instance?.PluginInterface?.SavePluginConfig(_config);
        }
    }

    private void UpdateSyncClientState()
    {
        if (_tokenManager.IsReady() && _config.FCSyncShell && !_syncPaused && _peerSyncEnabled)
        {
            _syncClient.Start();
            return;
        }

        _ = StopSyncClientAsync();
    }

    private void SetSyncPaused(bool paused)
    {
        if (_syncPaused == paused)
        {
            return;
        }

        _syncPaused = paused;
        if (_config.Categories.TryGetValue("syncshell", out var st))
        {
            st.Paused = paused;
            PluginServices.Instance?.PluginInterface.SavePluginConfig(_config);
        }

        if (paused)
        {
            CancelActiveDownloads();
        }
        else
        {
            ProcessBudgetQueue(force: true);
        }

        UpdateSyncClientState();
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
        _disposed = true;
        StopPeriodicRefresh();
        CancelActiveDownloads();
        foreach (var unsubscribe in _ipcUnsubscribers)
        {
            try
            {
                unsubscribe();
            }
            catch (Exception ex)
            {
                PluginServices.Instance?.Log.Debug(ex, "Failed to unsubscribe SyncShell IPC listener");
            }
        }
        _ipcUnsubscribers.Clear();
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
        _manifestPushLock.Dispose();
        _pairingLock.Dispose();
        _bundleFetchLock.Dispose();
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

    private enum LocalStateChangeSource
    {
        Penumbra,
        Glamourer,
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

    private sealed class DownloadDeferredException : Exception
    {
        public string Reason { get; }

        public DownloadDeferredException(string reason)
            : base(reason)
        {
            Reason = reason;
        }
    }

    private class PenumbraConflict
    {
        public string ModName { get; set; } = string.Empty;
        public TaskCompletionSource<bool> Tcs { get; set; } = new();
    }

    private readonly record struct PendingDownload(string Key, Asset Asset, bool IsBundle, bool AutoResume);

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

    private class BundlesResponse
    {
        [JsonPropertyName("items")]
        public List<BundleResponseItem> Items { get; set; } = new();
    }

    private class BundleResponseItem
    {
        [JsonPropertyName("id")]
        public string? Id { get; set; }

        [JsonPropertyName("name")]
        public string? Name { get; set; }

        [JsonPropertyName("description")]
        public string? Description { get; set; }

        [JsonPropertyName("updated_at")]
        public DateTimeOffset? UpdatedAt { get; set; }

        [JsonPropertyName("assets")]
        public List<BundleAssetResponse> Assets { get; set; } = new();
    }

    private class BundleAssetResponse
    {
        [JsonPropertyName("id")]
        public string? Id { get; set; }

        [JsonPropertyName("name")]
        public string? Name { get; set; }

        [JsonPropertyName("kind")]
        public string? Kind { get; set; }

        [JsonPropertyName("download_url")]
        public string? DownloadUrl { get; set; }

        [JsonPropertyName("size")]
        public long Size { get; set; }

        [JsonPropertyName("dependencies")]
        public List<string>? Dependencies { get; set; }

        [JsonPropertyName("created_at")]
        public DateTimeOffset? CreatedAt { get; set; }

        [JsonPropertyName("updated_at")]
        public DateTimeOffset? UpdatedAt { get; set; }

        [JsonPropertyName("quantity")]
        public int? Quantity { get; set; }
    }

    private class BundlesCache
    {
        [JsonPropertyName("etag")]
        public string? Etag { get; set; }

        [JsonPropertyName("bundles")]
        public List<BundleCacheEntry> Bundles { get; set; } = new();
    }

    private class BundleCacheEntry
    {
        [JsonPropertyName("id")]
        public string Id { get; set; } = string.Empty;

        [JsonPropertyName("name")]
        public string Name { get; set; } = string.Empty;

        [JsonPropertyName("description")]
        public string Description { get; set; } = string.Empty;

        [JsonPropertyName("updated_at")]
        public DateTimeOffset? UpdatedAt { get; set; }

        [JsonPropertyName("items")]
        public List<Asset> Items { get; set; } = new();
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

