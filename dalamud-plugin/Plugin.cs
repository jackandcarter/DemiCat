using System;
using DiscordHelper;

namespace DalamudPlugin;

public interface IDalamudPlugin : IDisposable
{
    string Name { get; }
}

public class Plugin : IDalamudPlugin
{
    public string Name => "SamplePlugin";
    private readonly Config _config = new();
    private readonly UiRenderer _ui = new();

    public void Dispose()
    {
        _ui.Dispose();
    }
}
