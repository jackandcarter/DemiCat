using System.Linq;

namespace DemiCat.UI;

public static class IdHelpers
{
    public static string Truncate(string s, int max) =>
        string.IsNullOrEmpty(s) || s.Length <= max ? s : s.Substring(0, max);

    public static string Sanitize(string s) =>
        new string((s ?? "").ToLowerInvariant()
            .Where(ch => char.IsLetterOrDigit(ch) || ch == '-' || ch == '_')
            .ToArray());

    public static string MakeCustomId(string label, int row, int col)
    {
        var slug = Sanitize(label);
        if (string.IsNullOrWhiteSpace(slug)) slug = $"btn-{row}-{col}";
        var h = Hash8(label);
        var id = $"rsvp:{slug}:{h}";
        return Truncate(id, 100); // Discord custom_id limit
    }

    private static string Hash8(string s)
    {
        unchecked
        {
            uint hash = 2166136261;
            foreach (var ch in s ?? "")
                hash = (hash ^ ch) * 16777619;
            return hash.ToString("x8");
        }
    }
}
