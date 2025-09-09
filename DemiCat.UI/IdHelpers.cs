using System;
using System.Globalization;
using System.Linq;

namespace DemiCat.UI;

public static class IdHelpers
{
    public static string Truncate(string s, int max)
    {
        if (string.IsNullOrEmpty(s) || s.Length <= max) return s;
        var e = StringInfo.GetTextElementEnumerator(s);
        int consumed = 0;
        int count = 0;
        while (e.MoveNext())
        {
            var element = e.GetTextElement();
            consumed += element.Length;
            count++;
            if (count == max)
                return s.Substring(0, consumed);
        }
        return s;
    }

    public static string MakeCustomId(string label, int row, int col)
    {
        var slug = Sanitize(label);
        if (string.IsNullOrWhiteSpace(slug)) slug = $"btn-{row}-{col}";
        var hash = Hash8(label);
        var maxSlug = 100 - ("rsvp:".Length + 1 + hash.Length);
        if (maxSlug < 0) maxSlug = 0;
        slug = Truncate(slug, maxSlug);
        return $"rsvp:{slug}:{hash}";
    }

    public static string Sanitize(string s) =>
        new string(s.ToLowerInvariant()
            .Where(ch => char.IsLetterOrDigit(ch) || ch == '-' || ch == '_')
            .ToArray());

    public static string Hash8(string s)
    {
        unchecked
        {
            uint hash = 2166136261;
            foreach (var ch in s)
                hash = (hash ^ ch) * 16777619;
            return hash.ToString("x8");
        }
    }
}
