using System.Reflection;

namespace DemiCatPlugin.Emoji;

internal sealed class ManagedFontAtlasEmojiFontHandle : IEmojiFontHandle
{
    private readonly object _handle;
    private readonly MethodInfo? _pushMethod;
    private readonly MethodInfo? _disposeMethod;

    public ManagedFontAtlasEmojiFontHandle(object handle)
    {
        _handle = handle;
        var handleType = handle.GetType();
        _pushMethod = handleType.GetMethod("Push", Type.EmptyTypes);
        _disposeMethod = handleType.GetMethod("Dispose", Type.EmptyTypes);
    }

    public IDisposable? Push()
    {
        var result = _pushMethod?.Invoke(_handle, Array.Empty<object>());
        return result as IDisposable;
    }

    public void Dispose()
    {
        switch (_handle)
        {
            case IDisposable disposable:
                disposable.Dispose();
                break;
            default:
                _disposeMethod?.Invoke(_handle, Array.Empty<object>());
                break;
        }
    }
}
