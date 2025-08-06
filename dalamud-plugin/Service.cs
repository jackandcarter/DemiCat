using System;

namespace DalamudPlugin;

public interface IDalamudTextureWrap : IDisposable
{
    nint ImGuiHandle { get; }
    int Width { get; }
    int Height { get; }
}

public static class Service
{
    public static PluginInterface Interface { get; } = new();

    public class PluginInterface
    {
        public UiBuilder UiBuilder { get; } = new();
    }

    public class UiBuilder
    {
        public event Action? Draw;

        public void TriggerDraw() => Draw?.Invoke();

        public IDalamudTextureWrap LoadImageRaw(byte[] data, int width, int height, int bytesPerPixel)
        {
            return new DummyTextureWrap(width, height);
        }
    }

    private class DummyTextureWrap : IDalamudTextureWrap
    {
        public nint ImGuiHandle { get; } = (nint)1;
        public int Width { get; }
        public int Height { get; }

        public DummyTextureWrap(int width, int height)
        {
            Width = width;
            Height = height;
        }

        public void Dispose()
        {
        }
    }
}

