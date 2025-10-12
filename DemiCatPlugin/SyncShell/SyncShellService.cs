using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Dalamud.Plugin.Services;

namespace DemiCatPlugin.SyncShell;

public sealed class SyncShellService : ISyncShellService, IDisposable
{
    private static readonly TimeSpan PresencePollInterval = TimeSpan.FromSeconds(12);
    private static readonly TimeSpan PublishCooldown = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan TerritoryDebounce = TimeSpan.FromSeconds(1);

    private readonly Config _config;
    private readonly TokenManager _tokenManager;
    private readonly SyncShellClient _client;
    private readonly BlobStore _blobStore;
    private readonly PenumbraIpc _penumbra;
    private readonly IPluginLog _log;
    private readonly IClientState _clientState;
    private readonly IFramework _framework;
    private readonly object _statusLock = new();
    private readonly SemaphoreSlim _lifecycleLock = new(1, 1);
    private readonly HashSet<string> _nearbyUsers = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _memberLock = new();
    private List<SyncshellMemberStatus> _members = new();
    private List<SyncshellMemberStatus> _activeMembers = new();

    private CancellationTokenSource? _runCts;
    private Task? _presenceTask;
    private Task? _publishTask;
    private bool _disposed;
    private DateTimeOffset _lastPublish;
    private string _status = "SyncShell disabled";
    private bool _paused;
    private bool _validationFailed;

    public SyncShellService(
        Config config,
        TokenManager tokenManager,
        SyncShellClient client,
        BlobStore blobStore,
        PenumbraIpc penumbra,
        IPluginLog log,
        IClientState clientState,
        IFramework framework)
    {
        _config = config ?? throw new ArgumentNullException(nameof(config));
        _tokenManager = tokenManager ?? throw new ArgumentNullException(nameof(tokenManager));
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _blobStore = blobStore ?? throw new ArgumentNullException(nameof(blobStore));
        _penumbra = penumbra ?? throw new ArgumentNullException(nameof(penumbra));
        _log = log ?? throw new ArgumentNullException(nameof(log));
        _clientState = clientState ?? throw new ArgumentNullException(nameof(clientState));
        _framework = framework ?? throw new ArgumentNullException(nameof(framework));

        _tokenManager.OnLinked += HandleTokenLinked;
        _tokenManager.OnUnlinked += HandleTokenUnlinked;
        _clientState.TerritoryChanged += HandleTerritoryChanged;
    }

    public event EventHandler? StatusChanged;

    public bool IsRunning => _runCts != null;

    public bool PenumbraAvailable => _penumbra.Available;

    public string? DetectedPenumbraPath => _penumbra.Available ? _penumbra.GetModDirectory() : BlobStore.GuessDefaultPenumbraRoot();

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
                return _members.ToArray();
            }
        }
    }

    public IReadOnlyList<SyncshellMemberStatus> ActiveMembers
    {
        get
        {
            lock (_memberLock)
            {
                return _activeMembers.ToArray();
            }
        }
    }

    public Task Start() => Start(CancellationToken.None);

    public async Task Start(CancellationToken cancellationToken)
    {
        await _lifecycleLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_runCts != null)
            {
                return;
            }

            if (!_config.EnableSyncShell)
            {
                Status = "SyncShell disabled";
                return;
            }

            if (_tokenManager.State != LinkState.Linked)
            {
                Status = "Not linked";
                return;
            }

            if (!ApiHelpers.ValidateApiBaseUrl(_config))
            {
                Status = "SyncShell disabled";
                return;
            }

            _validationFailed = false;
            using var validateCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            validateCts.CancelAfter(TimeSpan.FromSeconds(10));
            if (!await _client.ValidateAsync(validateCts.Token).ConfigureAwait(false))
            {
                _validationFailed = true;
                Status = "Auth invalid—please relink";
                return;
            }

            _runCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            _presenceTask = Task.Run(() => PresenceLoopAsync(_runCts.Token));
            _publishTask = Task.Run(() => PublishLoopAsync(_runCts.Token));
            Status = "Active";
        }
        finally
        {
            _lifecycleLock.Release();
        }
    }

    public Task Stop() => Stop(CancellationToken.None);

    public async Task Stop(CancellationToken cancellationToken)
    {
        await _lifecycleLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_runCts == null)
            {
                return;
            }

            _runCts.Cancel();
            try
            {
                if (_presenceTask != null)
                {
                    await _presenceTask.ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException)
            {
            }

            try
            {
                if (_publishTask != null)
                {
                    await _publishTask.ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException)
            {
            }

            _presenceTask = null;
            _publishTask = null;
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

    public void Pause()
    {
        IsPaused = true;
    }

    public void Resume()
    {
        IsPaused = false;
    }

    public void ClearCache()
        => _blobStore.Clear();

    public Task EnforceCacheLimitAsync(CancellationToken cancellationToken = default)
    {
        var limit = Math.Max(256, _config.SyncshellCacheLimitMb) * 1024L * 1024L;
        return _blobStore.EnforceLimitAsync(limit, cancellationToken);
    }

    public Task ResyncAllAsync(CancellationToken cancellationToken = default)
        => TriggerPublishAsync(cancellationToken);

    internal void UpdateLocalAppearance(IEnumerable<LocalBlobInfo> blobs, string? glamourerJson)
    {
        lock (_appearanceLock)
        {
            _localBlobs.Clear();
            if (blobs != null)
            {
                foreach (var blob in blobs)
                {
                    if (blob == null)
                    {
                        continue;
                    }

                    if (string.IsNullOrWhiteSpace(blob.Sha256) || string.IsNullOrWhiteSpace(blob.FullPath))
                    {
                        continue;
                    }

                    _localBlobs[blob.Sha256] = blob;
                }
            }

            _glamourerJson = string.IsNullOrWhiteSpace(glamourerJson) ? null : glamourerJson;
        }
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

                try
                {
                    var activeIds = ActiveMembers
                        .Select(m => m.Id)
                        .Where(id => !string.IsNullOrWhiteSpace(id))
                        .ToArray();
                    if (activeIds.Length > 0)
                    {
                        await _client.UpdatePresenceAsync(activeIds, token).ConfigureAwait(false);
                    }
                }
                catch (Exception ex)
                {
                    _log.Warning(ex, "Failed to update presence");
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

        PublishPayload payload;
        Dictionary<string, LocalBlobInfo> localBlobs;
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

    private (PublishPayload Payload, Dictionary<string, LocalBlobInfo> LocalBlobs) BuildPublishPayload()
    {
        var appearance = new AppearanceMeta
        {
            ActorHash = _clientState.LocalPlayer?.Name.TextValue ?? string.Empty,
        };

        Dictionary<string, LocalBlobInfo> localBlobs;
        lock (_appearanceLock)
        {
            appearance.GlamourerJson = _glamourerJson;
            localBlobs = _localBlobs.ToDictionary(kvp => kvp.Key, kvp => kvp.Value, StringComparer.OrdinalIgnoreCase);
        }

        foreach (var blob in localBlobs.Values.OrderBy(b => b.Name, StringComparer.OrdinalIgnoreCase))
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
        IReadOnlyDictionary<string, LocalBlobInfo> localBlobs,
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

            if (!localBlobs.TryGetValue(hash, out var blob))
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
                await TriggerPublishAsync(_runCts?.Token ?? CancellationToken.None).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
        });
    }

    private void HandleTokenLinked()
    {
        if (_config.EnableSyncShell)
        {
            _ = Start();
        }
        else
        {
            Status = "SyncShell disabled";
        }
    }

    private void HandleTokenUnlinked(string? reason)
    {
        _ = Stop();
    }

    private void OnStatusChanged()
        => StatusChanged?.Invoke(this, EventArgs.Empty);

    private void UpdateMemberSnapshot(MembershipsResponseDto snapshot)
    {
        var members = new List<SyncshellMemberStatus>();
        var activeMembers = new List<SyncshellMemberStatus>();

        if (snapshot.Members != null)
        {
            foreach (var entry in snapshot.Members)
            {
                if (entry == null)
                {
                    continue;
                }

                var status = MapMember(entry);
                members.Add(status);
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

                var status = MapMember(entry);
                activeMembers.Add(status);
            }
        }

        lock (_memberLock)
        {
            _members = members;
            _activeMembers = activeMembers;
        }

        lock (_nearbyUsers)
        {
            _nearbyUsers.Clear();
            foreach (var entry in activeMembers)
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
            statusChanged = UpdateStatusForMembers(activeMembers.Count);
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

    internal static string FormatStatusForMembers(int activeCount)
        => activeCount > 0
            ? $"Syncing {activeCount} member{(activeCount == 1 ? string.Empty : "s")}" : "Idle";

    private static SyncshellMemberStatus MapMember(MembershipEntryDto entry)
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
            SyncedAt = ParseTimestamp(entry.SyncedAt),
            TokenLinked = entry.TokenLinked,
        };
    }

    internal static DateTimeOffset? ParseTimestamp(string? value)
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

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _tokenManager.OnLinked -= HandleTokenLinked;
        _tokenManager.OnUnlinked -= HandleTokenUnlinked;
        _clientState.TerritoryChanged -= HandleTerritoryChanged;

        try
        {
            _ = Stop();
        }
        catch
        {
        }

        _lifecycleLock.Dispose();
    }

    internal readonly record struct LocalBlobInfo
    {
        public LocalBlobInfo(string name, string sha256, long size, string fullPath)
        {
            Name = name ?? string.Empty;
            Sha256 = sha256 ?? string.Empty;
            Size = size;
            FullPath = fullPath ?? string.Empty;
        }

        public string Name { get; }

        public string Sha256 { get; }

        public long Size { get; }

        public string FullPath { get; }
    }
}
