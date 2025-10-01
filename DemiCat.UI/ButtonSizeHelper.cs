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

    public static float ResolveWidth(int? explicitWidth, string label)
    {
        if (explicitWidth.HasValue && explicitWidth.Value > 0)
        {
            return Math.Min(explicitWidth.Value, Max);
        }

        var computed = ComputeWidth(label);
        return Math.Max(1, computed);
    }
}
