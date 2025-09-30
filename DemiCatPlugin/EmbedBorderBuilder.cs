using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace DemiCatPlugin;

public static class EmbedBorderBuilder
{
    private const int MaxLineLength = 120;
    private const int Padding = 1;
    private static readonly IReadOnlyDictionary<Config.EmbedBorderGlyph, string> GlyphSymbols = new Dictionary<Config.EmbedBorderGlyph, string>
    {
        [Config.EmbedBorderGlyph.Square] = "■",
        [Config.EmbedBorderGlyph.Circle] = "●",
        [Config.EmbedBorderGlyph.Triangle] = "▲"
    };

    public sealed class BorderState
    {
        public Config.EmbedBorderGlyph Glyph { get; init; }
        public uint Color { get; init; }
    }

    public sealed class Result
    {
        public string Text { get; init; } = string.Empty;
        public bool Applied { get; init; }
        public BorderState? Border { get; init; }
        public string? Warning { get; init; }
    }

    public static Result Apply(string? content, Config.EmbedBorderSettings? settings, string? channelKind, int maxLength)
    {
        var normalized = content ?? string.Empty;
        if (string.IsNullOrWhiteSpace(normalized) || settings == null || !settings.Enabled)
        {
            return new Result { Text = normalized, Applied = false };
        }

        var sanitized = settings.Clone();
        sanitized.Color = Config.SanitizeRgb(sanitized.Color, Config.GetDefaultEmbedColor(channelKind));
        var glyphSymbol = GetGlyphSymbol(sanitized.Glyph);
        var lines = normalized.Split('\n');
        if (lines.Length == 0)
        {
            lines = new[] { string.Empty };
        }

        if (lines.Any(line => line.Length > MaxLineLength))
        {
            return new Result
            {
                Text = normalized,
                Applied = false,
                Warning = $"Embed border disabled because a line exceeds {MaxLineLength} characters."
            };
        }

        var width = Math.Max(1, lines.Max(line => line.Length));
        var horizontalGlyphCount = width + (Padding + 1) * 2;
        var builder = new StringBuilder();
        AppendRepeated(builder, glyphSymbol, horizontalGlyphCount);
        builder.Append('\n');
        foreach (var line in lines)
        {
            builder.Append(glyphSymbol);
            builder.Append(' ');
            builder.Append(line.PadRight(width));
            builder.Append(' ');
            builder.Append(glyphSymbol);
            builder.Append('\n');
        }
        AppendRepeated(builder, glyphSymbol, horizontalGlyphCount);

        var bordered = builder.ToString();
        if (bordered.Length > 0 && bordered[^1] == '\n')
        {
            bordered = bordered[..^1];
        }

        if (bordered.Length > maxLength)
        {
            return new Result
            {
                Text = normalized,
                Applied = false,
                Warning = "Embed border disabled because it exceeds Discord's embed length limit."
            };
        }

        return new Result
        {
            Text = bordered,
            Applied = true,
            Border = new BorderState
            {
                Glyph = sanitized.Glyph,
                Color = sanitized.Color
            }
        };
    }

    public static bool TryStrip(string text, string? glyphName, out string stripped)
    {
        stripped = text ?? string.Empty;
        if (string.IsNullOrEmpty(text))
        {
            return false;
        }

        if (!TryParseGlyph(glyphName, out var glyph))
        {
            return false;
        }

        var symbol = GetGlyphSymbol(glyph);
        var lines = text.Split('\n');
        if (lines.Length < 3)
        {
            return false;
        }

        if (!IsHorizontalLine(lines[0], symbol) || !IsHorizontalLine(lines[^1], symbol))
        {
            return false;
        }

        var inner = new List<string>();
        for (var i = 1; i < lines.Length - 1; i++)
        {
            var line = lines[i];
            var prefix = symbol + " ";
            var suffix = " " + symbol;
            if (!line.StartsWith(prefix, StringComparison.Ordinal) || !line.EndsWith(suffix, StringComparison.Ordinal))
            {
                return false;
            }

            var innerSpan = line.Substring(prefix.Length, line.Length - prefix.Length - suffix.Length);
            inner.Add(innerSpan.TrimEnd());
        }

        stripped = string.Join('\n', inner);
        return true;
    }

    public static string GetGlyphName(Config.EmbedBorderGlyph glyph)
        => glyph switch
        {
            Config.EmbedBorderGlyph.Circle => "circle",
            Config.EmbedBorderGlyph.Triangle => "triangle",
            _ => "square"
        };

    public static bool TryParseGlyph(string? glyphName, out Config.EmbedBorderGlyph glyph)
    {
        glyph = Config.EmbedBorderGlyph.Square;
        if (string.IsNullOrWhiteSpace(glyphName))
        {
            return false;
        }

        switch (glyphName.Trim().ToLowerInvariant())
        {
            case "circle":
                glyph = Config.EmbedBorderGlyph.Circle;
                return true;
            case "triangle":
                glyph = Config.EmbedBorderGlyph.Triangle;
                return true;
            case "square":
                glyph = Config.EmbedBorderGlyph.Square;
                return true;
            default:
                return false;
        }
    }

    public static string GetGlyphSymbol(Config.EmbedBorderGlyph glyph)
    {
        if (GlyphSymbols.TryGetValue(glyph, out var symbol))
        {
            return symbol;
        }

        return GlyphSymbols[Config.EmbedBorderGlyph.Square];
    }

    private static bool IsHorizontalLine(string line, string symbol)
    {
        if (string.IsNullOrEmpty(line))
        {
            return false;
        }

        var glyphLength = symbol.Length;
        if (glyphLength <= 0 || line.Length % glyphLength != 0)
        {
            return false;
        }

        var count = line.Length / glyphLength;
        var builder = new StringBuilder(line.Length);
        AppendRepeated(builder, symbol, count);
        return builder.ToString().Equals(line, StringComparison.Ordinal);
    }

    private static void AppendRepeated(StringBuilder builder, string symbol, int count)
    {
        for (var i = 0; i < count; i++)
        {
            builder.Append(symbol);
        }
    }
}
