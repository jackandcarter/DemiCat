using System.Globalization;
using System.Linq;

namespace DemiCat.UI;

public static class IdHelpers
{
    /// <summary>
    /// Grapheme-safe truncate. Ensures we don't split emoji / ZWJ clusters.
    /// </summary>
    public static string Truncate(string s, int maxGraphemes)
    {
        if (string.IsNullOrEmpty(s)) return s;
        if (maxGraphemes <= 0) return string.Empty;

        // Fast-path when likely small ASCII
        if (s.Length <= maxGraphemes && StringInfo.ParseCombiningCharacters(s).Length == s.Length)
            return s;

        var e = StringInfo.GetTextElementEnumerator(s);
        int consumedChars = 0;
        int count = 0;
        while (e.MoveNext())
        {
            consumedChars += e.GetTextElement().Length;
            count++;
            if (count >= maxGraphemes)
                return s.Substring(0, consumedChars);
        }
        return s; // shorter than max
    }

    public static string Sanitize(string s) =>
        new string((s ?? string.Empty).ToLowerInvariant()
            .Where(ch => char.IsLetterOrDigit(ch) || ch == '-' || ch == '_')
            .ToArray());

    /// <summary>
    /// Make a Discord custom_id of the form "rsvp:{slug}:{hash}" and cap to 100 characters.
    /// Include row/col in the hash seed to avoid collisions for duplicate labels.
    /// </summary>
    public static string MakeCustomId(string label, int row, int col)
    {
        var slug = Sanitize(label);
        if (string.IsNullOrWhiteSpace(slug))
            slug = $"btn-{row}-{col}";

        var h = Hash8($"{label}#{row}:{col}");
        var id = $"rsvp:{slug}:{h}";
        return TruncateSimple(id, 100);
    }

    public static string TruncateSimple(string s, int maxChars)
    {
        if (string.IsNullOrEmpty(s)) return s;
        if (maxChars <= 0) return string.Empty;
        return s.Length <= maxChars ? s : s.Substring(0, maxChars);
    }

    private static string Hash8(string s)
    {
        unchecked
        {
            uint hash = 2166136261;
            foreach (var ch in s ?? string.Empty)
                hash = (hash ^ ch) * 16777619;
            return hash.ToString("x8");
        }
    }
}
