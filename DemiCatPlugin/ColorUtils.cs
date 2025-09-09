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
}
