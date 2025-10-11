using System;
using System.Collections.Generic;
using System.Linq;

namespace DemiCatPlugin;

public sealed class TypingCoalescer : IDisposable
{
    private readonly Dictionary<string, DateTime> _typingUntil = new();
    private readonly TimeSpan _ttl = TimeSpan.FromSeconds(6);
    private readonly TimeSpan _uiDebounce = TimeSpan.FromMilliseconds(350);
    private DateTime _nextUiTick = DateTime.MinValue;

    public event Action<IReadOnlyList<string>>? TypingUsersChanged;

    public void OnTyping(string userId)
    {
        if (string.IsNullOrEmpty(userId))
        {
            return;
        }

        var now = DateTime.UtcNow;
        _typingUntil[userId] = now + _ttl;
        TryEmit(now);
    }

    public void Tick()
    {
        var now = DateTime.UtcNow;
        if (now < _nextUiTick)
        {
            return;
        }

        var changed = Sweep(now);
        if (changed)
        {
            Emit(now);
        }
    }

    public void Clear()
    {
        _typingUntil.Clear();
        _nextUiTick = DateTime.MinValue;
        TypingUsersChanged?.Invoke(Array.Empty<string>());
    }

    private bool Sweep(DateTime now)
    {
        var expired = _typingUntil.Where(kv => kv.Value <= now).Select(kv => kv.Key).ToList();
        if (expired.Count == 0)
        {
            return false;
        }

        foreach (var key in expired)
        {
            _typingUntil.Remove(key);
        }

        return true;
    }

    private void TryEmit(DateTime now)
    {
        if (now < _nextUiTick)
        {
            return;
        }

        Emit(now);
    }

    private void Emit(DateTime now)
    {
        _nextUiTick = now + _uiDebounce;
        TypingUsersChanged?.Invoke(_typingUntil.Keys.ToList());
    }

    public void Dispose()
    {
        _typingUntil.Clear();
    }
}
