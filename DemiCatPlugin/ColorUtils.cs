using System.Numerics;

namespace DemiCatPlugin;

public static class ColorUtils
{
    public static uint RgbToAbgr(uint rgb)
    {
        var r = (rgb >> 16) & 0xFF;
        var g = (rgb >> 8) & 0xFF;
        var b = rgb & 0xFF;
        return r | (g << 8) | (b << 16);
    }

    public static uint AbgrToRgb(uint abgr)
    {
        var r = abgr & 0xFF;
        var g = (abgr >> 8) & 0xFF;
        var b = (abgr >> 16) & 0xFF;
        return (r << 16) | (g << 8) | b;
    }

    public static uint RgbToImGui(uint rgb)
        => RgbToAbgr(rgb) | 0xFF000000;

    public static uint ImGuiToRgb(uint color)
        => AbgrToRgb(color & 0xFFFFFF);

    public static Vector3 ImGuiToVector(uint color)
        => new((color & 0xFF) / 255f, ((color >> 8) & 0xFF) / 255f, ((color >> 16) & 0xFF) / 255f);

    public static uint VectorToImGui(Vector3 color)
        => ((uint)(color.X * 255)) | ((uint)(color.Y * 255) << 8) | ((uint)(color.Z * 255) << 16) | 0xFF000000;
}

