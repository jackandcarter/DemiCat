using System.Text.RegularExpressions;

namespace DemiCatPlugin;

public static class MarkdownFormatter
{
    public static string Format(string text)
    {
        text = Regex.Replace(text, "\\[(.+?)\\]\\((.+?)\\)", m => $"[LINK={m.Groups[2].Value}]{m.Groups[1].Value}[/LINK]");
        text = Regex.Replace(text, "\\*\\*(.+?)\\*\\*", m => $"[B]{m.Groups[1].Value}[/B]");
        text = Regex.Replace(text, "\\*(.+?)\\*", m => $"[I]{m.Groups[1].Value}[/I]");
        text = Regex.Replace(text, "__(.+?)__", m => $"[U]{m.Groups[1].Value}[/U]");
        return text;
    }
}
