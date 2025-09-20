using System;
using System.Collections.Generic;
using System.Linq;
using DiscordHelper;
using DemiCat.UI;

namespace DemiCatPlugin;

public static class EventPreviewFormatter
{
    public sealed record Result(
        EmbedDto Embed,
        string? Content,
        IReadOnlyList<EmbedButtonDto> Buttons,
        IReadOnlyList<string> Warnings);

    private static readonly IReadOnlyList<string> DefaultAttendance = new[] { "yes", "maybe", "no" };

    public static Result Build(
        string? title,
        string? description,
        DateTimeOffset? timestamp,
        string? url,
        string? imageUrl,
        string? thumbnailUrl,
        uint? color,
        IEnumerable<EmbedFieldDto>? fields,
        IEnumerable<EmbedButtonDto>? buttons,
        IEnumerable<ulong>? mentions,
        IEnumerable<string>? attendance = null,
        string? embedId = null)
    {
        var fieldList = fields?
            .Where(f => !string.IsNullOrWhiteSpace(f.Name) && !string.IsNullOrWhiteSpace(f.Value))
            .Select(f => new EmbedFieldDto { Name = f.Name, Value = f.Value, Inline = f.Inline })
            .ToList() ?? new List<EmbedFieldDto>();

        var buttonList = buttons?
            .Where(b => !string.IsNullOrWhiteSpace(b.Label))
            .Select(CopyButton)
            .ToList() ?? new List<EmbedButtonDto>();

        foreach (var button in buttonList)
        {
            if (button.Width.HasValue)
            {
                button.Width = button.Width.Value > 0
                    ? Math.Min(button.Width.Value, ButtonSizeHelper.Max)
                    : null;
            }
        }

        EnsureDefaultButtons(buttonList, attendance);

        var mentionList = new List<ulong>();
        if (mentions != null)
        {
            var seen = new HashSet<ulong>();
            foreach (var mention in mentions)
            {
                if (seen.Add(mention))
                {
                    mentionList.Add(mention);
                }
            }
        }

        var content = mentionList.Count > 0
            ? string.Join(" ", mentionList.Select(id => $"<@&{id}>"))
            : null;

        var embed = new EmbedDto
        {
            Id = string.IsNullOrWhiteSpace(embedId) ? "preview" : embedId!,
            Title = string.IsNullOrWhiteSpace(title) ? null : title,
            Description = string.IsNullOrWhiteSpace(description) ? null : description,
            Url = string.IsNullOrWhiteSpace(url) ? null : url,
            ImageUrl = string.IsNullOrWhiteSpace(imageUrl) ? null : imageUrl,
            ThumbnailUrl = string.IsNullOrWhiteSpace(thumbnailUrl) ? null : thumbnailUrl,
            Color = color,
            Timestamp = timestamp,
            Fields = fieldList.Count > 0 ? fieldList : null,
            Buttons = buttonList.Count > 0 ? buttonList : null,
            Mentions = mentionList.Count > 0 ? mentionList : null
        };

        var warnings = new List<string>();
        warnings.AddRange(EmbedValidation.Validate(embed, buttonList));
        warnings.AddRange(ValidateButtonLayout(buttonList));

        return new Result(embed, content, buttonList, warnings);
    }

    private static EmbedButtonDto CopyButton(EmbedButtonDto button)
        => new()
        {
            Label = button.Label,
            Url = string.IsNullOrWhiteSpace(button.Url) ? null : button.Url,
            CustomId = string.IsNullOrWhiteSpace(button.CustomId) ? null : button.CustomId,
            Emoji = string.IsNullOrWhiteSpace(button.Emoji) ? null : button.Emoji,
            Style = button.Style,
            MaxSignups = button.MaxSignups,
            Width = button.Width,
            RowIndex = button.RowIndex
        };

    private static IEnumerable<string> ValidateButtonLayout(IReadOnlyList<EmbedButtonDto> buttons)
    {
        var warnings = new List<string>();
        if (buttons.Count == 0)
        {
            return warnings;
        }

        var rows = buttons.GroupBy(b => b.RowIndex ?? 0).ToList();
        var tooManyRows = rows.Count > ButtonRows.MaxRows || rows.Any(r => r.Key >= ButtonRows.MaxRows || r.Key < 0);
        if (tooManyRows)
        {
            warnings.Add($"Too many button rows (max {ButtonRows.MaxRows})");
        }

        foreach (var row in rows)
        {
            var rowButtons = row.ToList();
            if (rowButtons.Count > ButtonRows.MaxPerRow && !warnings.Contains($"Too many buttons in row (max {ButtonRows.MaxPerRow})"))
            {
                warnings.Add($"Too many buttons in row (max {ButtonRows.MaxPerRow})");
            }

            foreach (var button in rowButtons)
            {
                if (button.Style == ButtonStyle.Link)
                {
                    if (string.IsNullOrWhiteSpace(button.Url))
                    {
                        warnings.Add("Link buttons require a URL");
                    }
                }
                else
                {
                    if (string.IsNullOrWhiteSpace(button.CustomId))
                    {
                        warnings.Add("Non-link buttons require customId");
                    }
                }
            }
        }

        return warnings;
    }

    private static void EnsureDefaultButtons(List<EmbedButtonDto> buttonList, IEnumerable<string>? attendance)
    {
        if (buttonList.Count > 0)
        {
            return;
        }

        var tags = attendance?.ToList();
        if (tags == null || tags.Count == 0)
        {
            tags = new List<string>(DefaultAttendance);
        }

        foreach (var tag in tags)
        {
            var value = tag ?? string.Empty;
            var label = Capitalize(value);
            buttonList.Add(new EmbedButtonDto
            {
                Label = label,
                CustomId = $"rsvp:{value}",
                Width = ButtonSizeHelper.ComputeWidth(label)
            });
        }
    }

    private static string Capitalize(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        if (value.Length == 1)
        {
            return value.ToUpperInvariant();
        }

        return char.ToUpperInvariant(value[0]) + value.Substring(1).ToLowerInvariant();
    }
}
