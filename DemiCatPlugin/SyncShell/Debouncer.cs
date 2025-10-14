using System;
using System.Threading;
using System.Threading.Tasks;

namespace DemiCatPlugin.SyncShell;

internal sealed class Debouncer : IDisposable
{
    private readonly TimeSpan _delay;
    private readonly object _lock = new();
    private CancellationTokenSource? _cts;
    private bool _disposed;

    public Debouncer(TimeSpan delay)
    {
        if (delay < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(delay));
        }

        _delay = delay;
    }

    public void Run(Action action)
    {
        if (action == null)
        {
            throw new ArgumentNullException(nameof(action));
        }

        lock (_lock)
        {
            ThrowIfDisposed();
            _cts?.Cancel();
            _cts?.Dispose();
            _cts = new CancellationTokenSource();
            var token = _cts.Token;

            _ = Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(_delay, token).ConfigureAwait(false);
                    if (!token.IsCancellationRequested)
                    {
                        action();
                    }
                }
                catch (OperationCanceledException)
                {
                }
                catch (Exception ex)
                {
                    PluginServices.Instance?.Log.Warning(ex, "Debouncer action failed");
                }
            }, CancellationToken.None);
        }
    }

    private void ThrowIfDisposed()
    {
        if (_disposed)
        {
            throw new ObjectDisposedException(nameof(Debouncer));
        }
    }

    public void Dispose()
    {
        lock (_lock)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _cts?.Cancel();
            _cts?.Dispose();
            _cts = null;
        }
    }
}
