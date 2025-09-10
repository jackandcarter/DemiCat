using System;
using System.Collections.Generic;
using System.Numerics;
using Dalamud.Interface.Textures;
using Dalamud.Interface.Textures.TextureWraps;

namespace DemiCatPlugin;

// Simple cache around Dalamud web textures.
// You already call t.GetWrapOrEmpty() elsewhere; we stick to that pattern.
public static class WebTextureCache
{
    private static readonly Dictionary<string, ISharedImmediateTexture> _map = new();

    // Allow tests to override texture fetching behaviour
    public static Func<string, Action<ISharedImmediateTexture?>, object?>? FetchOverride { get; set; }

    public static void Get(string url, Action<ISharedImmediateTexture?> onReady)
    {
        if (FetchOverride != null)
        {
            FetchOverride(url, onReady);
            return;
        }

        if (string.IsNullOrEmpty(url))
        {
            onReady(null);
            return;
        }

        if (_map.TryGetValue(url, out var tex))
        {
            onReady(tex);
            return;
        }

        try
        {
            PluginServices.Instance!.TextureProvider.GetFromUrl(new Uri(url), tex =>
            {
                if (tex != null) _map[url] = tex;
                onReady(tex);
            });
        }
        catch
        {
            onReady(null);
        }
    }

    public static void DrawImageButton(string id, ISharedImmediateTexture? tex, Vector2 size, Action onClick)
    {
        if (tex == null) return;
        var wrap = tex.GetWrapOrEmpty();
        if (ImGui.ImageButton(id, wrap.Handle, size))
            onClick();
    }
}

