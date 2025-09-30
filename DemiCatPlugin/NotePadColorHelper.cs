using System;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DemiCatPlugin;

internal static class NotePadColorHelper
{
    public const int DefaultColorValue = 0x4A5568;
    public const string DefaultHexColor = "#4a5568";

    public static bool TryParseColorString(string? color, out int value)
    {
        value = 0;
        if (string.IsNullOrWhiteSpace(color))
        {
            return false;
        }

        var span = color.AsSpan().Trim();
        if (span.Length == 0)
        {
            return false;
        }

        if (span[0] == '#')
        {
            span = span[1..];
            return TryParseHex(span, out value);
        }

        if (span.Length > 2 && span[0] == '0' && (span[1] == 'x' || span[1] == 'X'))
        {
            span = span[2..];
            return TryParseHex(span, out value);
        }

        if (int.TryParse(span, NumberStyles.Integer, CultureInfo.InvariantCulture, out var dec) && dec >= 0)
        {
            value = dec;
            return true;
        }

        return TryParseHex(span, out value);
    }

    public static string ToHex(int value)
    {
        var sanitized = value & 0xFFFFFF;
        return $"#{sanitized:X6}".ToLowerInvariant();
    }

    public static string Normalize(string? color)
        => TryParseColorString(color, out var value) ? ToHex(value) : DefaultHexColor;

    private static bool TryParseHex(ReadOnlySpan<char> span, out int value)
    {
        if (span.Length == 0)
        {
            value = 0;
            return false;
        }

        if (int.TryParse(span, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var hex))
        {
            value = hex;
            return true;
        }

        value = 0;
        return false;
    }
}

internal sealed class NotePadColorJsonConverter : JsonConverter<string>
{
    public override string Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.Number)
        {
            if (reader.TryGetInt64(out var value))
            {
                return NotePadColorHelper.ToHex((int)(value & 0xFFFFFF));
            }

            throw new JsonException("Unable to parse numeric NotePad color");
        }

        if (reader.TokenType == JsonTokenType.String)
        {
            return NotePadColorHelper.Normalize(reader.GetString());
        }

        if (reader.TokenType == JsonTokenType.Null)
        {
            return NotePadColorHelper.DefaultHexColor;
        }

        throw new JsonException($"Unexpected token {reader.TokenType} when parsing NotePad color");
    }

    public override void Write(Utf8JsonWriter writer, string value, JsonSerializerOptions options)
    {
        if (NotePadColorHelper.TryParseColorString(value, out var color))
        {
            writer.WriteNumberValue(color & 0xFFFFFF);
            return;
        }

        writer.WriteNullValue();
    }
}
