using System;

namespace DemiCatPlugin;

internal static class StringUtil
{
    // Safe string (never null; trims; returns "")
    public static string S(string? v) => string.IsNullOrWhiteSpace(v) ? string.Empty : v.Trim();

    // Safe nullable (passes null through; ToString() otherwise)
    public static string? SN(object? v) => v switch
    {
        null => null,
        string s => s,
        _ => v.ToString()
    };

    // Null if empty/whitespace (after trim)
    public static string? NullIfWhite(string? v) => string.IsNullOrWhiteSpace(v) ? null : v!.Trim();

    // Trim and hard-limit length (default 2000 to match existing UI rule)
    public static string TrimLimit(string? v, int max = 2000)
    {
        var t = S(v);
        return t.Length <= max ? t : t.Substring(0, max);
    }
}
