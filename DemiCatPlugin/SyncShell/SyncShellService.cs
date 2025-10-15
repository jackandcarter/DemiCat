using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Dalamud.Game.ClientState;
using Dalamud.Game.ClientState.Objects;
using Dalamud.Game.ClientState.Objects.Enums;
using Dalamud.Plugin.Services;

namespace DemiCatPlugin.SyncShell;

public sealed class SyncShellService : ISyncShellService, IDisposable
{
    private static readonly TimeSpan TerritoryDebounce = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan RootRetryGap = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan AppearanceDebounceGap = TimeSpan.FromMilliseconds(400);
    private static readonly TimeSpan SkippedPublishLogThrottle = TimeSpan.FromSeconds(30);
    private const long MaxSyncshellFileSizeBytes = 64L * 1024L * 1024L;

    private static readonly HashSet<string> SupportedExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".tex",
        ".dds",
        ".mdl",
        ".mtrl",
        ".png",
        ".ttmp",
        ".ttmp2",
        ".scd",
        ".tmb",
        ".atex",
        ".avfx",
        ".sklb",
        ".pap",
        ".json",
        ".meta",
    };

    private readonly Config _config;
    private readonly TokenManager _tokenManager;
    private readonly SyncShellClient _client;
    private readonly BlobStore _blobStore;
    private readonly PenumbraIpc _penumbra;
    private readonly GlamourerIpc _glamourer;
    private readonly CustomizePlusIpc _customizePlus;
    private readonly SimpleHeelsIpc _simpleHeels;
    private readonly PalettePlusIpc _palettePlus;
    private readonly HonorificIpc _honorific;
    private readonly IPluginLog _log;
    private readonly IClientState _clientState;
    private readonly IFramework _framework;
    private readonly IObjectTable _objects;
    private readonly ISyncShellWatcher _watcher;

    private int _frameworkThreadId = -1;

    private readonly object _statusLock = new();
    private readonly SemaphoreSlim _lifecycleLock = new(1, 1);
    private readonly object _memberLock = new();
    private readonly object _appearanceLock = new();
    private readonly object _applyStateLock = new();
    private readonly object _penRootLock = new();

    private readonly Dictionary<string, LocalBlobInfo?> _localBlobs = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, TargetInfo> _targets = new(StringComparer.Ordinal);
    private readonly Dictionary<string, DateTimeOffset> _localSyncedAt = new(StringComparer.OrdinalIgnoreCase);

    private HashSet<string> _nearby = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _nearbyLock = new();

    private readonly SemaphoreSlim _redrawGate = new(1, 1);

    private Channel<string> _prefetchQueue = Channel.CreateUnbounded<string>();
    private Channel<string> _applyQueue = Channel.CreateUnbounded<string>();

    private CancellationTokenSource? _runCts;
    private Task? _prefetchTask;
    private Task? _applyTask;
    private Task? _membershipTask;
    private bool _disposed;
    private bool _paused;
    private bool _validationFailed;
    private string _status = "SyncShell disabled";
    private string? _glamourerJson;
    private string? _customizePlusJson;
    private string? _simpleHeelsJson;
    private string? _palettePlusJson;
    private string? _honorificJson;
    private DateTimeOffset _lastRedrawAt = DateTimeOffset.MinValue;
    private bool _loggedSettingsJsonDetection;
    private string? _cachedPenRoot;
    private string? _lastPenumbraSource;
    private DateTime _lastRootAttempt;
    private readonly Debouncer _appearanceDebounce = new(AppearanceDebounceGap);
    private string? _lastAppearanceHash;
    private DateTime _lastSkippedPublishLog;

    private List<SyncshellMemberStatus> _members = new();
    private List<SyncshellMemberStatus> _activeMembers = new();

    public SyncShellService(
        Config config,
        TokenManager tokenManager,
        SyncShellClient client,
        BlobStore blobStore,
        PenumbraIpc penumbra,
        GlamourerIpc glamourer,
        CustomizePlusIpc customizePlus,
        SimpleHeelsIpc simpleHeels,
        PalettePlusIpc palettePlus,
        HonorificIpc honorific,
        IPluginLog log,
        IClientState clientState,
        IFramework framework,
        IObjectTable objects,
        ISyncShellWatcher watcher)
    {
        _config = config ?? throw new ArgumentNullException(nameof(config));
        _tokenManager = tokenManager ?? throw new ArgumentNullException(nameof(tokenManager));
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _blobStore = blobStore ?? throw new ArgumentNullException(nameof(blobStore));
        _penumbra = penumbra ?? throw new ArgumentNullException(nameof(penumbra));
        _glamourer = glamourer ?? throw new ArgumentNullException(nameof(glamourer));
        _customizePlus = customizePlus ?? throw new ArgumentNullException(nameof(customizePlus));
        _simpleHeels = simpleHeels ?? throw new ArgumentNullException(nameof(simpleHeels));
        _palettePlus = palettePlus ?? throw new ArgumentNullException(nameof(palettePlus));
        _honorific = honorific ?? throw new ArgumentNullException(nameof(honorific));
        _log = log ?? throw new ArgumentNullException(nameof(log));
        _clientState = clientState ?? throw new ArgumentNullException(nameof(clientState));
        _framework = framework ?? throw new ArgumentNullException(nameof(framework));
        _objects = objects ?? throw new ArgumentNullException(nameof(objects));
        _watcher = watcher ?? throw new ArgumentNullException(nameof(watcher));

        _tokenManager.OnLinked += HandleTokenLinked;
        _tokenManager.OnUnlinked += HandleTokenUnlinked;
        _clientState.TerritoryChanged += HandleTerritoryChanged;
        _framework.Update += OnFrameworkTick;
        _framework.RunOnFrameworkThread(() =>
        {
            _frameworkThreadId = Environment.CurrentManagedThreadId;
        });

        _watcher.Connected += HandleWatcherConnected;
        _watcher.NearbySet += HandleWatcherNearbySet;
        _watcher.MemberChanged += HandleWatcherMemberChanged;
    }

    public event EventHandler? StatusChanged;

    public bool IsRunning => _runCts != null;

    public bool IsPaused
    {
        get => _paused;
        private set
        {
            if (_paused == value)
            {
                return;
            }

            _paused = value;
            OnStatusChanged();
        }
    }

    public string Status
    {
        get
        {
            lock (_statusLock)
            {
                if (_tokenManager.State != LinkState.Linked)
                {
                    return "Not linked";
                }

                if (!_config.EnableSyncShell)
                {
                    return "SyncShell disabled";
                }

                if (_validationFailed)
                {
                    return "Auth invalid—please relink";
                }

                if (!IsRunning)
                {
                    return _status;
                }

                if (_paused)
                {
                    return "Paused";
                }

                return _status;
            }
        }
        private set
        {
            lock (_statusLock)
            {
                _status = value;
            }

            OnStatusChanged();
        }
    }

    public int NearbyUserCount
    {
        get
        {
            lock (_nearbyLock)
            {
                return _nearby.Count;
            }
        }
    }

    public IReadOnlyList<SyncshellMemberStatus> Members
    {
        get
        {
            lock (_memberLock)
            {
                return _members.ToList();
            }
        }
    }

    public IReadOnlyList<SyncshellMemberStatus> ActiveMembers
    {
        get
        {
            lock (_memberLock)
            {
                return _activeMembers.ToList();
            }
        }
    }

    public bool PenumbraAvailable => _penumbra.Available;

    public string? DetectedPenumbraPath => GetPenumbraDetectionDetails().Path;

    public bool DetectedPenumbraPathFromSettingsJson
    {
        get
        {
            var details = GetPenumbraDetectionDetails();
            return details.Source != null
                && details.Source.Contains("settings", StringComparison.OrdinalIgnoreCase);
        }
    }

    public PenumbraDetectionDetails GetPenumbraDetectionDetails()
    {
        lock (_penRootLock)
        {
            string? path = null;
            if (!string.IsNullOrWhiteSpace(_cachedPenRoot) && Directory.Exists(_cachedPenRoot))
            {
                path = _cachedPenRoot;
            }
            else
            {
                _cachedPenRoot = null;
            }

            TimeSpan? retry = null;
            if (string.IsNullOrWhiteSpace(path))
            {
                var remaining = RootRetryGap - (DateTime.UtcNow - _lastRootAttempt);
                if (remaining > TimeSpan.Zero)
                {
                    retry = remaining;
                }
            }

            return new PenumbraDetectionDetails(path, _lastPenumbraSource, retry);
        }
    }

    private async Task<string?> ResolvePenumbraRootAsync()
    {
        lock (_penRootLock)
        {
            if (!string.IsNullOrWhiteSpace(_cachedPenRoot) && Directory.Exists(_cachedPenRoot))
            {
                return _cachedPenRoot;
            }

            if (DateTime.UtcNow - _lastRootAttempt < RootRetryGap)
            {
                return null;
            }

            _cachedPenRoot = null;
            _lastRootAttempt = DateTime.UtcNow;
        }

        var attempts = new List<string>();
        string? resolved = null;
        string? source = null;

        var overridePath = _config.PenumbraPathOverride;
        if (!string.IsNullOrWhiteSpace(overridePath))
        {
            if (Directory.Exists(overridePath))
            {
                resolved = overridePath;
                source = "Override";
            }
            else
            {
                attempts.Add($"Override missing: {overridePath}");
            }
        }

        if (resolved == null)
        {
            if (_penumbra.Available)
            {
                try
                {
                    var viaIpc = await OnFrameworkAsync(() => _penumbra.GetModDirectory()).ConfigureAwait(false);
                    if (!string.IsNullOrWhiteSpace(viaIpc) && Directory.Exists(viaIpc))
                    {
                        resolved = viaIpc;
                        source = "IPC";
                    }
                    else
                    {
                        attempts.Add("IPC returned no directory");
                    }
                }
                catch (Exception ex)
                {
                    attempts.Add($"IPC error: {ex.GetType().Name}");
                    _log.Debug(ex, "Penumbra IPC mod directory query failed");
                }
            }
            else
            {
                attempts.Add("IPC unavailable");
            }
        }

        if (resolved == null)
        {
            var diskPath = BlobStore.FindPenumbraModRootOnDisk(out var diskSource);
            if (!string.IsNullOrWhiteSpace(diskPath))
            {
                resolved = diskPath;
                source = diskSource switch
                {
                    null => "Disk",
                    "penumbra.json" => "penumbra.json",
                    "Penumbra.json" => "Installed Penumbra.json",
                    _ when diskSource.Contains("settings", StringComparison.OrdinalIgnoreCase) => "settings.json",
                    _ => diskSource,
                };

                if (!_loggedSettingsJsonDetection && diskSource != null && diskSource.Contains("settings", StringComparison.OrdinalIgnoreCase))
                {
                    _log.Debug("Detected Penumbra path from {Source}: {Path}", diskSource, diskPath);
                    _loggedSettingsJsonDetection = true;
                }
            }
            else
            {
                attempts.Add("Disk scan found no path");
            }
        }

        lock (_penRootLock)
        {
            if (!string.IsNullOrWhiteSpace(resolved) && Directory.Exists(resolved))
            {
                _cachedPenRoot = resolved;
                _lastPenumbraSource = source;
                _log.Debug("Penumbra root resolved via {Source}: {Path}", source ?? "unknown", resolved);
                return resolved;
            }

            _cachedPenRoot = null;
            _lastPenumbraSource = null;
            if (attempts.Count > 0)
            {
                _log.Information("Penumbra root resolve attempts: {Attempts}", string.Join("; ", attempts));
            }

            return null;
        }
    }

    public SyncshellTargetStage GetStage(string memberId)
    {
        lock (_applyStateLock)
        {
            return _targets.TryGetValue(memberId, out var info) ? info.Stage : SyncshellTargetStage.Unknown;
        }
    }

    public async Task Start(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        await _lifecycleLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (IsRunning)
            {
                return;
            }

            if (!_config.EnableSyncShell || _tokenManager.State != LinkState.Linked)
            {
                Status = "SyncShell disabled";
                return;
            }

            _validationFailed = !await _client.ValidateAsync(cancellationToken).ConfigureAwait(false);
            if (_validationFailed)
            {
                Status = "Auth invalid—please relink";
                return;
            }

            _runCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            _lastAppearanceHash = null;
            Status = "Initializing…";
            await RefreshAppearanceCachesAsync().ConfigureAwait(false);

            _prefetchQueue = Channel.CreateUnbounded<string>();
            _applyQueue = Channel.CreateUnbounded<string>();

            _prefetchTask = Task.Run(() => PrefetchLoopAsync(_runCts.Token), CancellationToken.None);
            _applyTask = Task.Run(() => ApplyLoopAsync(_runCts.Token), CancellationToken.None);
            _membershipTask = Task.Run(() => RefreshMembershipsAsync(_runCts.Token), CancellationToken.None);
            _watcher.Start();
            Status = "Idle";
        }
        finally
        {
            _lifecycleLock.Release();
        }
    }

    public async Task Stop(CancellationToken cancellationToken = default)
    {
        await _lifecycleLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!IsRunning)
            {
                return;
            }

            _runCts!.Cancel();

            try
            {
                await Task.WhenAll(
                    _prefetchTask ?? Task.CompletedTask,
                    _applyTask ?? Task.CompletedTask,
                    _membershipTask ?? Task.CompletedTask).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }

            _watcher.Stop();
            _prefetchTask = null;
            _applyTask = null;
            _membershipTask = null;

            _runCts.Dispose();
            _runCts = null;
            _lastAppearanceHash = null;
            Status = _config.EnableSyncShell ? "Idle" : "SyncShell disabled";
        }
        finally
        {
            _lifecycleLock.Release();
        }
    }

    public async Task TriggerPublishAsync(CancellationToken cancellationToken = default)
    {
        if (!IsRunning)
        {
            return;
        }

        await _lifecycleLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!IsRunning)
            {
                return;
            }

            await RefreshAppearanceCachesAsync().ConfigureAwait(false);
            _appearanceDebounce.Run(() => PublishIfChangedAsync());
        }
        finally
        {
            _lifecycleLock.Release();
        }
    }

    public Task ResyncAllAsync(CancellationToken cancellationToken = default)
        => TriggerPublishAsync(cancellationToken);

    public void Pause() => IsPaused = true;

    public void Resume() => IsPaused = false;

    public void ClearCache() => _blobStore.Clear();

    public Task EnforceCacheLimitAsync(CancellationToken cancellationToken = default)
    {
        var limit = Math.Max(256, _config.SyncshellCacheLimitMb) * 1024L * 1024L;
        return _blobStore.EnforceLimitAsync(limit, cancellationToken);
    }

    public bool TryValidatePenumbraPath(string? path, out string? error)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            error = "Path is required";
            return false;
        }

        if (!Directory.Exists(path))
        {
            error = "Directory not found";
            return false;
        }

        try
        {
            var testDir = Path.Combine(path, "DemiCat-Test");
            Directory.CreateDirectory(testDir);
            var file = Path.Combine(testDir, "write-test.tmp");
            File.WriteAllText(file, "demicat");
            File.Delete(file);
            Directory.Delete(testDir, true);
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }

        error = null;
        return true;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _ = Stop();
        _tokenManager.OnLinked -= HandleTokenLinked;
        _tokenManager.OnUnlinked -= HandleTokenUnlinked;
        _clientState.TerritoryChanged -= HandleTerritoryChanged;
        _framework.Update -= OnFrameworkTick;
        _watcher.Connected -= HandleWatcherConnected;
        _watcher.NearbySet -= HandleWatcherNearbySet;
        _watcher.MemberChanged -= HandleWatcherMemberChanged;
        _watcher.Dispose();
        _lifecycleLock.Dispose();
        _redrawGate.Dispose();
        _appearanceDebounce.Dispose();
    }

    private void OnFrameworkTick(IFramework _)
    {
        _frameworkThreadId = Environment.CurrentManagedThreadId;
    }

    private void ThrowIfDisposed()
    {
        EnsureNotDisposed();
    }

    private void EnsureNotDisposed()
    {
        if (_disposed)
        {
            throw new ObjectDisposedException(nameof(SyncShellService));
        }
    }

    private void HandleTokenLinked()
    {
        if (_config.EnableSyncShell)
        {
            _ = Start();
        }
    }

    private void HandleTokenUnlinked(string? _unused)
    {
        _ = Stop();
        _lastAppearanceHash = null;
        lock (_statusLock)
        {
            _status = "Not linked";
        }

        OnStatusChanged();
    }

    private void HandleTerritoryChanged(ushort territoryId)
    {
        if (!IsRunning)
        {
            return;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(TerritoryDebounce, _runCts?.Token ?? CancellationToken.None).ConfigureAwait(false);
                await RefreshAppearanceCachesAsync().ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
        });
    }

    private async Task RefreshMembershipsAsync(CancellationToken token)
    {
        if (_paused)
        {
            return;
        }

        try
        {
            var memberships = await _client.GetMembershipsAsync(token).ConfigureAwait(false);
            if (memberships != null)
            {
                UpdateMemberSnapshot(memberships);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            _log.Warning(ex, "Failed to refresh SyncShell memberships");
        }
    }

    private async Task PublishIfChangedAsync()
    {
        if (_paused || _runCts == null || _tokenManager.State != LinkState.Linked)
        {
            return;
        }

        PublishPayload payload;
        Dictionary<string, LocalBlobInfo?> localBlobs;
        try
        {
            (payload, localBlobs) = await BuildPublishPayloadAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _log.Warning(ex, "Failed to build SyncShell appearance payload");
            return;
        }

        if (string.IsNullOrEmpty(payload.DiscordId))
        {
            _log.Warning("SyncShell publish skipped due to missing Discord ID");
            return;
        }

        var hash = ComputeAppearanceHash(payload.Appearance);
        if (string.Equals(hash, _lastAppearanceHash, StringComparison.Ordinal))
        {
            Status = "Idle";
            if (DateTime.UtcNow - _lastSkippedPublishLog > SkippedPublishLogThrottle)
            {
                _log.Debug("SyncShell publish skipped; appearance unchanged.");
                _lastSkippedPublishLog = DateTime.UtcNow;
            }

            return;
        }

        _lastAppearanceHash = hash;
        _ = PublishAsync(payload, localBlobs, _runCts.Token);
    }

    private async Task PublishAsync(PublishPayload payload, Dictionary<string, LocalBlobInfo?> localBlobs, CancellationToken token)
    {
        var previousStatus = Status;
        try
        {
            Status = "Publishing…";
            var result = await _client.PublishAsync(payload, token).ConfigureAwait(false);
            var missing = (result?.Missing ?? new List<string>())
                .Where(hash => !string.IsNullOrWhiteSpace(hash))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (missing.Count > 0)
            {
                await UploadMissingBlobsAsync(missing, localBlobs, token).ConfigureAwait(false);
                Status = "Publishing…";
            }

            payload.Complete = true;
            await _client.PublishAsync(payload, token).ConfigureAwait(false);
            Status = "Active";
        }
        catch (OperationCanceledException)
        {
            Status = previousStatus;
            throw;
        }
        catch (Exception ex)
        {
            _log.Warning(ex, "Failed to publish appearance");
            Status = previousStatus;
        }
    }

    private void HandleWatcherConnected()
    {
        if (!IsRunning)
        {
            return;
        }

        _lastAppearanceHash = null;
        _ = Task.Run(async () =>
        {
            try
            {
                await RefreshMembershipsAsync(_runCts?.Token ?? CancellationToken.None).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
        }, CancellationToken.None);

        _ = Task.Run(RefreshAppearanceCaches, CancellationToken.None);
    }

    private void HandleWatcherNearbySet(string[] handles)
    {
        if (!IsRunning)
        {
            return;
        }

        handles ??= Array.Empty<string>();
        var sanitized = handles
            .Where(h => !string.IsNullOrWhiteSpace(h))
            .Select(h => h.Trim())
            .ToArray();

        lock (_nearbyLock)
        {
            _nearby = new HashSet<string>(sanitized, StringComparer.OrdinalIgnoreCase);
        }

        Status = FormatStatusForMembers(sanitized.Length);
        _ = Task.Run(async () =>
        {
            try
            {
                await RefreshMembershipsAsync(_runCts?.Token ?? CancellationToken.None).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
        }, CancellationToken.None);
        _ = Task.Run(() => EnqueuePrefetchApplyForAsync(sanitized), CancellationToken.None);
    }

    private void HandleWatcherMemberChanged(string handle)
    {
        if (!IsRunning || string.IsNullOrWhiteSpace(handle))
        {
            return;
        }

        var trimmed = handle.Trim();
        lock (_nearbyLock)
        {
            if (!_nearby.Contains(trimmed))
            {
                return;
            }
        }

        _ = Task.Run(() => EnqueuePrefetchApplyForAsync(new[] { trimmed }), CancellationToken.None);
    }

    private async Task EnqueuePrefetchApplyForAsync(IEnumerable<string> handles)
    {
        if (_runCts == null || _paused)
        {
            return;
        }

        var input = handles?
            .Where(h => !string.IsNullOrWhiteSpace(h))
            .Select(h => h.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList() ?? new List<string>();

        if (input.Count == 0)
        {
            return;
        }

        var resolved = new List<SyncshellMemberStatus>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var handle in input)
        {
            var member = ResolveMemberFromHandle(handle);
            if (member == null || string.IsNullOrWhiteSpace(member.Id))
            {
                continue;
            }

            if (seen.Add(member.Id))
            {
                resolved.Add(member);
            }
        }

        if (resolved.Count == 0)
        {
            return;
        }

        var before = resolved.Count;
        var filtered = new List<SyncshellMemberStatus>();
        HashSet<string>? visibleHandles = null;

        if (_config.OnlySyncVisible)
        {
            visibleHandles = await GetVisibleHandlesAsync().ConfigureAwait(false);
        }

        foreach (var member in resolved)
        {
            if (_config.OnlySyncVisible)
            {
                if (visibleHandles == null || !visibleHandles.Contains(MemberHandle(member)))
                {
                    continue;
                }
            }

            if (!_config.SyncAutoMode)
            {
                if (!ulong.TryParse(member.Id, out var parsed) || !_config.ManualAutoList.Contains(parsed))
                {
                    continue;
                }
            }

            filtered.Add(member);
        }

        if (_config.OnlySyncVisible)
        {
            _log.Debug("Filtered SyncShell targets by visibility: {Before} -> {After}", before, filtered.Count);
        }

        var token = _runCts.Token;

        foreach (var member in filtered)
        {
            try
            {
                var id = member.Id;
                var target = GetOrCreateTarget(id);
                target.Stage = SyncshellTargetStage.Queued;

                if (_config.BackgroundPrefetch)
                {
                    await _prefetchQueue.Writer.WriteAsync(id, token).ConfigureAwait(false);
                }
                else
                {
                    await _applyQueue.Writer.WriteAsync(id, token).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    private async Task<string[]> BuildActiveMemberListAsync()
    {
        var roster = Members;
        var eligible = roster.Where(m => m.TokenLinked && string.Equals(m.Presence, "online", StringComparison.OrdinalIgnoreCase));

        if (_config.OnlySyncVisible)
        {
            var visible = await GetVisibleHandlesAsync().ConfigureAwait(false);
            eligible = eligible.Where(m => visible.Contains(MemberHandle(m)));
        }

        if (!_config.SyncAutoMode)
        {
            var allow = _config.ManualAutoList;
            eligible = eligible.Where(m => ulong.TryParse(m.Id, out var id) && allow.Contains(id));
        }

        return eligible.Select(m => m.Id)
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Distinct()
            .ToArray();
    }

    private Task<T> OnFrameworkAsync<T>(Func<T> fn)
    {
        if (fn == null)
        {
            throw new ArgumentNullException(nameof(fn));
        }

        EnsureNotDisposed();

        if (Environment.CurrentManagedThreadId == _frameworkThreadId)
        {
            try
            {
                return Task.FromResult(fn());
            }
            catch (Exception ex)
            {
                return Task.FromException<T>(ex);
            }
        }

        var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        _framework.RunOnFrameworkThread(() =>
        {
            try
            {
                tcs.TrySetResult(fn());
            }
            catch (Exception ex)
            {
                tcs.TrySetException(ex);
            }
        });

        return tcs.Task;
    }

    private Task OnFrameworkAsync(Action fn)
    {
        if (fn == null)
        {
            throw new ArgumentNullException(nameof(fn));
        }

        EnsureNotDisposed();

        if (Environment.CurrentManagedThreadId == _frameworkThreadId)
        {
            try
            {
                fn();
                return Task.CompletedTask;
            }
            catch (Exception ex)
            {
                return Task.FromException(ex);
            }
        }

        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _framework.RunOnFrameworkThread(() =>
        {
            try
            {
                fn();
                tcs.TrySetResult(true);
            }
            catch (Exception ex)
            {
                tcs.TrySetException(ex);
            }
        });

        return tcs.Task;
    }

    private async Task<(PublishPayload Payload, Dictionary<string, LocalBlobInfo?> LocalBlobs)> BuildPublishPayloadAsync()
    {
        var actorHash = await OnFrameworkAsync(() =>
        {
            var player = _clientState.LocalPlayer;
            return player?.Name.TextValue ?? string.Empty;
            // Omit world label to avoid GeneratedSheets dependency; actorHash is just the name.
        }).ConfigureAwait(false);

        var appearance = new AppearanceMeta
        {
            ActorHash = actorHash,
        };

        Dictionary<string, LocalBlobInfo?> localBlobs;
        lock (_appearanceLock)
        {
            appearance.GlamourerJson = _glamourerJson;
            appearance.CustomizePlusJson = _customizePlusJson;
            appearance.SimpleHeelsJson = _simpleHeelsJson;
            appearance.PalettePlusJson = _palettePlusJson;
            appearance.HonorificJson = _honorificJson;
            localBlobs = new Dictionary<string, LocalBlobInfo?>(_localBlobs, StringComparer.OrdinalIgnoreCase);
        }

        foreach (var blob in localBlobs.Values.Where(b => b != null).Cast<LocalBlobInfo>().OrderBy(b => b.Name, StringComparer.OrdinalIgnoreCase))
        {
            appearance.Blobs.Add(new BlobRef
            {
                Name = blob.Name,
                Sha256 = blob.Sha256,
                Size = blob.Size,
            });
        }

        var payload = new PublishPayload
        {
            DiscordId = _tokenManager.Token ?? string.Empty,
            Appearance = appearance,
            Complete = false,
        };

        return (payload, localBlobs);
    }

    private async Task UploadMissingBlobsAsync(
        IReadOnlyList<string> missing,
        IReadOnlyDictionary<string, LocalBlobInfo?> localBlobs,
        CancellationToken token)
    {
        if (missing.Count == 0)
        {
            return;
        }

        var remaining = missing.Count;
        foreach (var hash in missing)
        {
            token.ThrowIfCancellationRequested();
            Status = $"Uploading {remaining} blob{(remaining == 1 ? string.Empty : "s")}…";
            remaining--;

            if (!localBlobs.TryGetValue(hash, out LocalBlobInfo? blob) || blob is null)
            {
                _log.Warning("Missing local blob for hash {Hash}", hash);
                continue;
            }

            if (!File.Exists(blob.FullPath))
            {
                _log.Warning("Local blob path not found: {Path}", blob.FullPath);
                continue;
            }

            var shouldUpload = true;
            try
            {
                shouldUpload = !await _client.BlobExistsAsync(hash, token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _log.Warning(ex, "Failed to query blob {Hash} existence", hash);
            }

            if (!shouldUpload)
            {
                continue;
            }

            await using var stream = new FileStream(
                blob.FullPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                64 * 1024,
                useAsync: true);

            try
            {
                await _client.UploadBlobAsync(hash, stream, token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _log.Warning(ex, "Failed to upload blob {Hash}", hash);
            }
        }
    }

    private async Task PrefetchLoopAsync(CancellationToken token)
    {
        try
        {
            var workerCount = Math.Max(1, _config.MaxConcurrentPrefetch);
            var workers = Enumerable.Range(0, workerCount)
                .Select(_ => PrefetchWorkerAsync(token))
                .ToArray();

            await Task.WhenAll(workers).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
    }

    private async Task PrefetchWorkerAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            string memberId;
            try
            {
                memberId = await _prefetchQueue.Reader.ReadAsync(token).ConfigureAwait(false);
            }
            catch
            {
                break;
            }

            var target = GetOrCreateTarget(memberId);

            if (!_config.BackgroundPrefetch)
            {
                target.Stage = SyncshellTargetStage.Queued;
                continue;
            }

            target.Stage = SyncshellTargetStage.Prefetching;
            UserMetaDto? meta = null;
            try
            {
                meta = await _client.GetLatestMetaAsync(memberId, token).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _log.Warning(ex, "GetLatestMeta failed for {Member}", memberId);
            }

            if (meta?.Appearance == null)
            {
                continue;
            }

            try
            {
                foreach (var blob in meta.Appearance.Blobs ?? new List<BlobRef>())
                {
                    await _blobStore.EnsureLocalAsync(
                        blob.Sha256,
                        ct => _client.DownloadBlobAsync(blob.Sha256, ct),
                        token).ConfigureAwait(false);
                }

                await StageTempModForMemberAsync(memberId, meta.Appearance).ConfigureAwait(false);
                target.LastHash = ComputeAppearanceHash(meta.Appearance);
                target.Stage = SyncshellTargetStage.Prefetched;

                await _applyQueue.Writer.WriteAsync(memberId, token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _log.Warning(ex, "Prefetch failed for {Member}", memberId);
                GetOrCreateTarget(memberId).Stage = SyncshellTargetStage.Failed;
            }
        }
    }

    private async Task ApplyLoopAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            string memberId;
            try
            {
                memberId = await _applyQueue.Reader.ReadAsync(token).ConfigureAwait(false);
            }
            catch
            {
                break;
            }

            var target = GetOrCreateTarget(memberId);

            if (target.Stage is not SyncshellTargetStage.Prefetched and not SyncshellTargetStage.Applying and not SyncshellTargetStage.Applied)
            {
                if (_config.BackgroundPrefetch)
                {
                    target.Stage = SyncshellTargetStage.Queued;
                    await _prefetchQueue.Writer.WriteAsync(memberId, token).ConfigureAwait(false);
                    await Task.Delay(TimeSpan.FromMilliseconds(150), token).ConfigureAwait(false);
                    continue;
                }

                try
                {
                    target.Stage = SyncshellTargetStage.Prefetching;
                    var meta = await _client.GetLatestMetaAsync(memberId, token).ConfigureAwait(false);
                    if (meta?.Appearance == null)
                    {
                        target.Stage = SyncshellTargetStage.Failed;
                        continue;
                    }

                    foreach (var blob in meta.Appearance.Blobs ?? new List<BlobRef>())
                    {
                        await _blobStore.EnsureLocalAsync(
                            blob.Sha256,
                            ct => _client.DownloadBlobAsync(blob.Sha256, ct),
                            token).ConfigureAwait(false);
                    }

                    await StageTempModForMemberAsync(memberId, meta.Appearance).ConfigureAwait(false);
                    target.LastHash = ComputeAppearanceHash(meta.Appearance);
                    target.Stage = SyncshellTargetStage.Prefetched;
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    _log.Warning(ex, "Apply prefetch failed for {Member}", memberId);
                    target.Stage = SyncshellTargetStage.Failed;
                    continue;
                }
            }

            if (_config.OnlySyncVisible)
            {
                var member = Members.FirstOrDefault(m => string.Equals(m.Id, memberId, StringComparison.OrdinalIgnoreCase));
                var visible = await GetVisibleHandlesAsync().ConfigureAwait(false);
                if (member == null || !visible.Contains(MemberHandle(member)))
                {
                    continue;
                }
            }

            var now = DateTimeOffset.UtcNow;
            if (target.LastAppliedAt + TimeSpan.FromSeconds(Math.Max(1, _config.ApplyCooldownSeconds)) > now)
            {
                continue;
            }

            try
            {
                target.Stage = SyncshellTargetStage.Applying;
                await ActivateTempModAsync(memberId).ConfigureAwait(false);
                await ApplyGlamourerAsync(memberId).ConfigureAwait(false);
                await ApplyCustomizePlusAsync(memberId).ConfigureAwait(false);
                await ApplySimpleHeelsAsync(memberId).ConfigureAwait(false);
                await ApplyPalettePlusAsync(memberId).ConfigureAwait(false);
                await ApplyHonorificAsync(memberId).ConfigureAwait(false);
                await DebouncedRedrawAsync(token).ConfigureAwait(false);

                target.LastAppliedAt = DateTimeOffset.UtcNow;
                target.Stage = SyncshellTargetStage.Applied;
                ApplyLocalSyncedAt(memberId, target.LastAppliedAt);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _log.Warning(ex, "Apply failed for {Member}", memberId);
                target.Stage = SyncshellTargetStage.Failed;
            }
        }
    }

    private async Task DebouncedRedrawAsync(CancellationToken token)
    {
        await _redrawGate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            var gap = TimeSpan.FromMilliseconds(Math.Max(50, _config.RedrawDebounceMs));
            var wait = _lastRedrawAt + gap - DateTimeOffset.UtcNow;
            if (wait > TimeSpan.Zero)
            {
                await Task.Delay(wait, token).ConfigureAwait(false);
            }

            await OnFrameworkAsync(() =>
            {
                var index = _clientState.LocalPlayer?.ObjectIndex ?? 0;
                _penumbra.RedrawObject(index);
            }).ConfigureAwait(false);
            _lastRedrawAt = DateTimeOffset.UtcNow;
        }
        finally
        {
            _redrawGate.Release();
        }
    }

    public async Task RefreshAppearanceCachesAsync()
    {
        EnsureNotDisposed();

        string? glamourerJson = null;
        string? customizePlusJson = null;
        string? simpleHeelsJson = null;
        string? palettePlusJson = null;
        string? honorificJson = null;
        try
        {
            if (_glamourer.Available)
            {
                glamourerJson = await OnFrameworkAsync(() => _glamourer.TryGetPlayerDesignJson()).ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            _log.Warning(ex, "Glamourer IPC failed");
        }

        try
        {
            if (_customizePlus.Available)
            {
                customizePlusJson = await OnFrameworkAsync(() => _customizePlus.TryExportProfileJson()).ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            _log.Debug(ex, "Customize+ IPC failed");
        }

        try
        {
            if (_simpleHeels.Available)
            {
                simpleHeelsJson = await OnFrameworkAsync(() => _simpleHeels.TryExportOffsetsJson()).ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            _log.Debug(ex, "SimpleHeels IPC failed");
        }

        try
        {
            if (_palettePlus.Available)
            {
                palettePlusJson = await OnFrameworkAsync(() => _palettePlus.TryExportProfileJson()).ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            _log.Debug(ex, "Palette+ IPC failed");
        }

        try
        {
            if (_honorific.Available)
            {
                honorificJson = await OnFrameworkAsync(() => _honorific.TryGetTitleJson()).ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            _log.Debug(ex, "Honorific IPC failed");
        }

        var modRoot = await ResolvePenumbraRootAsync().ConfigureAwait(false);

        var discovered = new Dictionary<string, LocalBlobInfo?>(StringComparer.OrdinalIgnoreCase);
        if (!string.IsNullOrWhiteSpace(modRoot) && Directory.Exists(modRoot))
        {
            foreach (var path in EnumerateSyncshellFiles(modRoot))
            {
                try
                {
                    var info = new FileInfo(path);
                    var sha = Hasher.Sha256File(path);
                    var name = MakeStableBlobName(path, modRoot);
                    discovered[sha] = new LocalBlobInfo
                    {
                        Name = name,
                        Sha256 = sha,
                        Size = info.Length,
                        FullPath = path,
                    };
                }
                catch (Exception ex)
                {
                    _log.Warning(ex, "Failed to index Penumbra asset {Path}", path);
                }
            }
        }

        lock (_appearanceLock)
        {
            _glamourerJson = NormalizeJsonValue(glamourerJson);
            _customizePlusJson = NormalizeJsonValue(customizePlusJson);
            _simpleHeelsJson = NormalizeJsonValue(simpleHeelsJson);
            _palettePlusJson = NormalizeJsonValue(palettePlusJson);
            _honorificJson = NormalizeJsonValue(honorificJson);
            _localBlobs.Clear();
            foreach (var entry in discovered)
            {
                _localBlobs[entry.Key] = entry.Value;
            }
        }

        if (IsRunning)
        {
            _appearanceDebounce.Run(() => PublishIfChangedAsync());
        }
    }

    private static IEnumerable<string> EnumerateSyncshellFiles(string root)
    {
        foreach (var path in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
        {
            string extension;
            try
            {
                extension = Path.GetExtension(path);
            }
            catch
            {
                continue;
            }

            if (string.IsNullOrEmpty(extension) || !SupportedExtensions.Contains(extension))
            {
                continue;
            }

            FileInfo info;
            try
            {
                info = new FileInfo(path);
            }
            catch
            {
                continue;
            }

            if (!info.Exists || info.Length <= 0 || info.Length > MaxSyncshellFileSizeBytes)
            {
                continue;
            }

            yield return info.FullName;
        }
    }

    private static string MakeStableBlobName(string fullPath, string root)
    {
        var relative = Path.GetRelativePath(root, fullPath);
        return relative.Replace('\\', '/').ToLowerInvariant();
    }

    private static string? NormalizeJsonValue(string? json)
        => string.IsNullOrWhiteSpace(json) ? null : json;

    private static string NormalizeHandle(string name, string? world = null)
        => string.IsNullOrWhiteSpace(world)
            ? name.Trim().ToLowerInvariant()
            : $"{name.Trim().ToLowerInvariant()}@{world.Trim().ToLowerInvariant()}";

    private static string NormalizeIncomingHandle(string handle)
    {
        if (string.IsNullOrWhiteSpace(handle))
        {
            return string.Empty;
        }

        var parts = handle.Split('@', 2, StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        return parts.Length == 2
            ? NormalizeHandle(parts[0], parts[1])
            : NormalizeHandle(handle);
    }

    private static string MemberHandle(SyncshellMemberStatus member)
        => NormalizeHandle(member.DisplayName);

    private Task<HashSet<string>> GetVisibleHandlesAsync()
        => OnFrameworkAsync(() =>
        {
            var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                for (var i = 0; i < _objects.Length; i++)
                {
                    var obj = _objects[i];
                    if (obj == null || obj.ObjectKind != ObjectKind.Player)
                    {
                        continue;
                    }

                    var name = obj.Name?.TextValue ?? string.Empty;
                    if (!string.IsNullOrWhiteSpace(name))
                    {
                        // No world decoration to avoid GeneratedSheets dependency
                        set.Add(NormalizeHandle(name, null));
                    }
                }
            }
            catch (Exception ex)
            {
                _log.Warning(ex, "Failed to read visible players");
            }

            return set;
        });

    private async Task StageTempModForMemberAsync(string memberId, AppearanceMeta appearance)
    {
        var root = await EnsureWorkspaceRootAsync().ConfigureAwait(false);
        var modPath = Path.Combine(root, "mods", memberId, "current");
        var filesRoot = Path.Combine(modPath, "files");
        Directory.CreateDirectory(filesRoot);

        File.WriteAllText(Path.Combine(modPath, "meta.json"), "{\"Name\":\"DemiCat Sync\",\"Author\":\"DemiCat\",\"Version\":\"1.0\"}");

        foreach (var blob in appearance.Blobs ?? new List<BlobRef>())
        {
            var rel = blob.Name?.Replace('\\', '/').Trim() ?? blob.Sha256;
            var destination = Path.Combine(filesRoot, rel);
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            var source = _blobStore.GetLocalPath(blob.Sha256);
            if (File.Exists(destination))
            {
                continue;
            }

            try
            {
                if (!TryCreateHardLink(destination, source))
                {
                    File.Copy(source, destination, overwrite: false);
                }
            }
            catch (Exception ex)
            {
                _log.Warning(ex, "Stage copy failed for {Rel}", rel);
            }
        }

        WriteSystemJsonFile(modPath, "glamourer.json", appearance.GlamourerJson);
        WriteSystemJsonFile(modPath, "cplus.json", appearance.CustomizePlusJson);
        WriteSystemJsonFile(modPath, "heels.json", appearance.SimpleHeelsJson);
        WriteSystemJsonFile(modPath, "palette.json", appearance.PalettePlusJson);
        WriteSystemJsonFile(modPath, "honorific.json", appearance.HonorificJson);
    }

    private void WriteSystemJsonFile(string modPath, string fileName, string? json)
    {
        var path = Path.Combine(modPath, fileName);

        try
        {
            if (string.IsNullOrWhiteSpace(json))
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                }

                return;
            }

            File.WriteAllText(path, json);
        }
        catch (Exception ex)
        {
            _log.Warning(ex, "Failed to stage {File} for SyncShell", fileName);
        }
    }

    private async Task<string?> ReadSystemJsonAsync(string memberId, string fileName)
    {
        var root = await EnsureWorkspaceRootAsync().ConfigureAwait(false);
        var path = Path.Combine(root, "mods", memberId, "current", fileName);
        if (!File.Exists(path))
        {
            return null;
        }

        var json = File.ReadAllText(path);
        return string.IsNullOrWhiteSpace(json) ? null : json;
    }

    private async Task<string> EnsureWorkspaceRootAsync()
    {
        var root = await ResolvePenumbraRootAsync().ConfigureAwait(false);

        if (string.IsNullOrWhiteSpace(root))
        {
            throw new InvalidOperationException("Penumbra Mods root not found");
        }

        var dcRoot = Path.Combine(root, "DemiCat.Sync");
        Directory.CreateDirectory(Path.Combine(dcRoot, "cache", "blobs"));
        Directory.CreateDirectory(Path.Combine(dcRoot, "mods"));
        return dcRoot;
    }

    private async Task ActivateTempModAsync(string memberId)
    {
        var root = await EnsureWorkspaceRootAsync().ConfigureAwait(false);
        var modPath = Path.Combine(root, "mods", memberId, "current");
        await OnFrameworkAsync(() => _penumbra.SetTemporaryMod(modPath)).ConfigureAwait(false);
    }

    private async Task ApplyGlamourerAsync(string memberId)
    {
        if (!_glamourer.CanApply)
        {
            return;
        }

        try
        {
            var json = await ReadSystemJsonAsync(memberId, "glamourer.json").ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(json))
            {
                return;
            }

            await OnFrameworkAsync(() => _glamourer.ApplyPlayerDesignJson(json!)).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _log.Warning(ex, "ApplyGlamourer failed for {Member}", memberId);
        }
    }

    private async Task ApplyCustomizePlusAsync(string memberId)
    {
        if (!_customizePlus.Available)
        {
            return;
        }

        try
        {
            var json = await ReadSystemJsonAsync(memberId, "cplus.json").ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(json))
            {
                return;
            }

            await OnFrameworkAsync(() => _customizePlus.ApplyProfileJson(json!)).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _log.Warning(ex, "ApplyCustomizePlus failed for {Member}", memberId);
        }
    }

    private async Task ApplySimpleHeelsAsync(string memberId)
    {
        if (!_simpleHeels.Available)
        {
            return;
        }

        try
        {
            var json = await ReadSystemJsonAsync(memberId, "heels.json").ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(json))
            {
                return;
            }

            await OnFrameworkAsync(() => _simpleHeels.ApplyOffsetsJson(json!)).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _log.Warning(ex, "ApplySimpleHeels failed for {Member}", memberId);
        }
    }

    private async Task ApplyPalettePlusAsync(string memberId)
    {
        if (!_palettePlus.Available)
        {
            return;
        }

        try
        {
            var json = await ReadSystemJsonAsync(memberId, "palette.json").ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(json))
            {
                return;
            }

            await OnFrameworkAsync(() => _palettePlus.ApplyProfileJson(json!)).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _log.Warning(ex, "ApplyPalettePlus failed for {Member}", memberId);
        }
    }

    private async Task ApplyHonorificAsync(string memberId)
    {
        if (!_honorific.Available)
        {
            return;
        }

        try
        {
            var json = await ReadSystemJsonAsync(memberId, "honorific.json").ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(json))
            {
                return;
            }

            await OnFrameworkAsync(() => _honorific.SetTitleJson(json!)).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _log.Warning(ex, "ApplyHonorific failed for {Member}", memberId);
        }
    }

    private static string ComputeAppearanceHash(AppearanceMeta appearance)
    {
        using var sha = SHA256.Create();

        void Add(string? value)
        {
            var bytes = Encoding.UTF8.GetBytes(value ?? string.Empty);
            sha.TransformBlock(bytes, 0, bytes.Length, bytes, 0);
        }

        Add(appearance.GlamourerJson);
        Add(appearance.CustomizePlusJson);
        Add(appearance.SimpleHeelsJson);
        Add(appearance.PalettePlusJson);
        Add(appearance.HonorificJson);

        foreach (var blob in (appearance.Blobs ?? new List<BlobRef>()).OrderBy(b => b.Sha256, StringComparer.OrdinalIgnoreCase))
        {
            Add($"{blob.Sha256}:{blob.Size}");
        }

        sha.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
        return Convert.ToHexString(sha.Hash ?? Array.Empty<byte>());
    }

    private TargetInfo GetOrCreateTarget(string id)
    {
        lock (_applyStateLock)
        {
            if (_targets.TryGetValue(id, out var info))
            {
                return info;
            }

            info = new TargetInfo { Id = id };
            _targets[id] = info;
            return info;
        }
    }

    private void UpdateMemberSnapshot(MembershipsResponseDto snapshot)
    {
        var members = new List<SyncshellMemberStatus>();
        var active = new List<SyncshellMemberStatus>();

        if (snapshot.Members != null)
        {
            foreach (var entry in snapshot.Members)
            {
                if (entry == null)
                {
                    continue;
                }

                members.Add(MapMember(entry, GetLocalSyncedAt(entry.Id)));
            }
        }

        if (snapshot.CurrentlySynced != null)
        {
            foreach (var entry in snapshot.CurrentlySynced)
            {
                if (entry == null)
                {
                    continue;
                }

                active.Add(MapMember(entry, GetLocalSyncedAt(entry.Id)));
            }
        }

        lock (_memberLock)
        {
            _members = members;
            _activeMembers = active;
        }

        var statusChanged = false;
        if (!_paused)
        {
            statusChanged = UpdateStatusForMembers(active.Count);
        }

        if (!statusChanged)
        {
            OnStatusChanged();
        }
    }

    private bool UpdateStatusForMembers(int activeCount)
    {
        var status = FormatStatusForMembers(activeCount);
        if (!string.Equals(Status, status, StringComparison.Ordinal))
        {
            Status = status;
            return true;
        }

        return false;
    }

    private static SyncshellMemberStatus MapMember(MembershipEntryDto entry, DateTimeOffset? localSyncedAt)
    {
        var displayName = string.IsNullOrWhiteSpace(entry.DisplayName)
            ? entry.Id ?? string.Empty
            : entry.DisplayName;

        return new SyncshellMemberStatus
        {
            Id = entry.Id ?? string.Empty,
            DisplayName = displayName,
            Presence = entry.Presence ?? "offline",
            SyncStatus = entry.SyncStatus,
            LastSeen = ParseTimestamp(entry.LastSeen),
            SyncedAt = localSyncedAt ?? ParseTimestamp(entry.SyncedAt),
            TokenLinked = entry.TokenLinked,
        };
    }

    private DateTimeOffset? GetLocalSyncedAt(string? memberId)
    {
        if (string.IsNullOrWhiteSpace(memberId))
        {
            return null;
        }

        lock (_applyStateLock)
        {
            return _localSyncedAt.TryGetValue(memberId, out var value) ? value : null;
        }
    }

    private SyncshellMemberStatus? FindMember(string memberId)
    {
        if (string.IsNullOrWhiteSpace(memberId))
        {
            return null;
        }

        lock (_memberLock)
        {
            return _members.FirstOrDefault(m => string.Equals(m.Id, memberId, StringComparison.OrdinalIgnoreCase));
        }
    }

    private SyncshellMemberStatus? ResolveMemberFromHandle(string handleOrId)
    {
        if (string.IsNullOrWhiteSpace(handleOrId))
        {
            return null;
        }

        var trimmed = handleOrId.Trim();
        var normalizedHandle = NormalizeIncomingHandle(trimmed);

        lock (_memberLock)
        {
            var byId = _members.FirstOrDefault(m => string.Equals(m.Id, trimmed, StringComparison.OrdinalIgnoreCase));
            if (byId != null)
            {
                return byId;
            }

            return _members.FirstOrDefault(m => string.Equals(MemberHandle(m), normalizedHandle, StringComparison.OrdinalIgnoreCase));
        }
    }

    private void ApplyLocalSyncedAt(string targetId, DateTimeOffset timestamp)
    {
        lock (_memberLock)
        {
            _members = _members.Select(member => UpdateSyncedAtEntry(member, targetId, timestamp)).ToList();
            _activeMembers = _activeMembers.Select(member => UpdateSyncedAtEntry(member, targetId, timestamp)).ToList();
        }

        lock (_applyStateLock)
        {
            _localSyncedAt[targetId] = timestamp;
        }

        OnStatusChanged();
    }

    private static SyncshellMemberStatus UpdateSyncedAtEntry(SyncshellMemberStatus member, string targetId, DateTimeOffset timestamp)
    {
        if (!string.Equals(member.Id, targetId, StringComparison.OrdinalIgnoreCase))
        {
            return member;
        }

        return new SyncshellMemberStatus
        {
            Id = member.Id,
            DisplayName = member.DisplayName,
            Presence = member.Presence,
            SyncStatus = member.SyncStatus,
            LastSeen = member.LastSeen,
            SyncedAt = timestamp,
            TokenLinked = member.TokenLinked,
        };
    }

    private static bool TryCreateHardLink(string destination, string source)
    {
        if (!File.Exists(source))
        {
            return false;
        }

        try
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                return CreateHardLinkWindows(destination, source, IntPtr.Zero);
            }

            return link(source, destination) == 0;
        }
        catch
        {
            return false;
        }
    }

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CreateHardLinkWindows(string lpFileName, string lpExistingFileName, IntPtr lpSecurityAttributes);

    [DllImport("libc", SetLastError = true)]
    private static extern int link(string oldpath, string newpath);

    public static string FormatStatusForMembers(int activeCount)
        => activeCount > 0 ? $"Syncing {activeCount} member{(activeCount == 1 ? string.Empty : "s")}" : "Idle";

    public static DateTimeOffset? ParseTimestamp(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        if (DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsed))
        {
            return parsed;
        }

        return null;
    }

    private void OnStatusChanged()
        => StatusChanged?.Invoke(this, EventArgs.Empty);

    private sealed class LocalBlobInfo
    {
        public string Name { get; init; } = string.Empty;
        public string FullPath { get; init; } = string.Empty;
        public string Sha256 { get; init; } = string.Empty;
        public long Size { get; init; }
    }

    private sealed class TargetInfo
    {
        public string Id { get; init; } = string.Empty;
        public string LastHash { get; set; } = string.Empty;
        public DateTimeOffset LastAppliedAt { get; set; }
        public SyncshellTargetStage Stage { get; set; } = SyncshellTargetStage.Unknown;
    }
}
