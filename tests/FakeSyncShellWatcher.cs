using System;
using DemiCatPlugin.SyncShell;

namespace Tests;

internal sealed class FakeSyncShellWatcher : ISyncShellWatcher
{
    public event Action? Connected;
    public event Action<string[]>? NearbySet;
    public event Action<string>? MemberChanged;

    public bool IsRunning { get; private set; }

    public void Start() => IsRunning = true;

    public void Stop() => IsRunning = false;

    public void Dispose() => Stop();
}
