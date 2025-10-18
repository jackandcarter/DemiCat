using System;
using System.Net.Http;

namespace DemiCatPlugin;

/// <summary>
/// Standalone window wrapper around <see cref="PresenceSidebar"/> so the
/// roster can be surfaced independently from the chat views.
/// </summary>
public sealed class PresenceWindow : IDisposable
{
    private readonly PresenceSidebar _sidebar;

    public PresenceWindow(DiscordPresenceService service, Config config, HttpClient httpClient)
    {
        _sidebar = new PresenceSidebar(service, config, httpClient)
        {
            TextureLoader = WebTextureCache.Get,
            TextureTouch = TouchTexture
        };
    }

    public void Draw()
    {
        _sidebar.DrawStandalone();
    }

    public void Dispose()
    {
        _sidebar.Dispose();
    }

    private static void TouchTexture(string? url)
    {
        if (string.IsNullOrEmpty(url))
        {
            return;
        }

        WebTextureCache.TryGetTexture(url, out _);
    }
}
