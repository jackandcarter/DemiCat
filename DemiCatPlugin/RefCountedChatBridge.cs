using System;
using System.Threading.Tasks;

namespace DemiCatPlugin;

public sealed class RefCountedChatBridge : IChatBridge
{
    private readonly IChatBridge _inner;
    private readonly object _sync = new();
    private int _startCount;
    private bool _disposed;

    public RefCountedChatBridge(IChatBridge inner)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
    }

    public void Start()
    {
        lock (_sync)
        {
            ThrowIfDisposed();
            if (_startCount++ == 0)
            {
                _inner.Start();
            }
        }
    }

    public void Stop()
    {
        lock (_sync)
        {
            if (_disposed)
            {
                return;
            }

            if (_startCount == 0)
            {
                return;
            }

            if (--_startCount == 0)
            {
                _inner.Stop();
            }
        }
    }

    public bool IsReady() => _inner.IsReady();

    public void Subscribe(string channel, string? guildId, string? kind)
        => _inner.Subscribe(channel, guildId, kind);

    public void Unsubscribe(string channel)
        => _inner.Unsubscribe(channel);

    public void Ack(string channel, string? guildId, string? kind)
        => _inner.Ack(channel, guildId, kind);

    public Task Send(string channel, object payload)
        => _inner.Send(channel, payload);

    public void Resync(string channel)
        => _inner.Resync(channel);

    public event Action<string>? MessageReceived
    {
        add => _inner.MessageReceived += value;
        remove => _inner.MessageReceived -= value;
    }

    public event Action<DiscordUserDto>? TypingReceived
    {
        add => _inner.TypingReceived += value;
        remove => _inner.TypingReceived -= value;
    }

    public event Action? Linked
    {
        add => _inner.Linked += value;
        remove => _inner.Linked -= value;
    }

    public event Action? Unlinked
    {
        add => _inner.Unlinked += value;
        remove => _inner.Unlinked -= value;
    }

    public event Action<string>? StatusChanged
    {
        add => _inner.StatusChanged += value;
        remove => _inner.StatusChanged -= value;
    }

    public event Action<string, long>? ResyncRequested
    {
        add => _inner.ResyncRequested += value;
        remove => _inner.ResyncRequested -= value;
    }

    public event Action? TemplatesUpdated
    {
        add => _inner.TemplatesUpdated += value;
        remove => _inner.TemplatesUpdated -= value;
    }

    public void Dispose()
    {
        int startCount;
        lock (_sync)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            startCount = _startCount;
            _startCount = 0;
        }

        if (startCount > 0)
        {
            _inner.Stop();
        }

        _inner.Dispose();
    }

    private void ThrowIfDisposed()
    {
        if (_disposed)
        {
            throw new ObjectDisposedException(nameof(RefCountedChatBridge));
        }
    }
}
