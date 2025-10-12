using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using DiscordHelper;

namespace DemiCatPlugin;

internal static class EventEmbedHelpers
{
    private const string RsvpPrefix = "rsvp:";

    public static bool HasRsvpButtons(IReadOnlyList<EmbedButtonDto>? buttons)
    {
        if (buttons == null)
        {
            return false;
        }

        foreach (var button in buttons)
        {
            if (button?.CustomId == null)
            {
                continue;
            }

            if (button.CustomId.StartsWith(RsvpPrefix, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    public static bool IsApolloEvent(EmbedDto dto)
    {
        if (dto == null)
        {
            return false;
        }

        if (!string.IsNullOrEmpty(dto.FooterText) &&
            dto.FooterText.Contains("apollo", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (!string.IsNullOrEmpty(dto.ProviderName) &&
            dto.ProviderName.Contains("apollo", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (!string.IsNullOrEmpty(dto.AuthorName) &&
            dto.AuthorName.Contains("apollo", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (dto.Authors != null)
        {
            return dto.Authors.Any(a =>
                !string.IsNullOrEmpty(a?.Name) &&
                a!.Name!.Contains("apollo", StringComparison.OrdinalIgnoreCase));
        }

        return false;
    }

    public static bool ShouldDisplayStartTime(EmbedDto dto)
    {
        if (dto == null || !dto.Timestamp.HasValue)
        {
            return false;
        }

        if (HasRsvpButtons(dto.Buttons))
        {
            return true;
        }

        return IsApolloEvent(dto);
    }

    public static string FormatLocalStartTime(DateTimeOffset timestamp)
    {
        var local = timestamp.ToLocalTime();
        return local.ToString("f", CultureInfo.CurrentCulture);
    }

    public static string BuildDiscordStartLine(DateTimeOffset timestamp)
    {
        var unix = timestamp.ToUnixTimeSeconds();
        return $"**Starts:** <t:{unix}:F>";
    }

    public static string? AppendDiscordStartLine(string? description, DateTimeOffset? timestamp)
    {
        if (!timestamp.HasValue)
        {
            return description;
        }

        var startLine = BuildDiscordStartLine(timestamp.Value);
        if (string.IsNullOrWhiteSpace(description))
        {
            return startLine;
        }

        var trimmed = description.TrimEnd();
        if (trimmed.EndsWith(startLine, StringComparison.Ordinal))
        {
            return description;
        }

        return $"{trimmed}\n\n{startLine}";
    }

    public static string? RemoveAppendedStartLine(string? description, DateTimeOffset? timestamp, out bool removed)
    {
        removed = false;
        if (string.IsNullOrEmpty(description) || !timestamp.HasValue)
        {
            return description;
        }

        var startLine = BuildDiscordStartLine(timestamp.Value);
        var trimmed = description.TrimEnd();
        if (!trimmed.EndsWith(startLine, StringComparison.Ordinal))
        {
            return description;
        }

        removed = true;
        var withoutLine = trimmed.Substring(0, trimmed.Length - startLine.Length).TrimEnd();
        return string.IsNullOrEmpty(withoutLine) ? null : withoutLine;
    }
}
