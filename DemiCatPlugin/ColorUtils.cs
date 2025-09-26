using System;
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

    public static Vector4 RgbToVector4(uint rgb, float alpha = 1f)
        => new(
            ((rgb >> 16) & 0xFF) / 255f,
            ((rgb >> 8) & 0xFF) / 255f,
            (rgb & 0xFF) / 255f,
            alpha);

    public static uint Vector4ToImGui(Vector4 color)
        => VectorToImGui(new Vector3(color.X, color.Y, color.Z));

    public static uint MixRgb(uint source, uint target, float amount)
    {
        var t = Math.Clamp(amount, 0f, 1f);
        static byte MixChannel(uint s, uint t, float factor)
        {
            var blended = s * (1f - factor) + t * factor;
            var rounded = (int)Math.Round(blended);
            return (byte)Math.Clamp(rounded, 0, 255);
        }

        var sr = (source >> 16) & 0xFF;
        var sg = (source >> 8) & 0xFF;
        var sb = source & 0xFF;
        var tr = (target >> 16) & 0xFF;
        var tg = (target >> 8) & 0xFF;
        var tb = target & 0xFF;

        var r = MixChannel(sr, tr, t);
        var g = MixChannel(sg, tg, t);
        var b = MixChannel(sb, tb, t);

        return ((uint)r << 16) | ((uint)g << 8) | b;
    }
}

