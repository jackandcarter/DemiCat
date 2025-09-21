using System;

namespace DemiCatPlugin;

internal static class MarkdownSelectionHelper
{
    public static void WrapSelection(ref string? text, string prefix, string suffix, ref int selectionStart, ref int selectionEnd)
    {
        text ??= string.Empty;
        var original = text;
        var length = original.Length;

        var startIndex = Math.Clamp(selectionStart, 0, length);
        var endIndex = Math.Clamp(selectionEnd, 0, length);

        var start = Math.Min(startIndex, endIndex);
        var end = Math.Max(startIndex, endIndex);

        if (start == end)
        {
            while (start > 0 && char.IsLetterOrDigit(original[start - 1]))
            {
                start--;
            }

            while (end < length && char.IsLetterOrDigit(original[end]))
            {
                end++;
            }
        }

        var selected = original.Substring(start, end - start);
        text = original[..start] + prefix + selected + suffix + original[end..];

        var cursor = start + prefix.Length + selected.Length + suffix.Length;
        selectionStart = selectionEnd = cursor;
    }
}
