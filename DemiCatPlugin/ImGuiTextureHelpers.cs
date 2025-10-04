using Dalamud.Interface.Textures.TextureWraps;

namespace DemiCatPlugin;

internal static class ImGuiTextureHelpers
{
    public static nint ToImGuiHandle(this IDalamudTextureWrap wrap)
        => wrap.Handle.Handle;
}
