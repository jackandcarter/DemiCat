using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace DemiCatPlugin.SyncShell;

public interface ISyncShellService
{
    event EventHandler? StatusChanged;

    bool IsRunning { get; }
    bool IsPaused { get; }
    string Status { get; }
    int NearbyUserCount { get; }
    IReadOnlyList<SyncshellMemberStatus> Members { get; }
    IReadOnlyList<SyncshellMemberStatus> ActiveMembers { get; }
    bool PenumbraAvailable { get; }
    string? DetectedPenumbraPath { get; }

    SyncshellTargetStage GetStage(string memberId);

    Task Start(CancellationToken cancellationToken = default);
    Task Stop(CancellationToken cancellationToken = default);
    Task TriggerPublishAsync(CancellationToken cancellationToken = default);
    Task ResyncAllAsync(CancellationToken cancellationToken = default);
    void Pause();
    void Resume();
    void ClearCache();
    Task EnforceCacheLimitAsync(CancellationToken cancellationToken = default);
    bool TryValidatePenumbraPath(string? path, out string? error);
}
