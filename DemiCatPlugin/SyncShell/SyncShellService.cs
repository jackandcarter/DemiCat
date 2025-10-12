using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Dalamud.Game.ClientState;
using Dalamud.Game.ClientState.Objects;
using Dalamud.Game.ClientState.Objects.Enums;
using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Plugin.Services;

namespace DemiCatPlugin.SyncShell;

public sealed class SyncShellService : ISyncShellService, IDisposable
{
    private static readonly TimeSpan PresencePollInterval = TimeSpan.FromSeconds(12);
    private static readonly TimeSpan PublishCooldown = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan TerritoryDebounce = TimeSpan.FromSeconds(1);
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
    private readonly IPluginLog _log;
    private readonly IClientState _clientState;
    private readonly IFramework _framework;
    private readonly IObjectTable _objects;

    private readonly object _statusLock = new();
    private readonly SemaphoreSlim _lifecycleLock = new(1, 1);
    private readonly object _memberLock = new();
    private readonly object _appearanceLock = new();
    private readonly object _applyStateLock = new();

    private readonly Dictionary<string, LocalBlobInfo?> _localBlobs = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, TargetInfo> _targets = new(StringComparer.Ordinal);
    private readonly Dictionary<string, DateTimeOffset> _localSyncedAt = new(StringComparer.OrdinalIgnoreCase);

    private readonly HashSet<string> _nearbyUsers = new(StringComparer.OrdinalIgnoreCase);

    private readonly SemaphoreSlim _redrawGate = new(1, 1);

    private Channel<string> _prefetchQueue = Channel.CreateUnbounded<string>();
    private Channel<string> _applyQueue = Channel.CreateUnbounded<string>();

    private CancellationTokenSource? _runCts;
    private Task? _presenceTask;
    private Task? _publishTask;
    private Task? _prefetchTask;
    private Task? _applyTask;
    private bool _disposed;
    private bool _paused;
    private bool _validationFailed;
    private string _status = "SyncShell disabled";
    private string? _glamourerJson;
    private DateTimeOffset _lastPublish;
    private DateTimeOffset _lastRedrawAt = DateTimeOffset.MinValue;

    private List<SyncshellMemberStatus> _members = new();
    private List<SyncshellMemberStatus> _activeMembers = new();

    public SyncShellService(
        Config config,
        TokenManager tokenManager,
        SyncShellClient client,
        BlobStore blobStore,
        PenumbraIpc penumbra,
        GlamourerIpc glamourer,
        IPluginLog log,
        IClientState clientState,
        IFramework framework,
        IObjectTable objects)
    {
        _config = config ?? throw new ArgumentNullException(nameof(config));
        _tokenManager = tokenManager ?? throw new ArgumentNullException(nameof(tokenManager));
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _blobStore = blobStore ?? throw new ArgumentNullException(nameof(blobStore));
        _penumbra = penumbra ?? throw new ArgumentNullException(nameof(penumbra));
        _glamourer = glamourer ?? throw new ArgumentNullException(nameof(glamourer));
        _log = log ?? throw new ArgumentNullException(nameof(log));
        _clientState = clientState ?? throw new ArgumentNullException(nameof(clientState));
        _framework = framework ?? throw new ArgumentNullException(nameof(framework));
        _objects = objects ?? throw new ArgumentNullException(nameof(objects));

        _tokenManager.OnLinked += HandleTokenLinked;
        _tokenManager.OnUnlinked += HandleTokenUnlinked;
        _clientState.TerritoryChanged += HandleTerritoryChanged;
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
            lock (_nearbyUsers)
            {
                return _nearbyUsers.Count;
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

    public string? DetectedPenumbraPath => _penumbra.Available
        ? _penumbra.GetModDirectory()
        : BlobStore.GuessDefaultPenumbraRoot();

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
            Status = "Initializing…";
            RefreshAppearanceCaches();

            _prefetchQueue = Channel.CreateUnbounded<string>();
            _applyQueue = Channel.CreateUnbounded<string>();

            _presenceTask = Task.Run(() => PresenceLoopAsync(_runCts.Token), CancellationToken.None);
            _publishTask = Task.Run(() => PublishLoopAsync(_runCts.Token), CancellationToken.None);
            _prefetchTask = Task.Run(() => PrefetchLoopAsync(_runCts.Token), CancellationToken.None);
            _applyTask = Task.Run(() => ApplyLoopAsync(_runCts.Token), CancellationToken.None);
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
                    _presenceTask ?? Task.CompletedTask,
                    _publishTask ?? Task.CompletedTask,
                    _prefetchTask ?? Task.CompletedTask,
                    _applyTask ?? Task.CompletedTask).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }

            _presenceTask = null;
            _publishTask = null;
            _prefetchTask = null;
            _applyTask = null;

            _runCts.Dispose();
            _runCts = null;
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

            await PublishAsync(_runCts!.Token).ConfigureAwait(false);
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
        _lifecycleLock.Dispose();
        _redrawGate.Dispose();
    }

    private void ThrowIfDisposed()
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

    private void HandleTokenUnlinked(string? _)
    {
        _ = Stop();
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
                RefreshAppearanceCaches();
                await TriggerPublishAsync(_runCts?.Token ?? CancellationToken.None).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
        });
    }

    private async Task PresenceLoopAsync(CancellationToken token)
    {
        var initial = true;
        while (!token.IsCancellationRequested)
        {
            try
            {
                if (!initial)
                {
                    await Task.Delay(PresencePollInterval, token).ConfigureAwait(false);
                }
                else
                {
                    initial = false;
                }

                if (_paused)
                {
                    continue;
                }

                var memberships = await _client.GetMembershipsAsync(token).ConfigureAwait(false);
                if (memberships != null)
                {
                    UpdateMemberSnapshot(memberships);
                }

                var activeIds = BuildActiveMemberList();
                if (activeIds.Length > 0)
                {
                    await _client.UpdatePresenceAsync(activeIds, token).ConfigureAwait(false);
                }

                foreach (var id in activeIds)
                {
                    var target = GetOrCreateTarget(id);
                    if (target.Stage is SyncshellTargetStage.Unknown or SyncshellTargetStage.Failed)
                    {
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
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _log.Warning(ex, "Presence loop failed");
            }
        }
    }

    private string[] BuildActiveMemberList()
    {
        var roster = Members;
        var eligible = roster.Where(m => m.TokenLinked && string.Equals(m.Presence, "online", StringComparison.OrdinalIgnoreCase));

        if (_config.OnlySyncVisible)
        {
            var visible = GetVisibleHandles();
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

    private async Task PublishLoopAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(5), token).ConfigureAwait(false);
                if (_paused)
                {
                    continue;
                }

                if (DateTimeOffset.UtcNow - _lastPublish < PublishCooldown)
                {
                    continue;
                }

                await PublishAsync(token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _log.Warning(ex, "Publish loop failed");
            }
        }
    }

    private async Task PublishAsync(CancellationToken token)
    {
        _lastPublish = DateTimeOffset.UtcNow;
        if (_tokenManager.State != LinkState.Linked)
        {
            return;
        }

        RefreshAppearanceCaches();
        PublishPayload payload;
        Dictionary<string, LocalBlobInfo?> localBlobs;
        try
        {
            (payload, localBlobs) = BuildPublishPayload();
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

    private (PublishPayload Payload, Dictionary<string, LocalBlobInfo?> LocalBlobs) BuildPublishPayload()
    {
        var actorHash = string.Empty;
        var player = _clientState.LocalPlayer;
        if (player != null)
        {
            actorHash = player.Name.TextValue ?? string.Empty;
            // Omit world label to avoid GeneratedSheets dependency; actorHash is just the name.
        }

        var appearance = new AppearanceMeta
        {
            ActorHash = actorHash,
        };

        Dictionary<string, LocalBlobInfo?> localBlobs;
        lock (_appearanceLock)
        {
            appearance.GlamourerJson = _glamourerJson;
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

                StageTempModForMember(memberId, meta.Appearance);
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

                    StageTempModForMember(memberId, meta.Appearance);
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
                var visible = GetVisibleHandles();
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
                ActivateTempMod(memberId);
                ApplyGlamourer(memberId);
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

            _penumbra.RedrawObject(_clientState.LocalPlayer?.ObjectIndex ?? 0);
            _lastRedrawAt = DateTimeOffset.UtcNow;
        }
        finally
        {
            _redrawGate.Release();
        }
    }

    private void RefreshAppearanceCaches()
    {
        string? glamourerJson = null;
        try
        {
            if (_glamourer.Available)
            {
                glamourerJson = _glamourer.TryGetPlayerDesignJson();
            }
        }
        catch (Exception ex)
        {
            _log.Warning(ex, "Glamourer IPC failed");
        }

        var modRoot = _penumbra.GetModDirectory();
        if (string.IsNullOrWhiteSpace(modRoot))
        {
            modRoot = _config.PenumbraPathOverride;
        }

        if (string.IsNullOrWhiteSpace(modRoot))
        {
            modRoot = BlobStore.GuessDefaultPenumbraRoot();
        }

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
            _glamourerJson = string.IsNullOrWhiteSpace(glamourerJson) ? null : glamourerJson;
            _localBlobs.Clear();
            foreach (var entry in discovered)
            {
                _localBlobs[entry.Key] = entry.Value;
            }
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

    private static string NormalizeHandle(string name, string? world = null)
        => string.IsNullOrWhiteSpace(world)
            ? name.Trim().ToLowerInvariant()
            : $"{name.Trim().ToLowerInvariant()}@{world.Trim().ToLowerInvariant()}";

    private static string MemberHandle(SyncshellMemberStatus member)
        => NormalizeHandle(member.DisplayName);

    private HashSet<string> GetVisibleHandles()
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            for (var i = 0; i < _objects.Length; i++)
            {
                var obj = _objects[i];
                if (obj is not PlayerCharacter player)
                {
                    continue;
                }

                var name = player.Name?.TextValue ?? string.Empty;
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
    }

    private void StageTempModForMember(string memberId, AppearanceMeta appearance)
    {
        var root = EnsureWorkspaceRoot();
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

        if (!string.IsNullOrWhiteSpace(appearance.GlamourerJson))
        {
            File.WriteAllText(Path.Combine(modPath, "glamourer.json"), appearance.GlamourerJson);
        }
    }

    private string EnsureWorkspaceRoot()
    {
        var root = _penumbra.GetModDirectory() ?? _config.PenumbraPathOverride;
        if (string.IsNullOrWhiteSpace(root))
        {
            root = BlobStore.GuessDefaultPenumbraRoot();
        }

        if (string.IsNullOrWhiteSpace(root))
        {
            throw new InvalidOperationException("Penumbra Mods root not found");
        }

        var dcRoot = Path.Combine(root, "DemiCat.Sync");
        Directory.CreateDirectory(Path.Combine(dcRoot, "cache", "blobs"));
        Directory.CreateDirectory(Path.Combine(dcRoot, "mods"));
        return dcRoot;
    }

    private void ActivateTempMod(string memberId)
    {
        var root = EnsureWorkspaceRoot();
        var modPath = Path.Combine(root, "mods", memberId, "current");
        _penumbra.SetTemporaryMod(modPath);
    }

    private void ApplyGlamourer(string memberId)
    {
        try
        {
            var root = EnsureWorkspaceRoot();
            var path = Path.Combine(root, "mods", memberId, "current", "glamourer.json");
            if (!File.Exists(path))
            {
                return;
            }

            var json = File.ReadAllText(path);
            if (string.IsNullOrWhiteSpace(json) || !_glamourer.Available)
            {
                return;
            }

            _ = _glamourer.TryGetPlayerDesignJson();
        }
        catch (Exception ex)
        {
            _log.Warning(ex, "ApplyGlamourer failed for {Member}", memberId);
        }
    }

    private static string ComputeAppearanceHash(AppearanceMeta appearance)
    {
        var builder = new StringBuilder();
        builder.Append(appearance.GlamourerJson ?? string.Empty);
        foreach (var blob in (appearance.Blobs ?? new List<BlobRef>()).OrderBy(b => b.Sha256, StringComparer.Ordinal))
        {
            builder.Append(blob.Sha256);
        }

        return Hasher.Sha256String(builder.ToString());
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

        lock (_nearbyUsers)
        {
            _nearbyUsers.Clear();
            foreach (var entry in active)
            {
                if (!string.IsNullOrEmpty(entry.Id))
                {
                    _nearbyUsers.Add(entry.Id);
                }
            }
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

#if NET8_0_OR_GREATER
        try
        {
            System.IO.File.CreateHardLink(destination, source);
            return true;
        }
        catch
        {
            return false;
        }
#else
        return false;
#endif
    }

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
