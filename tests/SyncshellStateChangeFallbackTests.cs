using System;
using System.Collections.Generic;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using DemiCatPlugin;
using DemiCatPlugin.SyncShell;
using Xunit;

public class SyncshellStateChangeFallbackTests
{
    [Fact]
    public void StatusHistoryUpdatesWhenServiceRaisesEvent()
    {
        SyncshellWindow.Instance?.Dispose();
        var fakeService = new FakeSyncShellService();
        var config = new Config { EnableSyncShell = true };

        using var window = new SyncshellWindow(config, fakeService);
        fakeService.EmitStatus("Active");
        fakeService.EmitStatus("Paused");

        var historyField = typeof(SyncshellWindow)
            .GetField("_statusHistory", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var history = (List<string>)historyField.GetValue(window)!;

        Assert.Contains("Paused", history);
        Assert.Contains("Active", history);
    }

    [Fact]
    public void ClearCachesInvokesService()
    {
        SyncshellWindow.Instance?.Dispose();
        var fakeService = new FakeSyncShellService();
        var config = new Config { EnableSyncShell = true };

        using var window = new SyncshellWindow(config, fakeService);
        Assert.Equal(0, fakeService.ClearCount);
        window.ClearCaches();
        Assert.Equal(1, fakeService.ClearCount);
    }

    private sealed class FakeSyncShellService : ISyncShellService
    {
        private bool _paused;
        public int ClearCount { get; private set; }

        public event EventHandler? StatusChanged;

        public bool IsRunning => true;
        public bool IsPaused => _paused;
        public string Status { get; private set; } = "Idle";
        public int NearbyUserCount { get; set; }
        public IReadOnlyList<SyncshellMemberStatus> Members { get; private set; } = Array.Empty<SyncshellMemberStatus>();
        public IReadOnlyList<SyncshellMemberStatus> ActiveMembers { get; private set; } = Array.Empty<SyncshellMemberStatus>();
        public bool PenumbraAvailable => false;
        public string? DetectedPenumbraPath => null;

        public Task Start(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task Stop(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task TriggerPublishAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task ResyncAllAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public void Pause() => _paused = true;

        public void Resume() => _paused = false;

        public void ClearCache() => ClearCount++;

        public Task EnforceCacheLimitAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public bool TryValidatePenumbraPath(string? path, out string? error)
        {
            error = null;
            return true;
        }

        public void EmitStatus(string status)
        {
            Status = status;
            StatusChanged?.Invoke(this, EventArgs.Empty);
        }
    }
}
