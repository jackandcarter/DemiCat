using System;

namespace DemiCatPlugin;

public sealed class UiStyleScope : IDisposable
{
    private readonly int _pushVarCount;
    private readonly int _pushColorCount;
    private bool _disposed;

    public UiStyleScope(Config config)
    {
        (_pushVarCount, _pushColorCount) = UiTheme.Apply(config);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        if (_pushColorCount > 0 || _pushVarCount > 0)
        {
            UiTheme.Pop();
        }

        _disposed = true;
    }
}
