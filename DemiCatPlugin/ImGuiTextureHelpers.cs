using Dalamud.Interface.Textures.TextureWraps;

namespace DemiCatPlugin;

internal static class ImGuiTextureHelpers
{
    public static nint ToImGuiHandle(this IDalamudTextureWrap? wrap)
        => wrap is null ? 0 : (nint)(ulong)wrap.Handle.Handle;
}
