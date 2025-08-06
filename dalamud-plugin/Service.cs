using System;

namespace DalamudPlugin;

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
    }
}

