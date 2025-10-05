using System;
using System.Collections.Generic;

namespace DemiCatPlugin;

internal sealed class WindowSystem
{
    private readonly List<Action> _drawCallbacks = new();

    public void Add(Action drawCallback)
    {
        if (drawCallback == null)
            throw new ArgumentNullException(nameof(drawCallback));

        if (_drawCallbacks.Contains(drawCallback))
            return;

        _drawCallbacks.Add(drawCallback);
    }

    public void Draw()
    {
        foreach (var callback in _drawCallbacks)
            callback();
    }

    public void Clear()
    {
        _drawCallbacks.Clear();
    }
}
