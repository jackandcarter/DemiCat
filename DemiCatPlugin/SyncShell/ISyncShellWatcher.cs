using System;

namespace DemiCatPlugin.SyncShell;

public interface ISyncShellWatcher : IDisposable
{
    event Action? Connected;
    event Action<string[]>? NearbySet;
    event Action<string>? MemberChanged;

    bool IsRunning { get; }

    void Start();
    void Stop();
}
