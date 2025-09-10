using System;

namespace DemiCat.UI;

public static class ButtonSizeHelper
{
    public const int Max = 200;
    public const int DefaultHeight = 40;

    public static int ComputeWidth(string label)
    {
        if (string.IsNullOrWhiteSpace(label)) return 0;
        // Approximate width: 8px per character plus padding.
        var width = label.Length * 8 + 24;
        return Math.Min(width, Max);
    }
}
