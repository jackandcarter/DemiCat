using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Dalamud.Plugin.Services;

namespace DemiCatPlugin.SyncShell;

public sealed class SyncShellService : ISyncShellService, IDisposable
{
    private static readonly TimeSpan PresencePollInterval = TimeSpan.FromSeconds(12);
    private static readonly TimeSpan PublishCooldown = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan TerritoryDebounce = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan ApplyPollInterval = TimeSpan.FromSeconds(6);
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
    private readonly object _statusLock = new();
    private readonly SemaphoreSlim _lifecycleLock = new(1, 1);
    private readonly HashSet<string> _nearbyUsers = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _memberLock = new();
    private List<SyncshellMemberStatus> _members = new();
    private List<SyncshellMemberStatus> _activeMembers = new();
    private readonly object _appearanceLock = new();
    private readonly Dictionary<string, LocalBlobInfo> _localBlobs = new(StringComparer.OrdinalIgnoreCase);
    private string? _glamourerJson;
    private readonly object _applyStateLock = new();
    private readonly Dictionary<string, AppliedAppearanceState> _appliedTargets = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, DateTimeOffset> _localSyncedAt = new(StringComparer.OrdinalIgnoreCase);

    private CancellationTokenSource? _runCts;
    private Task? _presenceTask;
    private Task? _publishTask;
    private Task? _applyTask;
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
        GlamourerIpc glamourer,
        IPluginLog log,
        IClientState clientState,
        IFramework framework)
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

        var discovered = new Dictionary<string, LocalBlobInfo>(StringComparer.OrdinalIgnoreCase);
        if (!string.IsNullOrWhiteSpace(modRoot) && Directory.Exists(modRoot))
        {
            foreach (var path in EnumerateSyncshellFiles(modRoot))
            {
                try
                {
                    var info = new FileInfo(path);
                    var sha = Hasher.Sha256File(path);
                    var name = MakeStableBlobName(path, modRoot);
                    discovered[sha] = new LocalBlobInfo(name, sha, info.Length, path);
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

    private async Task ApplyLoopAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(ApplyPollInterval, token).ConfigureAwait(false);

                if (_paused)
                {
                    continue;
                }

                var targets = GetApplyTargets();
                foreach (var target in targets)
                {
                    token.ThrowIfCancellationRequested();

                    var meta = await _client.GetLatestMetaAsync(target, token).ConfigureAwait(false);
                    if (meta?.Appearance == null)
                    {
                        continue;
                    }

                    if (!ShouldApply(target, meta))
                    {
                        continue;
                    }

                    var localPaths = await EnsureBlobsLocalAsync(meta.Appearance, token).ConfigureAwait(false);
                    if (localPaths == null)
                    {
                        continue;
                    }

                    var workspace = await MaterializeTemporaryModAsync(target, meta.Appearance, localPaths, token).ConfigureAwait(false);
                    _penumbra.SetTemporaryMod(workspace);

                    var objectIndex = _clientState.LocalPlayer?.ObjectIndex ?? -1;
                    if (objectIndex >= 0)
                    {
                        _penumbra.RedrawObject(objectIndex);
                    }

                    var appliedAt = DateTimeOffset.UtcNow;
                    var hash = string.IsNullOrWhiteSpace(meta.Hash)
                        ? ComputeAppearanceHash(meta.Appearance)
                        : meta.Hash!;

                    lock (_applyStateLock)
                    {
                        _appliedTargets[target] = new AppliedAppearanceState(hash, appliedAt);
                        _localSyncedAt[target] = appliedAt;
                    }

                    ApplyLocalSyncedAt(target, appliedAt);
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _log.Warning(ex, "Apply loop failed");
            }
        }
    }

    private List<string> GetApplyTargets()
    {
        var results = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var selfId = _tokenManager.Token;

        List<SyncshellMemberStatus> members;
        lock (_memberLock)
        {
            members = _members.ToList();
        }

        if (_config.SyncshellAutoMode)
        {
            foreach (var member in members)
            {
                if (string.IsNullOrWhiteSpace(member.Id) || string.Equals(member.Id, selfId, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (!member.TokenLinked)
                {
                    continue;
                }

                if (string.Equals(member.Presence, "offline", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (seen.Add(member.Id))
                {
                    results.Add(member.Id);
                }
            }
        }
        else
        {
            var manualList = _config.SyncshellManualAllowList.ToArray();
            foreach (var id in manualList)
            {
                var idString = id.ToString(CultureInfo.InvariantCulture);
                if (string.IsNullOrWhiteSpace(idString) || string.Equals(idString, selfId, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (!seen.Add(idString))
                {
                    continue;
                }

                var member = members.FirstOrDefault(m => string.Equals(m.Id, idString, StringComparison.OrdinalIgnoreCase));
                if (member == null || !member.TokenLinked)
                {
                    continue;
                }

                results.Add(idString);
            }
        }

        return results;
    }

    internal IReadOnlyList<string> GetApplyTargetsForTesting()
        => GetApplyTargets();

    private bool ShouldApply(string target, UserMetaDto meta)
    {
        if (meta.Appearance == null)
        {
            return false;
        }

        var hash = string.IsNullOrWhiteSpace(meta.Hash)
            ? ComputeAppearanceHash(meta.Appearance)
            : meta.Hash!;

        lock (_applyStateLock)
        {
            if (_appliedTargets.TryGetValue(target, out var state) &&
                string.Equals(state.Hash, hash, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }

        return true;
    }

    private async Task<Dictionary<string, string>?> EnsureBlobsLocalAsync(AppearanceMeta appearance, CancellationToken token)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var blob in appearance.Blobs)
        {
            if (string.IsNullOrWhiteSpace(blob.Sha256))
            {
                continue;
            }

            var localPath = await _blobStore.EnsureLocalAsync(blob.Sha256, () => _client.DownloadBlobAsync(blob.Sha256, token), token).ConfigureAwait(false);
            if (string.IsNullOrEmpty(localPath))
            {
                _log.Warning("Failed to ensure local blob {Hash}", blob.Sha256);
                return null;
            }

            result[blob.Sha256] = localPath;
        }

        return result;
    }

    private async Task<string> MaterializeTemporaryModAsync(string targetId, AppearanceMeta appearance, IReadOnlyDictionary<string, string> localPaths, CancellationToken token)
    {
        var workspace = _blobStore.GetWorkspacePath($"apply-{targetId}");
        try
        {
            if (Directory.Exists(workspace))
            {
                Directory.Delete(workspace, true);
            }
        }
        catch (Exception ex)
        {
            _log.Warning(ex, "Failed to reset temporary workspace {Workspace}", workspace);
        }

        Directory.CreateDirectory(workspace);

        foreach (var blob in appearance.Blobs)
        {
            if (string.IsNullOrWhiteSpace(blob.Name))
            {
                continue;
            }

            if (!localPaths.TryGetValue(blob.Sha256, out var sourcePath) || string.IsNullOrWhiteSpace(sourcePath))
            {
                continue;
            }

            var destination = Path.Combine(workspace, blob.Name.Replace('/', Path.DirectorySeparatorChar));
            var directory = Path.GetDirectoryName(destination);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            await CopyFileAsync(sourcePath, destination, token).ConfigureAwait(false);
        }

        return workspace;
    }

    private static async Task CopyFileAsync(string source, string destination, CancellationToken token)
    {
        await using var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, useAsync: true);
        await using var output = new FileStream(destination, FileMode.Create, FileAccess.Write, FileShare.None, 64 * 1024, useAsync: true);
        await input.CopyToAsync(output, 64 * 1024, token).ConfigureAwait(false);
        await output.FlushAsync(token).ConfigureAwait(false);
    }

    private static string ComputeAppearanceHash(AppearanceMeta appearance)
    {
        var builder = new StringBuilder();
        if (!string.IsNullOrEmpty(appearance.ActorHash))
        {
            builder.Append(appearance.ActorHash);
        }

        if (!string.IsNullOrEmpty(appearance.GlamourerJson))
        {
            builder.Append(appearance.GlamourerJson);
        }

        foreach (var blob in appearance.Blobs.OrderBy(b => b.Sha256, StringComparer.OrdinalIgnoreCase))
        {
            builder.Append(blob.Sha256);
            builder.Append(':');
            builder.Append(blob.Size);
            builder.Append(':');
            builder.Append(blob.Name);
            builder.Append(';');
        }

        return Hasher.Sha256Bytes(Encoding.UTF8.GetBytes(builder.ToString()));
    }

    private void ApplyLocalSyncedAt(string targetId, DateTimeOffset timestamp)
    {
        lock (_memberLock)
        {
            _members = _members.Select(member => UpdateSyncedAtEntry(member, targetId, timestamp)).ToList();
            _activeMembers = _activeMembers.Select(member => UpdateSyncedAtEntry(member, targetId, timestamp)).ToList();
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

    private DateTimeOffset? GetLocalSyncedAt(string? memberId)
    {
        if (string.IsNullOrWhiteSpace(memberId))
        {
            return null;
        }

        lock (_applyStateLock)
        {
            return _localSyncedAt.TryGetValue(memberId, out var timestamp) ? timestamp : null;
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

            RefreshAppearanceCaches();
            _runCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            _presenceTask = Task.Run(() => PresenceLoopAsync(_runCts.Token));
            _publishTask = Task.Run(() => PublishLoopAsync(_runCts.Token));
            _applyTask = Task.Run(() => ApplyLoopAsync(_runCts.Token));
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

            try
            {
                if (_applyTask != null)
                {
                    await _applyTask.ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException)
            {
            }

            _presenceTask = null;
            _publishTask = null;
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
                    var targets = GetApplyTargets();
                    if (targets.Count > 0)
                    {
                        await _client.UpdatePresenceAsync(targets, token).ConfigureAwait(false);
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

        RefreshAppearanceCaches();
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
        var actorHash = string.Empty;
        var player = _clientState.LocalPlayer;
        if (player != null)
        {
            actorHash = player.Name.TextValue ?? string.Empty;
            var worldName = player.HomeWorld?.GameData?.Name;
            if (!string.IsNullOrEmpty(worldName))
            {
                actorHash = string.IsNullOrEmpty(actorHash)
                    ? worldName!
                    : $"{actorHash}@{worldName}";
            }
        }

        var appearance = new AppearanceMeta
        {
            ActorHash = actorHash,
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
                RefreshAppearanceCaches();
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
        lock (_applyStateLock)
        {
            _appliedTargets.Clear();
            _localSyncedAt.Clear();
        }
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

                var status = MapMember(entry, GetLocalSyncedAt(entry.Id));
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

                var status = MapMember(entry, GetLocalSyncedAt(entry.Id));
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

    private sealed record AppliedAppearanceState(string Hash, DateTimeOffset Timestamp);
}
