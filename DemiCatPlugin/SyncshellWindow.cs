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
using ImGuiNET;
using Dalamud.Plugin.Ipc;
using Dalamud.Plugin.Services;
using DemiCatPlugin.SyncShell;
using Penumbra.Api.Enums;
using Serilog;
using Serilog.Events;
using Dalamud.Interface.Utility;
using ImGuiInputTextCallbackData = ImGuiNET.ImGuiInputTextCallbackData;
using ImGuiMouseCursor = ImGuiNET.ImGuiMouseCursor;

namespace DemiCatPlugin;

public class SyncshellWindow : IDisposable
{
    public static SyncshellWindow? Instance { get; private set; }

    private static readonly object PenumbraOverrideLock = new();
    private static PenumbraResolveOptions? _pendingPenumbraOverrides;

    private static readonly TimeSpan DefaultPairingLifetime = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan PairingRefreshSkew = TimeSpan.FromSeconds(15);
    private static readonly string[] PairingLifetimeProperties = { "expiresIn", "expires_in", "ttl" };
    private const int MembershipPanelCount = 4;
    private const float MinMembershipPanelHeight = 80f;
    private const float MinMembershipPanelRatio = 0.05f;
    private const int InviteSuggestionLimit = 10;
    private static readonly float[] DefaultMembershipPanelRatios =
    {
        110f / 560f,
        150f / 560f,
        150f / 560f,
        150f / 560f,
    };
    private static readonly string[] MembershipPanelRatioKeys =
    {
        "currentlySynced",
        "memberPresence",
        "inviteMember",
        "pendingApprovals",
    };
    private static readonly ImGuiMouseCursor ResizeNsCursor = ResolveResizeNsCursor();
    private const float DefaultSyncSettingsHeight = 170f;
    private const float MinSyncSettingsHeight = 120f;
    private const float MaxSyncSettingsHeight = 400f;

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
    private readonly IDisposable? _tokenWatcher;
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
    private bool _needsRefresh = true;
    private PenumbraConflict? _penumbraConflict;
    private static DateTimeOffset _lastRedraw;
    private DateTimeOffset? _pairingExpiresAt;
    private TimeSpan _pairingLifetime = DefaultPairingLifetime;
    private readonly float[] _membershipPanelRatios = new float[MembershipPanelCount];
    private readonly float[] _membershipPanelHeights = new float[MembershipPanelCount];
    private bool _membershipPanelRatiosDirty;
    private float _syncSettingsHeight = DefaultSyncSettingsHeight;
    private bool _syncSettingsHeightDirty;
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
    private readonly List<MemberPresenceEntry> _memberPresence = new();
    private readonly List<MemberPresenceEntry> _currentlySyncedMembers = new();
    private readonly List<PendingApprovalEntry> _pendingApprovals = new();
    private readonly Dictionary<string, Config.SyncshellInviteState> _inviteStateByTarget = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Config.SyncshellInviteState> _inviteStateByRequestId = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _pendingApprovalInFlight = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _scopeUpdateInFlight = new(StringComparer.OrdinalIgnoreCase);
    private string _inviteTarget = string.Empty;
    private readonly List<MemberPresenceEntry> _inviteSuggestions = new();
    private int _inviteSuggestionIndex = -1;
    private bool _inviteSuggestionsDirty;
    private bool _inviteSuggestionsOpen;
    private Vector2 _inviteSuggestionAnchorMin;
    private Vector2 _inviteSuggestionAnchorMax;
    private Vector2 _inviteSuggestionWindowSize;
    private bool _inviteSuggestionFiltered;
    private bool _focusInviteInputNextFrame;
    private static SyncshellWindow? _activeInviteCallbackOwner;
    private static unsafe readonly ImGuiInputTextCallback _inviteInputCallback = OnInviteInputEdited;
    private int _inviteInFlight;
    private DateTimeOffset _lastMembershipFetch;
    private bool _membershipNeedsRefresh = true;
    private int _membershipRefreshInProgress;
    private int _membershipRefreshRequested;
    private string? _membershipError;

    public SyncshellWindow(Config config, HttpClient httpClient, TokenManager tokenManager)
    {
        if (!config.FCSyncShell)
            throw new InvalidOperationException("Syncshell disabled");

        _config = config;
        _httpClient = httpClient;
        _tokenManager = tokenManager;

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

        var log = services?.Log ?? new NullPluginLog();
        _resolver = new Resolver(config, _blobStore, log, services?.PluginInterface);
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
        if (state.SyncSettingsHeight is { } storedHeight && float.IsFinite(storedHeight) && storedHeight > 0f)
        {
            _syncSettingsHeight = Math.Clamp(storedHeight, MinSyncSettingsHeight, MaxSyncSettingsHeight);
        }
        InitializeMembershipPanelRatios();

        foreach (var inviteEntry in state.Invites.ToList())
        {
            if (string.IsNullOrWhiteSpace(inviteEntry.Target))
            {
                state.Invites.Remove(inviteEntry);
                continue;
            }

            inviteEntry.Target = inviteEntry.Target.Trim();
            inviteEntry.Status = NormalizeStatus(inviteEntry.Status);

            _inviteStateByTarget[inviteEntry.Target] = inviteEntry;
            if (!string.IsNullOrWhiteSpace(inviteEntry.RequestId))
                _inviteStateByRequestId[inviteEntry.RequestId] = inviteEntry;
        }

        _autoSyncAllUsers = _config.SyncshellAutoSyncAllUsers;
        _manualSyncAllUsers = _config.SyncshellManualSyncAllUsers;
        _manualSyncCustom = _config.SyncshellManualSyncCustom;
        EnforceSyncPreferenceInvariant(saveIfChanged: true);

        _syncClient.UpdatePenumbraOverrides(PenumbraResolveOptions.FromConfig(_config));

        PenumbraResolveOptions? pendingOverrides;
        lock (PenumbraOverrideLock)
        {
            pendingOverrides = _pendingPenumbraOverrides;
            _pendingPenumbraOverrides = null;
            Instance = this;
        }

        if (pendingOverrides != null)
        {
            _syncClient.UpdatePenumbraOverrides(pendingOverrides);
        }

        _assetsFile = Path.Combine(configDir, "assets.json");
        _installedFile = Path.Combine(configDir, "installed.json");
        _bundlesFile = Path.Combine(configDir, "bundles.json");
        LoadCaches();

        _ = TrimCacheAsync();

        SubscribeToStateChanges();
        _tokenWatcher = _tokenManager.RegisterWatcher(HandleTokenLinked, HandleTokenUnlinked);

        RequestMembershipRefresh();
    }

    public void OnPenumbraOverridesChanged()
    {
        PluginServices.Instance?.PluginInterface?.SavePluginConfig(_config);
        _syncClient.UpdatePenumbraOverrides(PenumbraResolveOptions.FromConfig(_config));
    }

    public static void NotifyPenumbraOverridesChanged(Config config)
    {
        var overrides = PenumbraResolveOptions.FromConfig(config);
        lock (PenumbraOverrideLock)
        {
            if (Instance != null)
            {
                Instance._syncClient.UpdatePenumbraOverrides(overrides);
                return;
            }

            _pendingPenumbraOverrides = overrides;
        }
    }

    public void Draw()
    {
        PumpClientEvents();

        if (ImGui.GetCurrentContext() == IntPtr.Zero)
        {
            return;
        }

        try
        {
            if (!_config.FCSyncShell)
            {
                const string message = "SyncShell is under development";
                var size = ImGui.CalcTextSize(message);
                var avail = ImGui.GetContentRegionAvail();
                ImGui.SetCursorPos(new Vector2((avail.X - size.X) / 2, (avail.Y - size.Y) / 2));
                ImGui.TextUnformatted(message);
                return;
            }

            if (!_loading && (Volatile.Read(ref _needsRefresh) || DateTimeOffset.UtcNow - _lastRefresh > TimeSpan.FromMinutes(5)))
                _ = Refresh();

            if (Volatile.Read(ref _membershipRefreshInProgress) == 0)
            {
                var refreshAge = DateTimeOffset.UtcNow - _lastMembershipFetch;
                if (Volatile.Read(ref _membershipNeedsRefresh) || refreshAge > TimeSpan.FromSeconds(45))
                {
                    RequestMembershipRefresh();
                }
            }

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

            var style = ImGui.GetStyle();
            var settingsSplitterHeight = Math.Max(4f, style.FramePadding.Y);
            var totalAvailableHeight = ImGui.GetContentRegionAvail().Y;
            var maxSettingsHeight = CalculateMaxSyncSettingsHeight(totalAvailableHeight, settingsSplitterHeight);
            if (!float.IsFinite(_syncSettingsHeight) || _syncSettingsHeight <= 0f)
                _syncSettingsHeight = DefaultSyncSettingsHeight;
            maxSettingsHeight = MathF.Max(MinSyncSettingsHeight, maxSettingsHeight);
            _syncSettingsHeight = Math.Clamp(_syncSettingsHeight, MinSyncSettingsHeight, maxSettingsHeight);
            ImGui.BeginChild("sync-settings", new Vector2(-1, _syncSettingsHeight), ImGuiChildFlags.Border, ImGuiWindowFlags.None);
            try
            {
                if (ImGui.BeginTabBar("syncshell-settings-tabs"))
                {
                    if (ImGui.BeginTabItem("Sync"))
                    {
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

                        ImGui.EndTabItem();
                    }
                    ImGui.EndTabBar();
                }
            }
            finally
            {
                ImGui.EndChild();
            }
        var splitterWidth = ImGui.GetContentRegionAvail().X;
        if (splitterWidth <= 0f)
        {
            ImGui.Dummy(new Vector2(0f, settingsSplitterHeight));
        }
        else
        {
            ImGui.InvisibleButton("##syncshell-settings-splitter", new Vector2(splitterWidth, settingsSplitterHeight));
            var splitterActive = ImGui.IsItemActive();
            if (ImGui.IsItemHovered() || splitterActive)
                ImGui.SetMouseCursor(ResizeNsCursor);

            if (splitterActive)
            {
                var delta = ImGui.GetIO().MouseDelta.Y;
                if (Math.Abs(delta) > float.Epsilon)
                {
                    var previous = _syncSettingsHeight;
                    var adjusted = Math.Clamp(previous + delta, MinSyncSettingsHeight, maxSettingsHeight);
                    if (Math.Abs(adjusted - previous) > float.Epsilon)
                    {
                        _syncSettingsHeight = adjusted;
                        _syncSettingsHeightDirty = true;
                    }
                }
            }
            else if (_syncSettingsHeightDirty)
            {
                SaveSyncSettingsHeight();
                _syncSettingsHeightDirty = false;
            }
        }
        if (_syncSettingsHeightDirty && splitterWidth <= 0f)
        {
            SaveSyncSettingsHeight();
            _syncSettingsHeightDirty = false;
        }

        ImGui.Separator();

        DrawMembershipPanels();

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
            ImGui.BeginChild("card", new Vector2(-1, childHeight), ImGuiChildFlags.Border, ImGuiWindowFlags.None);
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
    catch (Exception ex)
    {
        try
        {
            PluginServices.Instance?.Log.Error(ex, "SyncshellWindow.Draw()");
        }
        catch
        {
            // ignored
        }
    }

    }

    private static float CalculateMaxSyncSettingsHeight(float availableHeight, float splitterHeight)
    {
        if (!float.IsFinite(availableHeight) || availableHeight <= 0f)
            return MinSyncSettingsHeight;

        var membershipSplitterHeight = Math.Max(4f, ImGui.GetStyle().FramePadding.Y);
        var minRemaining = splitterHeight
            + MinMembershipPanelHeight * MembershipPanelCount
            + membershipSplitterHeight * (MembershipPanelCount - 1);
        var maxHeight = MathF.Max(MinSyncSettingsHeight, availableHeight - minRemaining);
        maxHeight = MathF.Min(MaxSyncSettingsHeight, maxHeight);
        return MathF.Max(MinSyncSettingsHeight, maxHeight);
    }

    private void SaveSyncSettingsHeight()
    {
        var clamped = Math.Clamp(_syncSettingsHeight, MinSyncSettingsHeight, MaxSyncSettingsHeight);
        _syncSettingsHeight = clamped;
        _syncshellState.SyncSettingsHeight = clamped;
        PluginServices.Instance?.PluginInterface?.SavePluginConfig(_config);
    }

    private void DrawMembershipPanels()
    {
        var error = _membershipError;
        if (!string.IsNullOrEmpty(error))
        {
            ImGui.TextColored(new Vector4(1f, 0.6f, 0.6f, 1f), error);
        }
        else if (Volatile.Read(ref _membershipRefreshInProgress) == 1)
        {
            ImGui.TextUnformatted("Updating membership data...");
        }

        var style = ImGui.GetStyle();
        var splitterHeight = Math.Max(4f, style.FramePadding.Y);
        var availableHeight = ImGui.GetContentRegionAvail().Y;
        var totalSplitterHeight = splitterHeight * (MembershipPanelCount - 1);
        var minTotalHeight = MinMembershipPanelHeight * MembershipPanelCount;
        var panelAreaHeight = minTotalHeight;
        if (availableHeight > 0f)
        {
            var desiredHeight = MathF.Max(minTotalHeight, availableHeight - totalSplitterHeight);
            panelAreaHeight = MathF.Min(desiredHeight, availableHeight);
        }

        UpdateMembershipPanelHeights(panelAreaHeight);

        DrawMembershipPanel("Currently synced", _membershipPanelHeights[0], DrawCurrentlySyncedContent);
        DrawMembershipSplitter(0, splitterHeight, panelAreaHeight);
        DrawMembershipPanel("Member presence", _membershipPanelHeights[1], DrawMemberPresenceContent);
        DrawMembershipSplitter(1, splitterHeight, panelAreaHeight);
        DrawMembershipPanel("Invite member", _membershipPanelHeights[2], DrawInviteEntryContent);
        DrawMembershipSplitter(2, splitterHeight, panelAreaHeight);
        DrawMembershipPanel("Pending approvals", _membershipPanelHeights[3], DrawPendingApprovalsContent, true);

        if (_membershipPanelRatiosDirty)
        {
            SaveMembershipPanelRatios();
        }
    }


    private void DrawMembershipSplitter(int index, float splitterHeight, float totalPanelHeight)
    {
        if (index >= MembershipPanelCount - 1)
            return;

        var splitterWidth = ImGui.GetContentRegionAvail().X;
        if (splitterWidth <= 0f)
        {
            ImGui.Dummy(new Vector2(0f, splitterHeight));
            return;
        }

        var id = $"##syncshell-membership-splitter-{index}";
        ImGui.InvisibleButton(id, new Vector2(splitterWidth, splitterHeight));
        if (ImGui.IsItemHovered() || ImGui.IsItemActive())
            ImGui.SetMouseCursor(ResizeNsCursor);

        if (ImGui.IsItemActive())
        {
            var delta = ImGui.GetIO().MouseDelta.Y;
            if (Math.Abs(delta) > float.Epsilon)
                AdjustMembershipPanelHeights(index, delta, totalPanelHeight);
        }

        var splitterMin = ImGui.GetItemRectMin();
        var splitterMax = ImGui.GetItemRectMax();
        var drawList = ImGui.GetWindowDrawList();
        var separatorColor = ImGui.GetColorU32(ImGuiCol.Separator);
        var centerY = (splitterMin.Y + splitterMax.Y) * 0.5f;
        drawList.AddLine(new Vector2(splitterMin.X, centerY), new Vector2(splitterMax.X, centerY), separatorColor);
    }

    private void AdjustMembershipPanelHeights(int index, float delta, float totalPanelHeight)
    {
        if (index < 0 || index >= MembershipPanelCount - 1)
            return;

        var upper = _membershipPanelHeights[index];
        var lower = _membershipPanelHeights[index + 1];
        var pairTotal = upper + lower;
        if (pairTotal <= 0f)
            return;

        var minUpper = MinMembershipPanelHeight;
        var minLower = MinMembershipPanelHeight;
        var maxUpper = MathF.Max(minUpper, pairTotal - minLower);
        var newUpper = Math.Clamp(upper + delta, minUpper, maxUpper);
        var newLower = pairTotal - newUpper;
        if (newLower < minLower)
        {
            newLower = minLower;
            newUpper = pairTotal - newLower;
        }

        if (Math.Abs(newUpper - upper) < 0.001f && Math.Abs(newLower - lower) < 0.001f)
            return;

        _membershipPanelHeights[index] = newUpper;
        _membershipPanelHeights[index + 1] = newLower;
        UpdateMembershipRatiosFromHeights(totalPanelHeight);
    }

    private void UpdateMembershipPanelHeights(float totalPanelHeight)
    {
        NormalizeMembershipPanelRatios();

        if (totalPanelHeight <= 0f)
        {
            for (var i = 0; i < MembershipPanelCount; i++)
                _membershipPanelHeights[i] = MinMembershipPanelHeight;
            return;
        }

        var remainingHeight = totalPanelHeight;
        var remainingRatio = 1f;
        for (var i = 0; i < MembershipPanelCount; i++)
        {
            var panelsLeft = MembershipPanelCount - i - 1;
            var minHeight = MinMembershipPanelHeight;
            var maxHeight = panelsLeft > 0
                ? MathF.Max(minHeight, remainingHeight - panelsLeft * MinMembershipPanelHeight)
                : remainingHeight;

            float desired;
            if (remainingRatio <= 0f)
            {
                desired = panelsLeft >= 0 ? remainingHeight / (panelsLeft + 1) : remainingHeight;
            }
            else
            {
                desired = remainingHeight * (_membershipPanelRatios[i] / remainingRatio);
            }

            var clamped = Math.Clamp(desired, minHeight, maxHeight);
            _membershipPanelHeights[i] = clamped;
            remainingHeight -= clamped;
            remainingRatio -= _membershipPanelRatios[i];
        }

        if (remainingHeight > 0f)
            _membershipPanelHeights[MembershipPanelCount - 1] += remainingHeight;
        else if (remainingHeight < 0f)
            _membershipPanelHeights[MembershipPanelCount - 1] = MathF.Max(MinMembershipPanelHeight, _membershipPanelHeights[MembershipPanelCount - 1] + remainingHeight);
    }

    private void UpdateMembershipRatiosFromHeights(float totalPanelHeight)
    {
        if (totalPanelHeight <= 0f)
            return;

        var sum = 0f;
        for (var i = 0; i < MembershipPanelCount; i++)
            sum += _membershipPanelHeights[i];

        if (sum <= 0f)
            return;

        for (var i = 0; i < MembershipPanelCount; i++)
        {
            var ratio = _membershipPanelHeights[i] / sum;
            _membershipPanelRatios[i] = Math.Clamp(ratio, MinMembershipPanelRatio, 1f);
        }

        NormalizeMembershipPanelRatios();
        _membershipPanelRatiosDirty = true;
    }

    private void InitializeMembershipPanelRatios()
    {
        var ratioState = EnsureMembershipPanelRatioState();
        for (var i = 0; i < MembershipPanelCount; i++)
        {
            var ratio = DefaultMembershipPanelRatios[i];
            if (ratioState.TryGetValue(MembershipPanelRatioKeys[i], out var stored) && float.IsFinite(stored) && stored > 0f)
                ratio = stored;

            _membershipPanelRatios[i] = ratio;
        }

        NormalizeMembershipPanelRatios();
    }

    private void NormalizeMembershipPanelRatios()
    {
        var sum = 0f;
        for (var i = 0; i < MembershipPanelCount; i++)
        {
            var ratio = _membershipPanelRatios[i];
            if (!float.IsFinite(ratio) || ratio <= 0f)
                ratio = DefaultMembershipPanelRatios[i];

            ratio = Math.Clamp(ratio, MinMembershipPanelRatio, 1f);
            _membershipPanelRatios[i] = ratio;
            sum += ratio;
        }

        if (sum <= 0f)
        {
            sum = 0f;
            for (var i = 0; i < MembershipPanelCount; i++)
            {
                var ratio = DefaultMembershipPanelRatios[i];
                _membershipPanelRatios[i] = ratio;
                sum += ratio;
            }
        }

        if (sum <= 0f)
        {
            var uniform = 1f / MembershipPanelCount;
            for (var i = 0; i < MembershipPanelCount; i++)
                _membershipPanelRatios[i] = uniform;
            return;
        }

        for (var i = 0; i < MembershipPanelCount; i++)
            _membershipPanelRatios[i] /= sum;
    }

    private void SaveMembershipPanelRatios()
    {
        NormalizeMembershipPanelRatios();

        var ratioState = EnsureMembershipPanelRatioState();
        var changed = false;
        for (var i = 0; i < MembershipPanelCount; i++)
        {
            var key = MembershipPanelRatioKeys[i];
            var ratio = _membershipPanelRatios[i];
            if (!ratioState.TryGetValue(key, out var existing) || Math.Abs(existing - ratio) > 0.0001f)
            {
                ratioState[key] = ratio;
                changed = true;
            }
        }

        if (changed)
            PluginServices.Instance?.PluginInterface?.SavePluginConfig(_config);

        _membershipPanelRatiosDirty = false;
    }

    private Dictionary<string, float> EnsureMembershipPanelRatioState()
    {
        return _syncshellState.MembershipPanelRatios ??= new Dictionary<string, float>();
    }

    private static ImGuiMouseCursor ResolveResizeNsCursor()
    {
        if (Enum.TryParse("ResizeNS", ignoreCase: true, out ImGuiMouseCursor cursor))
            return cursor;

        return ImGuiMouseCursor.ResizeAll;
    }

    private static void DrawMembershipPanel(string label, float height, Action content, bool fillRemaining = false)
    {
        var id = $"syncshell-panel-{label.Replace(' ', '-').ToLowerInvariant()}";
        var size = fillRemaining ? new Vector2(-1, 0f) : new Vector2(-1, height);
        ImGui.BeginChild(id, size, ImGuiChildFlags.Border, ImGuiWindowFlags.None);
        ImGui.TextUnformatted(label);
        ImGui.Separator();
        content();
        ImGui.EndChild();
    }

    private void DrawCurrentlySyncedContent()
    {
        if (_currentlySyncedMembers.Count == 0)
        {
            ImGui.TextDisabled("No members currently synced.");
            return;
        }

        foreach (var member in _currentlySyncedMembers)
        {
            var label = new StringBuilder(member.DisplayName);
            if (!string.IsNullOrWhiteSpace(member.SyncStatus))
            {
                label.Append(" [").Append(member.SyncStatus).Append(']');
            }
            if (member.SyncedAt.HasValue)
            {
                label.Append(" • since ").Append(FormatRelativeTime(member.SyncedAt.Value));
            }

            ImGui.TextColored(new Vector4(0.6f, 1f, 0.6f, 1f), label.ToString());
        }
    }

    private void DrawMemberPresenceContent()
    {
        if (_memberPresence.Count == 0)
        {
            ImGui.TextDisabled("No members available.");
            return;
        }

        foreach (var member in _memberPresence)
        {
            var presenceLabel = BuildPresenceLabel(member);
            var color = DeterminePresenceColor(member.Presence);
            ImGui.TextColored(color, $"{member.DisplayName} — {presenceLabel}");
            if (!string.IsNullOrWhiteSpace(member.SyncStatus))
            {
                ImGui.SameLine();
                ImGui.TextDisabled($"[{member.SyncStatus}]");
            }

            if (!member.TokenLinked)
            {
                ImGui.SameLine();
                ImGui.TextDisabled("[Token not linked]");
            }

            DrawMemberScopeControls(member);
        }
    }

    private void DrawMemberScopeControls(MemberPresenceEntry member)
    {
        if (string.IsNullOrWhiteSpace(member.Id))
        {
            return;
        }

        ImGui.Indent();
        ImGui.PushID($"syncshell-scope-{member.Id}");

        var inFlight = _scopeUpdateInFlight.Contains(member.Id);
        if (inFlight)
        {
            ImGui.BeginDisabled();
        }

        var shareAppearance = member.Scope.Appearance;
        if (ImGui.Checkbox("Share appearance", ref shareAppearance))
        {
            RequestScopeUpdate(member, shareAppearance, member.Scope.Assets);
        }
        if (shareAppearance)
        {
            ImGui.SameLine();
            ImGui.TextColored(new Vector4(1f, 0.82f, 0.4f, 1f), "Shares character appearance data.");
        }

        var shareAssets = member.Scope.Assets;
        if (ImGui.Checkbox("Share assets", ref shareAssets))
        {
            RequestScopeUpdate(member, member.Scope.Appearance, shareAssets);
        }
        if (shareAssets)
        {
            ImGui.SameLine();
            ImGui.TextColored(new Vector4(1f, 0.6f, 0.6f, 1f), "Allows this member to download your mod files.");
        }

        if (inFlight)
        {
            ImGui.EndDisabled();
            ImGui.TextDisabled("Updating member permissions...");
        }

        ImGui.PopID();
        ImGui.Unindent();
        ImGui.Spacing();
    }

    private void DrawInviteEntryContent()
    {
        ImGui.TextWrapped("Send a SyncShell invite to another member by character name.");

        string? invite = _inviteTarget;
        if (_focusInviteInputNextFrame)
        {
            ImGui.SetKeyboardFocusHere();
            _focusInviteInputNextFrame = false;
        }
        ImGui.SetNextItemWidth(-150f);
        bool submitted;
        _activeInviteCallbackOwner = this;
        try
        {
            submitted = ImGui.InputTextWithHint(
                "##syncshell-invite",
                "Character name",
                ref invite,
                64,
                ImGuiInputTextFlags.EnterReturnsTrue | ImGuiInputTextFlags.CallbackAlways,
                _inviteInputCallback
            );
        }
        finally
        {
            _activeInviteCallbackOwner = null;
        }
        invite ??= string.Empty;
        _inviteTarget = invite;
        var anchorMin = ImGui.GetItemRectMin();
        var anchorMax = ImGui.GetItemRectMax();
        var inputActive = ImGui.IsItemActive();
        var trimmed = invite.Trim();
        var edited = ImGui.IsItemEdited() || _inviteSuggestionsDirty;
        UpdateInviteSuggestionState(inputActive, anchorMin, anchorMax, edited);
        var suggestionInserted = inputActive && HandleInviteSuggestionKeys();
        suggestionInserted |= DrawInviteSuggestionWindow();
        if (suggestionInserted)
        {
            invite = _inviteTarget;
            trimmed = invite.Trim();
            submitted = false;
        }
        if (submitted && !string.IsNullOrWhiteSpace(trimmed))
        {
            TrySendInvite(trimmed);
        }

        ImGui.SameLine();
        var inviteInFlight = Volatile.Read(ref _inviteInFlight) == 1;
        var disabled = string.IsNullOrWhiteSpace(trimmed) || inviteInFlight;
        if (disabled)
        {
            ImGui.BeginDisabled();
        }

        if (ImGui.Button("Send invite"))
        {
            TrySendInvite(trimmed);
        }

        if (disabled)
        {
            ImGui.EndDisabled();
        }

        if (_inviteSuggestionFiltered && !_inviteSuggestionsOpen)
        {
            ImGui.TextDisabled("Matching members must link their SyncShell token before they can receive invites.");
        }

        ImGui.Separator();

        var invites = _syncshellState.Invites
            .OrderByDescending(static i => i.UpdatedAt)
            .ToList();

        if (invites.Count == 0)
        {
            ImGui.TextDisabled("No invites sent yet.");
            return;
        }

        foreach (var inviteEntry in invites)
        {
            var status = NormalizeStatus(inviteEntry.Status);
            var label = new StringBuilder()
                .Append(inviteEntry.Target)
                .Append(" — ")
                .Append(GetInviteStatusLabel(status));

            if (inviteEntry.UpdatedAt != default)
            {
                label.Append(" (").Append(FormatRelativeTime(inviteEntry.UpdatedAt)).Append(')');
            }

            var color = DetermineInviteColor(status);
            ImGui.TextColored(color, label.ToString());
            if (!string.IsNullOrWhiteSpace(inviteEntry.Direction))
            {
                ImGui.SameLine();
                ImGui.TextDisabled($"[{inviteEntry.Direction}]");
            }
        }
    }

    private void UpdateInviteSuggestionState(bool inputActive, Vector2 anchorMin, Vector2 anchorMax, bool edited)
    {
        if (!inputActive)
        {
            CloseInviteSuggestions();
            _inviteSuggestionFiltered = false;
            _inviteSuggestionsDirty = false;
            return;
        }

        _inviteSuggestionAnchorMin = anchorMin;
        _inviteSuggestionAnchorMax = anchorMax;

        if (!edited && _inviteSuggestionsOpen)
        {
            return;
        }

        _inviteSuggestionsDirty = false;
        _inviteSuggestionFiltered = false;
        var query = GetInviteSuggestionQuery(_inviteTarget);
        if (string.IsNullOrEmpty(query))
        {
            CloseInviteSuggestions();
            return;
        }

        _inviteSuggestions.Clear();
        var filteredDueToToken = false;
        foreach (var member in _memberPresence)
        {
            if (string.IsNullOrWhiteSpace(member.DisplayName))
            {
                continue;
            }

            if (!IsInviteSuggestionMatch(member.DisplayName, query))
            {
                continue;
            }

            if (!member.TokenLinked)
            {
                filteredDueToToken = true;
                continue;
            }

            _inviteSuggestions.Add(member);
            if (_inviteSuggestions.Count >= InviteSuggestionLimit)
            {
                break;
            }
        }

        _inviteSuggestionFiltered = filteredDueToToken;

        if (_inviteSuggestions.Count == 0)
        {
            CloseInviteSuggestions(filteredDueToToken);
            return;
        }

        _inviteSuggestionsOpen = true;
        if (_inviteSuggestionIndex < 0 || _inviteSuggestionIndex >= _inviteSuggestions.Count)
        {
            _inviteSuggestionIndex = 0;
        }
    }

    private bool DrawInviteSuggestionWindow()
    {
        if (!_inviteSuggestionsOpen || _inviteSuggestions.Count == 0)
        {
            return false;
        }

        var belowPosition = new Vector2(_inviteSuggestionAnchorMin.X, _inviteSuggestionAnchorMax.Y);
        var viewport = ImGui.GetWindowViewport();
        var min = viewport.WorkPos;
        var max = min + viewport.WorkSize;
        var spaceBelow = MathF.Max(0f, max.Y - _inviteSuggestionAnchorMax.Y);
        var desiredPosition = belowPosition;

        if (_inviteSuggestionWindowSize.Y > 0f)
        {
            if (_inviteSuggestionWindowSize.Y > spaceBelow)
            {
                var minY = min.Y;
                var maxY = max.Y - _inviteSuggestionWindowSize.Y;
                var aboveY = maxY < minY
                    ? minY
                    : Math.Clamp(_inviteSuggestionAnchorMin.Y - _inviteSuggestionWindowSize.Y, minY, maxY);
                desiredPosition = new Vector2(belowPosition.X, aboveY);
            }
            else
            {
                var minY = min.Y;
                var maxY = max.Y - _inviteSuggestionWindowSize.Y;
                var clampedY = maxY < minY ? minY : Math.Clamp(belowPosition.Y, minY, maxY);
                desiredPosition = new Vector2(belowPosition.X, clampedY);
            }
        }

        ImGui.SetNextWindowPos(desiredPosition, ImGuiCond.Always);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new Vector2(6f, 6f));
        var flags = ImGuiWindowFlags.NoDecoration | ImGuiWindowFlags.NoMove | ImGuiWindowFlags.NoSavedSettings | ImGuiWindowFlags.AlwaysAutoResize;
        var applied = false;
        if (ImGui.Begin("##syncshell-invite-suggestions", flags))
        {
            var windowSize = ImGui.GetWindowSize();
            _inviteSuggestionWindowSize = windowSize;
            spaceBelow = MathF.Max(0f, max.Y - _inviteSuggestionAnchorMax.Y);

            if (windowSize.Y > 0f)
            {
                if (windowSize.Y > spaceBelow)
                {
                    var minY = min.Y;
                    var maxY = max.Y - windowSize.Y;
                    var aboveY = maxY < minY ? minY : Math.Clamp(_inviteSuggestionAnchorMin.Y - windowSize.Y, minY, maxY);
                    desiredPosition = new Vector2(belowPosition.X, aboveY);
                }
                else
                {
                    var minY = min.Y;
                    var maxY = max.Y - windowSize.Y;
                    var clampedY = maxY < minY ? minY : Math.Clamp(belowPosition.Y, minY, maxY);
                    desiredPosition = new Vector2(belowPosition.X, clampedY);
                }

                var currentPos = ImGui.GetWindowPos();
                if (currentPos != desiredPosition)
                {
                    ImGui.SetWindowPos(desiredPosition);
                }
            }

            for (var i = 0; i < _inviteSuggestions.Count; i++)
            {
                var suggestion = _inviteSuggestions[i];
                var label = string.IsNullOrWhiteSpace(suggestion.DisplayName) ? suggestion.Id : suggestion.DisplayName;
                var selected = i == _inviteSuggestionIndex;
                if (ImGui.Selectable(label, selected))
                {
                    applied |= ApplyInviteSuggestion(suggestion);
                }

                if (ImGui.IsItemHovered())
                {
                    _inviteSuggestionIndex = i;
                }
            }

            if (_inviteSuggestionFiltered)
            {
                ImGui.Separator();
                var wrap = ImGui.GetCursorPosX() + 280f;
                ImGui.PushTextWrapPos(wrap);
                ImGui.TextDisabled("Some members must link their SyncShell token before they can receive invites.");
                ImGui.PopTextWrapPos();
            }
        }
        ImGui.End();
        ImGui.PopStyleVar();

        return applied;
    }

    private bool HandleInviteSuggestionKeys()
    {
        if (!_inviteSuggestionsOpen || _inviteSuggestions.Count == 0)
        {
            return false;
        }

        var applied = false;
        if (ImGui.IsKeyPressed(ImGuiKey.DownArrow))
        {
            _inviteSuggestionIndex++;
            if (_inviteSuggestionIndex >= _inviteSuggestions.Count)
            {
                _inviteSuggestionIndex = 0;
            }
        }
        else if (ImGui.IsKeyPressed(ImGuiKey.UpArrow))
        {
            _inviteSuggestionIndex--;
            if (_inviteSuggestionIndex < 0)
            {
                _inviteSuggestionIndex = _inviteSuggestions.Count - 1;
            }
        }

        if (ImGui.IsKeyPressed(ImGuiKey.Escape))
        {
            CloseInviteSuggestions();
        }
        else if (ImGui.IsKeyPressed(ImGuiKey.Enter) && _inviteSuggestionIndex >= 0 && _inviteSuggestionIndex < _inviteSuggestions.Count)
        {
            applied = ApplyInviteSuggestion(_inviteSuggestions[_inviteSuggestionIndex]);
        }

        return applied;
    }

    private bool ApplyInviteSuggestion(MemberPresenceEntry suggestion)
    {
        var name = NormalizeInviteDisplayName(suggestion.DisplayName);
        if (string.IsNullOrEmpty(name))
        {
            return false;
        }

        var current = _inviteTarget ?? string.Empty;
        var includePrefix = current.TrimStart().StartsWith("@", StringComparison.Ordinal);
        _inviteTarget = includePrefix ? $"@{name}" : name;
        _focusInviteInputNextFrame = true;
        CloseInviteSuggestions();
        return true;
    }

    private static string NormalizeInviteDisplayName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return string.Empty;
        }

        var parts = name
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return string.Join(' ', parts);
    }

    private static string NormalizeInviteTargetForSubmission(string target)
    {
        if (string.IsNullOrWhiteSpace(target))
        {
            return string.Empty;
        }

        var trimmed = target.Trim();
        if (trimmed.StartsWith("@", StringComparison.Ordinal))
        {
            trimmed = trimmed[1..];
        }

        return NormalizeInviteDisplayName(trimmed);
    }

    private static string? GetInviteSuggestionQuery(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var trimmed = value.TrimEnd();
        var atIndex = trimmed.LastIndexOf('@');
        if (atIndex < 0)
        {
            return null;
        }

        if (atIndex > 0 && !char.IsWhiteSpace(trimmed[atIndex - 1]))
        {
            return null;
        }

        if (atIndex == trimmed.Length - 1)
        {
            return null;
        }

        var query = trimmed[(atIndex + 1)..];
        return string.IsNullOrWhiteSpace(query) ? null : query.Trim();
    }

    private static bool IsInviteSuggestionMatch(string name, string query)
        => name?.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0;

    private void CloseInviteSuggestions(bool preserveFilterNotice = false)
    {
        _inviteSuggestionsOpen = false;
        _inviteSuggestionIndex = -1;
        _inviteSuggestions.Clear();
        _inviteSuggestionsDirty = false;
        if (!preserveFilterNotice)
        {
            _inviteSuggestionFiltered = false;
        }
    }

    private static unsafe int OnInviteInputEdited(ImGuiInputTextCallbackData* data)
    {
        if (data == null)
        {
            return 0;
        }

        var owner = _activeInviteCallbackOwner;
        if (owner == null)
        {
            return 0;
        }

        owner._inviteSuggestionsDirty = true;
        return 0;
    }

    private void DrawPendingApprovalsContent()
    {
        if (_pendingApprovals.Count == 0)
        {
            ImGui.TextDisabled("No pending approvals.");
            return;
        }

        foreach (var pending in _pendingApprovals)
        {
            ImGui.PushID(pending.Id);
            ImGui.TextUnformatted($"{pending.DisplayName} — requested {FormatRelativeTime(pending.RequestedAt)}");
            var inFlight = _pendingApprovalInFlight.Contains(pending.Id);
            if (inFlight)
            {
                ImGui.BeginDisabled();
            }

            if (ImGui.Button("Approve"))
            {
                ProcessPendingApproval(pending.Id, approve: true);
            }
            ImGui.SameLine();
            if (ImGui.Button("Deny"))
            {
                ProcessPendingApproval(pending.Id, approve: false);
            }

            if (inFlight)
            {
                ImGui.EndDisabled();
            }

            ImGui.PopID();
        }
    }

    private void TrySendInvite(string target)
    {
        var normalized = NormalizeInviteTargetForSubmission(target);
        if (string.IsNullOrEmpty(normalized))
        {
            return;
        }

        if (Interlocked.CompareExchange(ref _inviteInFlight, 1, 0) != 0)
        {
            return;
        }

        _ = SendInviteAsync(normalized);
    }

    private void ProcessPendingApproval(string requestId, bool approve)
    {
        if (string.IsNullOrWhiteSpace(requestId))
        {
            return;
        }

        var trimmed = requestId.Trim();
        if (!_pendingApprovalInFlight.Add(trimmed))
        {
            return;
        }

        _ = ProcessPendingApprovalAsync(trimmed, approve);
    }

    private async Task SendInviteAsync(string target)
    {
        try
        {
            if (!_tokenManager.IsReady() || !ApiHelpers.ValidateApiBaseUrl(_config))
            {
                throw new HttpRequestException("SyncShell pairing is unavailable.");
            }

            var baseUrl = _config.ApiBaseUrl.TrimEnd('/');
            var response = await SendWithPairingRetryAsync(() =>
            {
                var payload = new { member = target };
                var request = new HttpRequestMessage(HttpMethod.Post, $"{baseUrl}/api/syncshell/invites")
                {
                    Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json"),
                };
                ApiHelpers.AddAuthHeader(request, _tokenManager);
                return request;
            }).ConfigureAwait(false);

            if (response == null)
            {
                throw new HttpRequestException("SyncShell pairing is unavailable.");
            }

            string? content = null;
            using (response)
            {
                if (!response.IsSuccessStatusCode)
                {
                    var detail = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                    throw new HttpRequestException($"Failed to send invite: {(int)response.StatusCode} {detail}", null, response.StatusCode);
                }

                content = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
            }

            string? requestId = null;
            string? status = null;
            DateTimeOffset? updatedAt = null;

            if (!string.IsNullOrWhiteSpace(content))
            {
                try
                {
                    using var document = JsonDocument.Parse(content);
                    var root = document.RootElement;
                    requestId = GetString(root, "id", "requestId", "request_id");
                    status = GetString(root, "status", "state");
                    updatedAt = GetDateTime(root, "updatedAt", "updated_at", "createdAt", "created_at");
                }
                catch (JsonException ex)
                {
                    PluginServices.Instance?.Log.Debug(ex, "Failed to parse SyncShell invite response.");
                }
            }

            var parsedStatus = NormalizeStatus(status);
            var parsedUpdatedAt = updatedAt ?? DateTimeOffset.UtcNow;
            var parsedRequestId = requestId;

            _uiThreadActions.Enqueue(() =>
            {
                var entry = GetOrCreateInviteState(parsedRequestId, target, out var created);
                var changed = created;
                if (!string.Equals(entry.Status, parsedStatus, StringComparison.OrdinalIgnoreCase))
                {
                    entry.Status = parsedStatus;
                    changed = true;
                }

                if (entry.UpdatedAt != parsedUpdatedAt)
                {
                    entry.UpdatedAt = parsedUpdatedAt;
                    changed = true;
                }

                if (string.IsNullOrWhiteSpace(entry.Direction))
                {
                    entry.Direction = "outgoing";
                    changed = true;
                }

                _inviteTarget = string.Empty;
                PluginServices.Instance?.ToastGui.ShowNormal($"SyncShell invite sent to {target}.");
                if (changed)
                {
                    PluginServices.Instance?.PluginInterface?.SavePluginConfig(_config);
                }

                RequestMembershipRefresh();
            });
        }
        catch (Exception ex)
        {
            var services = PluginServices.Instance;
            services?.Log.Error(ex, "Failed to send SyncShell invite");
            _uiThreadActions.Enqueue(() =>
            {
                var entry = GetOrCreateInviteState(null, target, out var created);
                var changed = created;
                if (!string.Equals(entry.Status, "failed", StringComparison.OrdinalIgnoreCase))
                {
                    entry.Status = "failed";
                    changed = true;
                }
                entry.UpdatedAt = DateTimeOffset.UtcNow;
                if (string.IsNullOrWhiteSpace(entry.Direction))
                {
                    entry.Direction = "outgoing";
                    changed = true;
                }
                if (changed)
                {
                    services?.PluginInterface?.SavePluginConfig(_config);
                }
                services?.ToastGui.ShowError($"SyncShell invite to {target} failed – check logs.");
            });
        }
        finally
        {
            Interlocked.Exchange(ref _inviteInFlight, 0);
        }
    }

    private async Task ProcessPendingApprovalAsync(string requestId, bool approve)
    {
        try
        {
            if (!_tokenManager.IsReady() || !ApiHelpers.ValidateApiBaseUrl(_config))
            {
                throw new HttpRequestException("SyncShell pairing is unavailable.");
            }

            var baseUrl = _config.ApiBaseUrl.TrimEnd('/');
            var actionSegment = approve ? "approve" : "deny";
            var response = await SendWithPairingRetryAsync(() =>
            {
                var request = new HttpRequestMessage(HttpMethod.Post, $"{baseUrl}/api/syncshell/requests/{requestId}/{actionSegment}");
                ApiHelpers.AddAuthHeader(request, _tokenManager);
                return request;
            }).ConfigureAwait(false);

            if (response == null)
            {
                throw new HttpRequestException("SyncShell pairing is unavailable.");
            }

            using (response)
            {
                if (!response.IsSuccessStatusCode)
                {
                    var detail = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                    throw new HttpRequestException($"Failed to {actionSegment} request {requestId}: {(int)response.StatusCode} {detail}", null, response.StatusCode);
                }
            }

            _uiThreadActions.Enqueue(() =>
            {
                _pendingApprovalInFlight.Remove(requestId);
                _pendingApprovals.RemoveAll(p => string.Equals(p.Id, requestId, StringComparison.OrdinalIgnoreCase));
                PluginServices.Instance?.ToastGui.ShowNormal($"Request {(approve ? "approved" : "denied")}.");
                RequestMembershipRefresh();
            });
        }
        catch (Exception ex)
        {
            var services = PluginServices.Instance;
            services?.Log.Error(ex, "Failed to process SyncShell approval");
            _uiThreadActions.Enqueue(() =>
            {
                _pendingApprovalInFlight.Remove(requestId);
                services?.ToastGui.ShowError($"Failed to {(approve ? "approve" : "deny")} request – check logs.");
            });
        }
    }

    private void RequestMembershipRefresh()
    {
        if (_disposed)
        {
            return;
        }

        Volatile.Write(ref _membershipNeedsRefresh, true);
        Interlocked.CompareExchange(ref _membershipRefreshRequested, 1, 0);
    }

    private void RequestScopeUpdate(MemberPresenceEntry member, bool shareAppearance, bool shareAssets)
    {
        if (string.IsNullOrWhiteSpace(member.Id))
        {
            return;
        }

        var memberId = member.Id.Trim();
        if (memberId.Length == 0)
        {
            return;
        }

        if (!_scopeUpdateInFlight.Add(memberId))
        {
            return;
        }

        var previousAppearance = member.Scope.Appearance;
        var previousAssets = member.Scope.Assets;

        member.Scope.Hashes = true;
        member.Scope.Appearance = shareAppearance;
        member.Scope.Assets = shareAssets;

        _ = UpdateMemberScopeAsync(
            memberId,
            shareAppearance,
            shareAssets,
            previousAppearance,
            previousAssets,
            member);
    }

    private async Task UpdateMemberScopeAsync(
        string memberId,
        bool shareAppearance,
        bool shareAssets,
        bool previousAppearance,
        bool previousAssets,
        MemberPresenceEntry member)
    {
        try
        {
            if (!_tokenManager.IsReady() || !ApiHelpers.ValidateApiBaseUrl(_config))
            {
                throw new HttpRequestException("SyncShell pairing is unavailable.");
            }

            var baseUrl = _config.ApiBaseUrl.TrimEnd('/');
            var response = await SendWithPairingRetryAsync(() =>
            {
                var payload = new { appearance = shareAppearance, assets = shareAssets };
                var request = new HttpRequestMessage(
                    HttpMethod.Post,
                    $"{baseUrl}/api/syncshell/members/{memberId}/scope")
                {
                    Content = new StringContent(
                        JsonSerializer.Serialize(payload),
                        Encoding.UTF8,
                        "application/json"),
                };
                ApiHelpers.AddAuthHeader(request, _tokenManager);
                return request;
            }).ConfigureAwait(false);

            if (response == null)
            {
                throw new HttpRequestException("SyncShell pairing is unavailable.");
            }

            bool? responseAppearance = null;
            bool? responseAssets = null;

            using (response)
            {
                if (!response.IsSuccessStatusCode)
                {
                    var detail = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                    throw new HttpRequestException(
                        $"Failed to update member scope: {(int)response.StatusCode} {detail}",
                        null,
                        response.StatusCode);
                }

                var content = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                if (!string.IsNullOrWhiteSpace(content))
                {
                    try
                    {
                        using var document = JsonDocument.Parse(content);
                        if (document.RootElement.TryGetProperty("scope", out var scopeElement) &&
                            scopeElement.ValueKind == JsonValueKind.Array)
                        {
                            var appearance = false;
                            var assets = false;
                            var hashes = false;
                            foreach (var scopeValue in scopeElement.EnumerateArray())
                            {
                                if (scopeValue.ValueKind != JsonValueKind.String)
                                    continue;

                                switch (scopeValue.GetString()?.Trim().ToLowerInvariant())
                                {
                                    case "hashes":
                                        hashes = true;
                                        break;
                                    case "appearance":
                                        appearance = true;
                                        break;
                                    case "assets":
                                        assets = true;
                                        break;
                                }
                            }

                            if (hashes)
                            {
                                responseAppearance = appearance;
                                responseAssets = assets;
                            }
                            else
                            {
                                responseAppearance = false;
                                responseAssets = false;
                            }
                        }
                    }
                    catch (JsonException)
                    {
                        // Ignore malformed responses and rely on optimistic state.
                    }
                }
            }

            _uiThreadActions.Enqueue(() =>
            {
                if (responseAppearance.HasValue)
                {
                    member.Scope.Appearance = responseAppearance.Value;
                }
                if (responseAssets.HasValue)
                {
                    member.Scope.Assets = responseAssets.Value;
                }
                _scopeUpdateInFlight.Remove(memberId);
                RequestMembershipRefresh();
            });
        }
        catch (Exception ex)
        {
            var services = PluginServices.Instance;
            services?.Log.Warning(ex, "Failed to update SyncShell member scope");

            _uiThreadActions.Enqueue(() =>
            {
                member.Scope.Appearance = previousAppearance;
                member.Scope.Assets = previousAssets;
                _scopeUpdateInFlight.Remove(memberId);
                services?.ToastGui.ShowError("Failed to update member scope – check logs.");
            });
        }
    }

    private async Task RefreshMembershipOverviewAsync(bool force = false)
    {
        if (_disposed || !_config.FCSyncShell)
        {
            return;
        }

        if (!force)
        {
            var stale = DateTimeOffset.UtcNow - _lastMembershipFetch;
            if (!Volatile.Read(ref _membershipNeedsRefresh) && stale < TimeSpan.FromSeconds(45))
            {
                return;
            }
        }

        if (Interlocked.Exchange(ref _membershipRefreshInProgress, 1) == 1)
        {
            if (force)
            {
                Volatile.Write(ref _membershipNeedsRefresh, true);
            }
            return;
        }

        try
        {
            Volatile.Write(ref _membershipNeedsRefresh, false);
            if (!_tokenManager.IsReady() || !ApiHelpers.ValidateApiBaseUrl(_config))
            {
                return;
            }

            var baseUrl = _config.ApiBaseUrl.TrimEnd('/');
            var response = await SendWithPairingRetryAsync(() =>
            {
                var request = new HttpRequestMessage(HttpMethod.Get, $"{baseUrl}/api/syncshell/memberships");
                ApiHelpers.AddAuthHeader(request, _tokenManager);
                return request;
            }).ConfigureAwait(false);

            if (response == null)
            {
                throw new HttpRequestException("SyncShell pairing is unavailable.");
            }

            using (response)
            {
                if (response.StatusCode == HttpStatusCode.NotFound)
                {
                    _uiThreadActions.Enqueue(() => _membershipError = "Server does not support SyncShell membership endpoints yet.");
                    return;
                }

                if (!response.IsSuccessStatusCode)
                {
                    var detail = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                    throw new HttpRequestException($"Failed to fetch membership data: {(int)response.StatusCode} {detail}", null, response.StatusCode);
                }

                var content = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                if (string.IsNullOrWhiteSpace(content))
                {
                    _uiThreadActions.Enqueue(() => _membershipError = "Membership response was empty.");
                    return;
                }

                using var document = JsonDocument.Parse(content);
                var snapshot = document.RootElement.Clone();
                _uiThreadActions.Enqueue(() =>
                {
                    _membershipError = null;
                    ApplyMembershipOverview(snapshot);
                });
            }
        }
        catch (Exception ex)
        {
            var services = PluginServices.Instance;
            services?.Log.Warning(ex, "Failed to refresh SyncShell membership overview");
            _uiThreadActions.Enqueue(() => _membershipError = ex.Message);
        }
        finally
        {
            _lastMembershipFetch = DateTimeOffset.UtcNow;
            Interlocked.Exchange(ref _membershipRefreshInProgress, 0);
        }
    }

    private void ApplyMembershipOverview(JsonElement root)
    {
        _currentlySyncedMembers.Clear();
        if (TryGetArray(root, out var syncedArray, "currentlySynced", "currently_synced"))
        {
            foreach (var item in syncedArray.EnumerateArray())
            {
                if (item.ValueKind == JsonValueKind.Object)
                {
                    var entry = ParseMemberEntry(item);
                    if (string.IsNullOrWhiteSpace(entry.DisplayName))
                    {
                        entry.DisplayName = string.IsNullOrWhiteSpace(entry.Id) ? "Unknown" : entry.Id;
                    }
                    _currentlySyncedMembers.Add(entry);
                }
                else if (item.ValueKind == JsonValueKind.String)
                {
                    var name = item.GetString();
                    if (!string.IsNullOrWhiteSpace(name))
                    {
                        _currentlySyncedMembers.Add(new MemberPresenceEntry { DisplayName = name });
                    }
                }
            }
        }

        _memberPresence.Clear();
        if (TryGetArray(root, out var membersArray, "members", "memberPresence", "presence"))
        {
            foreach (var item in membersArray.EnumerateArray())
            {
                if (item.ValueKind == JsonValueKind.Object)
                {
                    var entry = ParseMemberEntry(item);
                    if (string.IsNullOrWhiteSpace(entry.DisplayName))
                    {
                        entry.DisplayName = string.IsNullOrWhiteSpace(entry.Id) ? "Unknown" : entry.Id;
                    }
                    _memberPresence.Add(entry);
                }
                else if (item.ValueKind == JsonValueKind.String)
                {
                    var name = item.GetString();
                    if (!string.IsNullOrWhiteSpace(name))
                    {
                        _memberPresence.Add(new MemberPresenceEntry { DisplayName = name });
                    }
                }
            }
        }

        _pendingApprovals.Clear();
        if (TryGetArray(root, out var pendingArray, "pendingApprovals", "pending_requests", "pending"))
        {
            foreach (var item in pendingArray.EnumerateArray())
            {
                if (item.ValueKind == JsonValueKind.Object)
                {
                    var entry = ParsePendingApproval(item);
                    if (!string.IsNullOrWhiteSpace(entry.Id))
                    {
                        if (string.IsNullOrWhiteSpace(entry.DisplayName))
                        {
                            entry.DisplayName = entry.Id;
                        }
                        _pendingApprovals.Add(entry);
                    }
                }
            }
        }

        var invitesChanged = false;
        if (TryGetArray(root, out var invitesArray, "invites", "sentInvites"))
        {
            foreach (var item in invitesArray.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                var target = GetString(item, "displayName", "target", "name", "character");
                if (string.IsNullOrWhiteSpace(target))
                {
                    target = GetString(item, "id", "requestId", "request_id");
                }

                if (string.IsNullOrWhiteSpace(target))
                {
                    continue;
                }

                var requestId = GetString(item, "id", "requestId", "request_id");
                var status = NormalizeStatus(GetString(item, "status", "state"));
                var updatedAt = GetDateTime(item, "updatedAt", "updated_at", "createdAt", "created_at") ?? DateTimeOffset.UtcNow;
                var direction = GetString(item, "direction");

                var entry = GetOrCreateInviteState(string.IsNullOrWhiteSpace(requestId) ? null : requestId, target, out var created);
                var changed = created;
                if (!string.IsNullOrWhiteSpace(status) && !string.Equals(entry.Status, status, StringComparison.OrdinalIgnoreCase))
                {
                    entry.Status = status;
                    changed = true;
                }

                if (entry.UpdatedAt != updatedAt)
                {
                    entry.UpdatedAt = updatedAt;
                    changed = true;
                }

                if (!string.IsNullOrWhiteSpace(direction) && !string.Equals(entry.Direction, direction, StringComparison.OrdinalIgnoreCase))
                {
                    entry.Direction = direction;
                    changed = true;
                }

                if (changed)
                {
                    invitesChanged = true;
                }
            }
        }

        _memberPresence.Sort((a, b) => string.Compare(a.DisplayName, b.DisplayName, StringComparison.OrdinalIgnoreCase));
        _currentlySyncedMembers.Sort((a, b) => string.Compare(a.DisplayName, b.DisplayName, StringComparison.OrdinalIgnoreCase));
        _pendingApprovals.Sort((a, b) => DateTimeOffset.Compare(b.RequestedAt, a.RequestedAt));

        CloseInviteSuggestions();
        _inviteSuggestionsDirty = true;

        if (invitesChanged)
        {
            PluginServices.Instance?.PluginInterface?.SavePluginConfig(_config);
        }
    }

    private Config.SyncshellInviteState GetOrCreateInviteState(string? requestId, string target, out bool created)
    {
        created = false;
        var trimmedTarget = target.Trim();
        Config.SyncshellInviteState? entry = null;

        if (!string.IsNullOrWhiteSpace(requestId))
        {
            _inviteStateByRequestId.TryGetValue(requestId, out entry);
        }

        if (entry == null && !string.IsNullOrWhiteSpace(trimmedTarget))
        {
            _inviteStateByTarget.TryGetValue(trimmedTarget, out entry);
        }

        if (entry == null)
        {
            entry = new Config.SyncshellInviteState
            {
                Target = trimmedTarget,
                Status = "pending",
                UpdatedAt = DateTimeOffset.UtcNow,
            };
            _syncshellState.Invites.Add(entry);
            created = true;
        }

        if (!string.IsNullOrWhiteSpace(requestId))
        {
            entry.RequestId = requestId;
            _inviteStateByRequestId[requestId] = entry;
        }

        if (!string.IsNullOrWhiteSpace(trimmedTarget))
        {
            entry.Target = trimmedTarget;
            _inviteStateByTarget[trimmedTarget] = entry;
        }

        return entry;
    }

    private static MemberPresenceEntry ParseMemberEntry(JsonElement element)
    {
        var entry = new MemberPresenceEntry
        {
            Id = GetString(element, "id", "memberId", "userId"),
            DisplayName = GetString(element, "displayName", "name", "nickname"),
            Presence = GetString(element, "presence", "status"),
            SyncStatus = GetString(element, "syncStatus", "state"),
            LastSeen = GetDateTime(element, "lastSeen", "last_seen"),
            SyncedAt = GetDateTime(element, "syncedAt", "since", "synced_at"),
            TokenLinked = GetBoolean(element, "tokenLinked", "token_linked"),
        };

        if (TryGetProperty(element, "scope", out var scopeElement) && scopeElement.ValueKind == JsonValueKind.Array)
        {
            foreach (var scopeValue in scopeElement.EnumerateArray())
            {
                if (scopeValue.ValueKind != JsonValueKind.String)
                    continue;

                var value = scopeValue.GetString();
                if (string.IsNullOrWhiteSpace(value))
                    continue;

                switch (value.Trim().ToLowerInvariant())
                {
                    case "hashes":
                        entry.Scope.Hashes = true;
                        break;
                    case "appearance":
                        entry.Scope.Appearance = true;
                        break;
                    case "assets":
                        entry.Scope.Assets = true;
                        break;
                }
            }
        }

        return entry;
    }

    private static PendingApprovalEntry ParsePendingApproval(JsonElement element)
    {
        return new PendingApprovalEntry
        {
            Id = GetString(element, "id", "requestId", "request_id"),
            DisplayName = GetString(element, "displayName", "name", "character"),
            RequestedAt = GetDateTime(element, "requestedAt", "createdAt", "created_at") ?? DateTimeOffset.UtcNow,
        };
    }

    private static Vector4 DetermineInviteColor(string status)
    {
        return status switch
        {
            "pending" or "requested" => new Vector4(1f, 0.8f, 0.2f, 1f),
            "approved" or "accepted" or "joined" => new Vector4(0.6f, 1f, 0.6f, 1f),
            "denied" or "rejected" or "cancelled" or "expired" or "failed" => new Vector4(1f, 0.6f, 0.6f, 1f),
            _ => new Vector4(0.8f, 0.8f, 0.8f, 1f),
        };
    }

    private static Vector4 DeterminePresenceColor(string? presence)
    {
        if (string.IsNullOrWhiteSpace(presence))
        {
            return new Vector4(0.8f, 0.8f, 0.8f, 1f);
        }

        switch (presence.ToLowerInvariant())
        {
            case "online":
                return new Vector4(0.6f, 1f, 0.6f, 1f);
            case "away":
            case "idle":
                return new Vector4(1f, 0.85f, 0.4f, 1f);
            case "offline":
                return new Vector4(0.7f, 0.7f, 0.7f, 1f);
            default:
                return new Vector4(0.8f, 0.8f, 0.8f, 1f);
        }
    }

    private static string BuildPresenceLabel(MemberPresenceEntry entry)
    {
        if (string.Equals(entry.Presence, "online", StringComparison.OrdinalIgnoreCase))
        {
            return "Online";
        }

        if (string.Equals(entry.Presence, "away", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(entry.Presence, "idle", StringComparison.OrdinalIgnoreCase))
        {
            return "Away";
        }

        if (string.Equals(entry.Presence, "offline", StringComparison.OrdinalIgnoreCase))
        {
            return entry.LastSeen.HasValue
                ? $"Offline (last seen {FormatRelativeTime(entry.LastSeen.Value)})"
                : "Offline";
        }

        if (!string.IsNullOrWhiteSpace(entry.Presence))
        {
            var text = entry.Presence.Replace('_', ' ').ToLowerInvariant();
            return CultureInfo.InvariantCulture.TextInfo.ToTitleCase(text);
        }

        return "Unknown";
    }

    private static string GetInviteStatusLabel(string status)
    {
        if (string.IsNullOrWhiteSpace(status))
        {
            return "Pending";
        }

        var text = status.Replace('_', ' ').ToLowerInvariant();
        return CultureInfo.InvariantCulture.TextInfo.ToTitleCase(text);
    }

    private static string NormalizeStatus(string? status)
    {
        return string.IsNullOrWhiteSpace(status) ? "pending" : status.Trim().ToLowerInvariant();
    }

    private static bool TryGetArray(JsonElement root, out JsonElement array, params string[] propertyNames)
    {
        foreach (var name in propertyNames)
        {
            if (TryGetProperty(root, name, out var value) && value.ValueKind == JsonValueKind.Array)
            {
                array = value;
                return true;
            }
        }

        array = default;
        return false;
    }

    private static bool TryGetProperty(JsonElement element, string name, out JsonElement value)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            value = default;
            return false;
        }

        if (element.TryGetProperty(name, out value))
        {
            return true;
        }

        foreach (var property in element.EnumerateObject())
        {
            if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                value = property.Value;
                return true;
            }
        }

        value = default;
        return false;
    }

    private static string GetString(JsonElement element, params string[] propertyNames)
    {
        foreach (var name in propertyNames)
        {
            if (TryGetProperty(element, name, out var value))
            {
                return value.ValueKind switch
                {
                    JsonValueKind.String => value.GetString() ?? string.Empty,
                    JsonValueKind.Number when value.TryGetInt64(out var longValue) => longValue.ToString(CultureInfo.InvariantCulture),
                    JsonValueKind.Number when value.TryGetDouble(out var doubleValue) => doubleValue.ToString(CultureInfo.InvariantCulture),
                    JsonValueKind.True => "true",
                    JsonValueKind.False => "false",
                    _ => value.GetRawText(),
                };
            }
        }

        return string.Empty;
    }

    private static bool GetBoolean(JsonElement element, params string[] propertyNames)
    {
        foreach (var name in propertyNames)
        {
            if (!TryGetProperty(element, name, out var value))
            {
                continue;
            }

            switch (value.ValueKind)
            {
                case JsonValueKind.True:
                    return true;
                case JsonValueKind.False:
                    return false;
                case JsonValueKind.String:
                {
                    var text = value.GetString();
                    if (string.IsNullOrWhiteSpace(text))
                    {
                        return false;
                    }

                    if (bool.TryParse(text, out var boolResult))
                    {
                        return boolResult;
                    }

                    if (long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var longValue))
                    {
                        return longValue != 0;
                    }

                    if (double.TryParse(text, NumberStyles.Float | NumberStyles.AllowThousands, CultureInfo.InvariantCulture, out var doubleValue))
                    {
                        return Math.Abs(doubleValue) > double.Epsilon;
                    }

                    break;
                }
                case JsonValueKind.Number:
                {
                    if (value.TryGetInt64(out var longValue))
                    {
                        return longValue != 0;
                    }

                    if (value.TryGetDouble(out var doubleValue))
                    {
                        return Math.Abs(doubleValue) > double.Epsilon;
                    }

                    break;
                }
            }
        }

        return false;
    }

    private static DateTimeOffset? GetDateTime(JsonElement element, params string[] propertyNames)
    {
        foreach (var name in propertyNames)
        {
            if (!TryGetProperty(element, name, out var value))
            {
                continue;
            }

            switch (value.ValueKind)
            {
                case JsonValueKind.String:
                {
                    var text = value.GetString();
                    if (string.IsNullOrWhiteSpace(text))
                    {
                        continue;
                    }

                    if (DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var parsed))
                    {
                        return parsed;
                    }

                    if (long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var seconds))
                    {
                        return DateTimeOffset.FromUnixTimeSeconds(seconds);
                    }
                    break;
                }
                case JsonValueKind.Number:
                {
                    if (value.TryGetInt64(out var seconds))
                    {
                        return DateTimeOffset.FromUnixTimeSeconds(seconds);
                    }

                    if (value.TryGetDouble(out var secondsDouble))
                    {
                        return DateTimeOffset.FromUnixTimeSeconds((long)Math.Floor(secondsDouble));
                    }
                    break;
                }
            }
        }

        return null;
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
                LocalStateChangeSource.CustomizePlus => "Customize+",
                LocalStateChangeSource.SimpleHeels => "SimpleHeels",
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

        void HandlePenumbraModSetting(ModSettingChange _, Guid __, string ___, bool ____) => HandleLocalStateChanged(LocalStateChangeSource.Penumbra);
        void HandlePenumbraLegacyModSetting(Guid __, string ___, bool ____) => HandleLocalStateChanged(LocalStateChangeSource.Penumbra);
        TrySubscribeAny(
            "Penumbra.ModSettingChanged",
            () =>
            {
                var subscriber = pi.GetIpcSubscriber<ModSettingChange, Guid, string, bool, object?>(Penumbra.Api.IpcSubscribers.ModSettingChanged.Label);
                subscriber.Subscribe(HandlePenumbraModSetting);
                return () => subscriber.Unsubscribe(HandlePenumbraModSetting);
            },
            () =>
            {
                var subscriber = pi.GetIpcSubscriber<Guid, string, bool, object?>("Penumbra.ModSettingChanged");
                subscriber.Subscribe(HandlePenumbraLegacyModSetting);
                return () => subscriber.Unsubscribe(HandlePenumbraLegacyModSetting);
            });

        void HandlePenumbraEnabled(bool _) => HandleLocalStateChanged(LocalStateChangeSource.Penumbra);
        TrySubscribeAny(
            "Penumbra.EnabledChange",
            () =>
            {
                var subscriber = pi.GetIpcSubscriber<bool, object?>(Penumbra.Api.IpcSubscribers.EnabledChange.Label);
                subscriber.Subscribe(HandlePenumbraEnabled);
                return () => subscriber.Unsubscribe(HandlePenumbraEnabled);
            },
            () =>
            {
                var subscriber = pi.GetIpcSubscriber<bool, object?>("Penumbra.EnabledChange");
                subscriber.Subscribe(HandlePenumbraEnabled);
                return () => subscriber.Unsubscribe(HandlePenumbraEnabled);
            });

        void HandleGlamourerState(nint _) => HandleLocalStateChanged(LocalStateChangeSource.Glamourer);
        void HandleGlamourerStateInt(int _) => HandleLocalStateChanged(LocalStateChangeSource.Glamourer);
        TrySubscribeAny(
            "Glamourer.StateChanged",
            () =>
            {
                var subscriber = pi.GetIpcSubscriber<nint, object?>(Glamourer.Api.IpcSubscribers.StateChanged.Label);
                subscriber.Subscribe(HandleGlamourerState);
                return () => subscriber.Unsubscribe(HandleGlamourerState);
            },
            () =>
            {
                var subscriber = pi.GetIpcSubscriber<int, object?>(Glamourer.Api.IpcSubscribers.StateChanged.Label);
                subscriber.Subscribe(HandleGlamourerStateInt);
                return () => subscriber.Unsubscribe(HandleGlamourerStateInt);
            },
            () =>
            {
                var subscriber = pi.GetIpcSubscriber<nint, object?>("Glamourer.StateChanged");
                subscriber.Subscribe(HandleGlamourerState);
                return () => subscriber.Unsubscribe(HandleGlamourerState);
            },
            () =>
            {
                var subscriber = pi.GetIpcSubscriber<int, object?>("Glamourer.StateChanged");
                subscriber.Subscribe(HandleGlamourerStateInt);
                return () => subscriber.Unsubscribe(HandleGlamourerStateInt);
            });

        void HandleCustomizePlusProfileChanged(string _) => HandleLocalStateChanged(LocalStateChangeSource.CustomizePlus);
        void HandleCustomizePlusProfileChangedGuid(Guid _) => HandleLocalStateChanged(LocalStateChangeSource.CustomizePlus);
        void HandleCustomizePlusProfileChangedFallback() => HandleLocalStateChanged(LocalStateChangeSource.CustomizePlus);
        TrySubscribeAny(
            "Customize.ProfileChanged",
            () =>
            {
                var subscriber = pi.GetIpcSubscriber<string, object?>("Customize.ProfileChanged");
                subscriber.Subscribe(HandleCustomizePlusProfileChanged);
                return () => subscriber.Unsubscribe(HandleCustomizePlusProfileChanged);
            },
            () =>
            {
                var subscriber = pi.GetIpcSubscriber<Guid, object?>("Customize.ProfileChanged");
                subscriber.Subscribe(HandleCustomizePlusProfileChangedGuid);
                return () => subscriber.Unsubscribe(HandleCustomizePlusProfileChangedGuid);
            },
            () =>
            {
                var subscriber = pi.GetIpcSubscriber<object?>("Customize.ProfileChanged");
                Action handler = HandleCustomizePlusProfileChangedFallback;
                subscriber.Subscribe(handler);
                return () =>
                {
                    subscriber.Unsubscribe(handler);
                };
            });

        void HandleCustomizePlusProfileApplied(string _) => HandleLocalStateChanged(LocalStateChangeSource.CustomizePlus);
        void HandleCustomizePlusProfileAppliedGuid(Guid _) => HandleLocalStateChanged(LocalStateChangeSource.CustomizePlus);
        void HandleCustomizePlusProfileAppliedFallback() => HandleLocalStateChanged(LocalStateChangeSource.CustomizePlus);
        TrySubscribeAny(
            "Customize.ProfileApplied",
            () =>
            {
                var subscriber = pi.GetIpcSubscriber<string, object?>("Customize.ProfileApplied");
                subscriber.Subscribe(HandleCustomizePlusProfileApplied);
                return () => subscriber.Unsubscribe(HandleCustomizePlusProfileApplied);
            },
            () =>
            {
                var subscriber = pi.GetIpcSubscriber<Guid, object?>("Customize.ProfileApplied");
                subscriber.Subscribe(HandleCustomizePlusProfileAppliedGuid);
                return () => subscriber.Unsubscribe(HandleCustomizePlusProfileAppliedGuid);
            },
            () =>
            {
                var subscriber = pi.GetIpcSubscriber<object?>("Customize.ProfileApplied");
                Action handler = HandleCustomizePlusProfileAppliedFallback;
                subscriber.Subscribe(handler);
                return () =>
                {
                    subscriber.Unsubscribe(handler);
                };
            });

        void HandleSimpleHeelsProfileChanged(string _) => HandleLocalStateChanged(LocalStateChangeSource.SimpleHeels);
        void HandleSimpleHeelsProfileChangedGuid(Guid _) => HandleLocalStateChanged(LocalStateChangeSource.SimpleHeels);
        void HandleSimpleHeelsProfileChangedFallback() => HandleLocalStateChanged(LocalStateChangeSource.SimpleHeels);
        TrySubscribeAny(
            "SimpleHeels.ProfileChanged",
            () =>
            {
                var subscriber = pi.GetIpcSubscriber<string, object?>("SimpleHeels.ProfileChanged");
                subscriber.Subscribe(HandleSimpleHeelsProfileChanged);
                return () => subscriber.Unsubscribe(HandleSimpleHeelsProfileChanged);
            },
            () =>
            {
                var subscriber = pi.GetIpcSubscriber<Guid, object?>("SimpleHeels.ProfileChanged");
                subscriber.Subscribe(HandleSimpleHeelsProfileChangedGuid);
                return () => subscriber.Unsubscribe(HandleSimpleHeelsProfileChangedGuid);
            },
            () =>
            {
                var subscriber = pi.GetIpcSubscriber<object?>("SimpleHeels.ProfileChanged");
                Action handler = HandleSimpleHeelsProfileChangedFallback;
                subscriber.Subscribe(handler);
                return () =>
                {
                    subscriber.Unsubscribe(handler);
                };
            });

        void HandleSimpleHeelsProfileApplied(string _) => HandleLocalStateChanged(LocalStateChangeSource.SimpleHeels);
        void HandleSimpleHeelsProfileAppliedGuid(Guid _) => HandleLocalStateChanged(LocalStateChangeSource.SimpleHeels);
        void HandleSimpleHeelsProfileAppliedFallback() => HandleLocalStateChanged(LocalStateChangeSource.SimpleHeels);
        TrySubscribeAny(
            "SimpleHeels.ProfileApplied",
            () =>
            {
                var subscriber = pi.GetIpcSubscriber<string, object?>("SimpleHeels.ProfileApplied");
                subscriber.Subscribe(HandleSimpleHeelsProfileApplied);
                return () => subscriber.Unsubscribe(HandleSimpleHeelsProfileApplied);
            },
            () =>
            {
                var subscriber = pi.GetIpcSubscriber<Guid, object?>("SimpleHeels.ProfileApplied");
                subscriber.Subscribe(HandleSimpleHeelsProfileAppliedGuid);
                return () => subscriber.Unsubscribe(HandleSimpleHeelsProfileAppliedGuid);
            },
            () =>
            {
                var subscriber = pi.GetIpcSubscriber<object?>("SimpleHeels.ProfileApplied");
                Action handler = HandleSimpleHeelsProfileAppliedFallback;
                subscriber.Subscribe(handler);
                return () =>
                {
                    subscriber.Unsubscribe(handler);
                };
            });
    }

    private void TrySubscribeAny(string description, params Func<Action>[] attempts)
    {
        foreach (var attempt in attempts)
        {
            if (TrySubscribe(attempt, description))
            {
                return;
            }
        }
    }

    private bool TrySubscribe(Func<Action> subscribe, string description)
    {
        try
        {
            var unsubscribe = subscribe();
            RegisterUnsubscriber(unsubscribe, description);
            return true;
        }
        catch (Exception ex)
        {
            PluginServices.Instance?.Log.Debug(ex, $"Failed to subscribe to {description} IPC event.");
            return false;
        }
    }

    private void RegisterUnsubscriber(Action unsubscribe, string description)
    {
        _ipcUnsubscribers.Add(() =>
        {
            try
            {
                unsubscribe();
            }
            catch (Exception ex)
            {
                PluginServices.Instance?.Log.Debug(ex, $"Failed to unsubscribe from {description} IPC event.");
            }
        });
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

        if (Interlocked.CompareExchange(ref _membershipRefreshRequested, 0, 1) == 1)
        {
            _ = RefreshMembershipOverviewAsync();
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
                    PluginServices.Instance?.Log.Warning(ex, "Failed to refresh SyncShell installations");
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
            Volatile.Write(ref _needsRefresh, false);
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
            Hash = string.Empty,
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
            Hash = asset.Hash,
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

            await UpdateInstallationStatus(asset.Id, "DOWNLOADED", asset.Hash);

            switch (asset.Kind)
            {
                case "PENUMBRA_PACK":
                    await InstallPenumbraPack(tmp, asset);
                    break;
                case "GLAMOURER_DESIGN":
                    var design = await File.ReadAllTextAsync(tmp);
                    using (JsonDocument.Parse(design)) { }
                    ApplyIpc("Glamourer.Design.Apply", design);
                    await UpdateInstallationStatus(asset.Id, "APPLIED", asset.Hash);
                    break;
                case "CUSTOMIZE_PROFILE":
                    var profile = await File.ReadAllTextAsync(tmp);
                    using (JsonDocument.Parse(profile)) { }
                    ApplyIpc("Customize.ApplyProfile", profile);
                    await UpdateInstallationStatus(asset.Id, "APPLIED", asset.Hash);
                    break;
                case "SIMPLEHEELS_PROFILE":
                    var heels = await File.ReadAllTextAsync(tmp);
                    using (JsonDocument.Parse(heels)) { }
                    ApplyIpc("SimpleHeels.ApplyProfile", heels);
                    await UpdateInstallationStatus(asset.Id, "APPLIED", asset.Hash);
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
            await UpdateInstallationStatus(asset.Id, "FAILED", asset.Hash);
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

    private bool TryGetBudgetReason(string assetId, out string? reason)
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
        string? dest = null;
        string? backupPath = null;
        if (pi != null)
        {
            try
            {
                var modsDir = pi.GetIpcSubscriber<string>("Penumbra.GetModsDirectory").InvokeFunc();
                dest = Path.Combine(modsDir, asset.Name);
                var conflict = await ResolvePenumbraConflict(asset.Name, dest);
                backupPath = conflict.BackupPath;
                if (!conflict.Proceed)
                {
                    await UpdateInstallationStatus(asset.Id, "SKIPPED", asset.Hash);
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

        if (backupPath != null)
        {
            if (success)
            {
                try
                {
                    Directory.Delete(backupPath, true);
                }
                catch (Exception ex)
                {
                    PluginServices.Instance?.Log.Warning(ex, $"Failed to remove Penumbra backup for '{asset.Name}' at '{backupPath}'. Manual cleanup may be required.");
                }
            }
            else if (dest != null)
            {
                PluginServices.Instance?.Log.Information($"Restoring original Penumbra mod '{asset.Name}' after failed install.");
                try
                {
                    if (Directory.Exists(dest))
                    {
                        Directory.Delete(dest, true);
                    }
                }
                catch (Exception ex)
                {
                    PluginServices.Instance?.Log.Warning(ex, $"Failed to remove incomplete Penumbra install at '{dest}' before restoring backup for '{asset.Name}'. Manual cleanup may be required.");
                }

                try
                {
                    Directory.Move(backupPath, dest);
                }
                catch (Exception ex)
                {
                    PluginServices.Instance?.Log.Error(ex, $"Failed to restore Penumbra mod '{asset.Name}' from backup at '{backupPath}'. Manual cleanup may be required.");
                }
            }
            else
            {
                PluginServices.Instance?.Log.Warning($"Unable to restore Penumbra backup for '{asset.Name}' because the destination path is unknown. Backup remains at '{backupPath}'.");
            }
        }

        if (success)
        {
            await UpdateInstallationStatus(asset.Id, "INSTALLED", asset.Hash);
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
            await UpdateInstallationStatus(asset.Id, "APPLIED", asset.Hash);
        }
        else
        {
            await UpdateInstallationStatus(asset.Id, "FAILED", asset.Hash);
        }
    }

    private async Task<(bool Proceed, string? BackupPath)> ResolvePenumbraConflict(string modName, string dest)
    {
        if (!Directory.Exists(dest))
            return (true, null);
        var log = PluginServices.Instance?.Log;
        if (_config.PenumbraChoices.TryGetValue(modName, out var useVault))
        {
            if (!useVault)
                return (false, null);
        }
        else
        {
            var tcs = new TaskCompletionSource<bool>();
            _penumbraConflict = new PenumbraConflict { ModName = modName, Tcs = tcs };
            var result = await tcs.Task;
            _config.PenumbraChoices[modName] = result;
            PluginServices.Instance?.PluginInterface.SavePluginConfig(_config);
            if (!result)
                return (false, null);
        }

        var backupPath = GetBackupDirectoryPath(dest);
        try
        {
            Directory.Move(dest, backupPath);
            return (true, backupPath);
        }
        catch (Exception ex)
        {
            log?.Error(ex, $"Failed to move existing Penumbra mod '{modName}' to backup location '{backupPath}'.");
            return (false, null);
        }
    }

    private static string GetBackupDirectoryPath(string dest)
    {
        var basePath = dest + "_backup";
        var candidate = basePath;
        var counter = 1;
        while (Directory.Exists(candidate))
        {
            candidate = $"{basePath}_{counter++}";
        }

        return candidate;
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

    private async Task UpdateInstallationStatus(string assetId, string status, string? assetHash = null)
    {
        try
        {
            if (!_tokenManager.IsReady() || !ApiHelpers.ValidateApiBaseUrl(_config))
                return;

            var url = $"{_config.ApiBaseUrl.TrimEnd('/')}/api/users/me/installations";
            assetHash ??= GetAssetHash(assetId);
            if (assetHash == null && _installations.TryGetValue(assetId, out var existing) && !string.IsNullOrEmpty(existing.AssetHash))
            {
                assetHash = existing.AssetHash;
            }

            var payload = new { assetId, status, assetHash };
            var request = new HttpRequestMessage(HttpMethod.Post, url)
            {
                Content = new StringContent(JsonSerializer.Serialize(payload), System.Text.Encoding.UTF8, "application/json")
            };
            ApiHelpers.AddAuthHeader(request, _tokenManager);
            await _httpClient.SendAsync(request);

            _installations[assetId] = new Installation
            {
                AssetId = assetId,
                Status = status,
                UpdatedAt = DateTimeOffset.UtcNow,
                AssetHash = assetHash,
            };
            SaveInstalledCache();
            ComputeUpdates();
        }
        catch (Exception ex)
        {
            PluginServices.Instance?.Log.Error(ex, "Failed to update installation status");
        }
    }

    private string? GetAssetHash(string assetId)
    {
        var asset = _assets.FirstOrDefault(a => string.Equals(a.Id, assetId, StringComparison.Ordinal));
        if (asset != null && !string.IsNullOrEmpty(asset.Hash))
        {
            return asset.Hash;
        }

        return null;
    }

    private bool EnsureInstallationHashesFromAssets()
    {
        var updated = false;
        var missing = false;

        foreach (var pair in _installations)
        {
            if (!string.IsNullOrEmpty(pair.Value.AssetHash))
            {
                continue;
            }

            var hash = GetAssetHash(pair.Key);
            if (!string.IsNullOrEmpty(hash))
            {
                pair.Value.AssetHash = hash;
                updated = true;
            }
            else
            {
                missing = true;
            }
        }

        if (missing)
        {
            Interlocked.Exchange(ref _installationsRefreshRequested, 1);
        }

        return updated;
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

            var hashesBackfilled = EnsureInstallationHashesFromAssets();
            if (hashesBackfilled)
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
            Hash = asset.Hash ?? string.Empty,
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
        Volatile.Write(ref _needsRefresh, true);
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
            PluginServices.Instance?.Log.Warning(ex, "Failed to push SyncShell manifest");
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
            EnsureInstallationHashesFromAssets();
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
            if (!_installations.TryGetValue(asset.Id, out var inst))
            {
                continue;
            }

            if (!string.IsNullOrEmpty(asset.Hash))
            {
                if (!string.Equals(asset.Hash, inst.AssetHash, StringComparison.Ordinal))
                {
                    _updatesAvailable.Add(asset.Id);
                }
            }
            else if (asset.UpdatedAt > inst.UpdatedAt)
            {
                _updatesAvailable.Add(asset.Id);
            }
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
            PluginServices.Instance?.Log.Warning(ex, "Failed to trim SyncShell cache");
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
        if (_disposed)
        {
            return;
        }

        _ = EnsurePairingAsync();
        StartPeriodicRefresh();
        UpdateSyncClientState();
    }

    private void HandleTokenUnlinked()
    {
        if (_disposed)
        {
            return;
        }

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
        _tokenWatcher?.Dispose();
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
            PluginServices.Instance?.Log.Warning(ex, "Failed to stop SyncShell client");
        }
        _syncClient.Dispose();
        if (PluginServices.Instance?.ProgressOverlay == _progressOverlay)
            PluginServices.Instance.ProgressOverlay = null;
        _manifestPushLock.Dispose();
        _pairingLock.Dispose();
        _bundleFetchLock.Dispose();
        lock (PenumbraOverrideLock)
        {
            if (Instance == this)
                Instance = null;
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

    private enum LocalStateChangeSource
    {
        Penumbra,
        Glamourer,
        CustomizePlus,
        SimpleHeels,
    }

    private sealed class MemberScope
    {
        public bool Hashes { get; set; } = true;

        public bool Appearance { get; set; }
            = false;

        public bool Assets { get; set; }
            = false;
    }

    private sealed class MemberPresenceEntry
    {
        public string Id { get; set; } = string.Empty;

        public string DisplayName { get; set; } = string.Empty;

        public string? Presence { get; set; }
            = null;

        public string? SyncStatus { get; set; }
            = null;

        public DateTimeOffset? LastSeen { get; set; }
            = null;

        public DateTimeOffset? SyncedAt { get; set; }
            = null;

        public bool TokenLinked { get; set; }
            = false;

        public MemberScope Scope { get; } = new MemberScope();
    }

    private sealed class PendingApprovalEntry
    {
        public string Id { get; set; } = string.Empty;

        public string DisplayName { get; set; } = string.Empty;

        public DateTimeOffset RequestedAt { get; set; }
            = DateTimeOffset.UtcNow;
    }

    private sealed class PeerInventory
    {
        public Dictionary<string, Asset> Assets { get; } = new(StringComparer.OrdinalIgnoreCase);

        public DateTimeOffset LastUpdated { get; set; }
    }

    private sealed class NullPluginLog : IPluginLog
    {
        public ILogger Logger { get; } = new LoggerConfiguration().CreateLogger();

        public LogEventLevel MinimumLogLevel { get; set; }

        public void Verbose(string messageTemplate, params object[] propertyValues)
        {
        }

        public void Verbose(Exception? exception, string messageTemplate, params object[] propertyValues)
        {
        }

        public void Debug(string messageTemplate, params object[] propertyValues)
        {
        }

        public void Debug(Exception? exception, string messageTemplate, params object[] propertyValues)
        {
        }

        public void Info(string messageTemplate, params object[] propertyValues)
        {
        }

        public void Info(Exception? exception, string messageTemplate, params object[] propertyValues)
        {
        }

        public void Information(string messageTemplate, params object[] propertyValues)
        {
        }

        public void Information(Exception? exception, string messageTemplate, params object[] propertyValues)
        {
        }

        public void Warning(string messageTemplate, params object[] propertyValues)
        {
        }

        public void Warning(Exception? exception, string messageTemplate, params object[] propertyValues)
        {
        }

        public void Error(string messageTemplate, params object[] propertyValues)
        {
        }

        public void Error(Exception? exception, string messageTemplate, params object[] propertyValues)
        {
        }

        public void Fatal(string messageTemplate, params object[] propertyValues)
        {
        }

        public void Fatal(Exception? exception, string messageTemplate, params object[] propertyValues)
        {
        }

        public void Write(LogEventLevel level, Exception? exception, string messageTemplate, params object[] propertyValues)
        {
        }
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
        [JsonPropertyName("hash")]
        public string Hash { get; set; } = string.Empty;
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

        [JsonPropertyName("hash")]
        public string? Hash { get; set; }

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
        [JsonPropertyName("assetHash")]
        public string? AssetHash { get; set; }
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

                if (PropertyMatches(propertyName, "assetHash"))
                {
                    installation.AssetHash = ReadStringValue(ref reader);
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
            writer.WritePropertyName("assetHash");
            if (!string.IsNullOrEmpty(value.AssetHash))
            {
                writer.WriteStringValue(value.AssetHash);
            }
            else
            {
                writer.WriteNullValue();
            }
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

