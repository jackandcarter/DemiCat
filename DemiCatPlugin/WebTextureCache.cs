using System;
using System.Collections.Generic;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Textures;

namespace DemiCatPlugin;

// Minimal web texture cache used by the emoji picker & tests.
// NOTE: In production we rely on the caller's texture loader; here we only
// provide a cache shim + test override. If no override is set, we just
// invoke the callback with null so callers can fall back gracefully.
public static class WebTextureCache
{
    private static readonly Dictionary<string, ISharedImmediateTexture> _map = new();

    // Tests set this to intercept fetches and provide mocked textures.
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

        // No engine-level URL loader available here; allow caller to render fallback.
        onReady(null);
    }

    public static void DrawImageButton(string id, ISharedImmediateTexture? tex, Vector2 size, Action onClick)
    {
        if (tex == null) return;
        var wrap = tex.GetWrapOrEmpty();

        // In this binding, ImageButton takes (ImTextureID, Vector2) and
        // IDs are provided via PushID/PopID (not a string label param).
        ImGui.PushID(id);
        if (ImGui.ImageButton(wrap.Handle, size))
            onClick();
        ImGui.PopID();
    }

}
