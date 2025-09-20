using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using DemiCatPlugin;
using DemiCat.UI;
using DiscordHelper;
using Xunit;

public class EventPreviewFormatterTests
{
    private static readonly DateTimeOffset Timestamp = new(2024, 4, 1, 20, 0, 0, TimeSpan.Zero);

    private static JsonElement GetExpected(string key)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "data", "event_preview_samples.json");
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        return doc.RootElement.GetProperty(key).Clone();
    }

    [Fact]
    public void EventPreviewMatchesFixtureForFc()
    {
        var result = EventPreviewFormatter.Build(
            "Raid Night",
            "Clear the raid together.",
            Timestamp,
            "https://example.com/event",
            "https://example.com/image.png",
            "https://example.com/thumb.png",
            0x123456,
            BuildEventFields(),
            BuildEventButtons(),
            new[] { 12345UL },
            embedId: "event-create-preview");

        AssertPreviewMatches(GetExpected("event_fc"), result);
    }

    [Fact]
    public void EventPreviewMatchesFixtureForOfficer()
    {
        var result = EventPreviewFormatter.Build(
            "Raid Night",
            "Clear the raid together.",
            Timestamp,
            "https://example.com/event",
            "https://example.com/image.png",
            "https://example.com/thumb.png",
            0x123456,
            BuildEventFields(),
            BuildEventButtons(),
            new[] { 12345UL, 67890UL },
            embedId: "event-create-preview");

        AssertPreviewMatches(GetExpected("event_officer"), result);
    }

    [Fact]
    public void TemplatePreviewMatchesFixtureForFc()
    {
        var result = EventPreviewFormatter.Build(
            "Static Meeting",
            "Discuss strategy.",
            Timestamp,
            "https://example.com/template",
            "https://example.com/template-image.png",
            "https://example.com/template-thumb.png",
            0xAA55CC,
            BuildTemplateFields(),
            BuildTemplateButtons(),
            new[] { 12345UL },
            embedId: "template-preview");

        AssertPreviewMatches(GetExpected("template_fc"), result);
    }

    [Fact]
    public void TemplatePreviewMatchesFixtureForOfficer()
    {
        var result = EventPreviewFormatter.Build(
            "Static Meeting",
            "Discuss strategy.",
            Timestamp,
            "https://example.com/template",
            "https://example.com/template-image.png",
            "https://example.com/template-thumb.png",
            0xAA55CC,
            BuildTemplateFields(),
            BuildTemplateButtons(),
            new[] { 12345UL, 67890UL },
            embedId: "template-preview");

        AssertPreviewMatches(GetExpected("template_officer"), result);
    }

    private static List<EmbedFieldDto> BuildEventFields() => new()
    {
        new EmbedFieldDto { Name = "When", Value = "Tonight 8pm", Inline = false },
        new EmbedFieldDto { Name = "Where", Value = "Discord", Inline = true }
    };

    private static List<EmbedFieldDto> BuildTemplateFields() => new()
    {
        new EmbedFieldDto { Name = "Agenda", Value = "Discuss plan", Inline = false },
        new EmbedFieldDto { Name = "Duration", Value = "60 min", Inline = true }
    };

    private static List<EmbedButtonDto> BuildEventButtons() => new()
    {
        new EmbedButtonDto { Label = "Sign Up", CustomId = "rsvp:yes", Style = ButtonStyle.Primary, Width = 80, RowIndex = 0 },
        new EmbedButtonDto { Label = "Info", Url = "https://example.com/info", Style = ButtonStyle.Link, Width = 56, RowIndex = 0 }
    };

    private static List<EmbedButtonDto> BuildTemplateButtons() => new()
    {
        new EmbedButtonDto { Label = "DPS Signup", CustomId = "rsvp:dps", Style = ButtonStyle.Primary, Width = 112, RowIndex = 0 },
        new EmbedButtonDto { Label = "Healer Signup", CustomId = "rsvp:heals", Style = ButtonStyle.Success, Width = 128, RowIndex = 0 },
        new EmbedButtonDto { Label = "Tank Signup", CustomId = "rsvp:tanks", Style = ButtonStyle.Secondary, Width = 104, RowIndex = 1 },
        new EmbedButtonDto { Label = "Guide", Url = "https://example.com/guide", Style = ButtonStyle.Link, Width = 64, RowIndex = 1 }
    };

    [Fact]
    public void AddsDefaultButtonsWhenNoneProvided()
    {
        var result = EventPreviewFormatter.Build(
            "No Buttons",
            "Needs defaults",
            Timestamp,
            null,
            null,
            null,
            null,
            Enumerable.Empty<EmbedFieldDto>(),
            Enumerable.Empty<EmbedButtonDto>(),
            Enumerable.Empty<ulong>(),
            embedId: "no-buttons-preview");

        Assert.Collection(
            result.Buttons,
            b =>
            {
                Assert.Equal("Yes", b.Label);
                Assert.Equal("rsvp:yes", b.CustomId);
                Assert.True(b.Width.HasValue && b.Width.Value > 0);
            },
            b =>
            {
                Assert.Equal("Maybe", b.Label);
                Assert.Equal("rsvp:maybe", b.CustomId);
                Assert.True(b.Width.HasValue && b.Width.Value > 0);
            },
            b =>
            {
                Assert.Equal("No", b.Label);
                Assert.Equal("rsvp:no", b.CustomId);
                Assert.True(b.Width.HasValue && b.Width.Value > 0);
            });
    }

    private static void AssertPreviewMatches(JsonElement expected, EventPreviewFormatter.Result actual)
    {
        Assert.Equal(expected.GetProperty("content").GetString(), actual.Content);
        AssertJsonEqual(expected.GetProperty("embed"), BuildEmbedElement(actual.Embed));
        AssertJsonEqual(expected.GetProperty("buttons"), BuildButtonsElement(actual.Buttons));
    }

    private static JsonElement BuildEmbedElement(EmbedDto embed)
    {
        var obj = new Dictionary<string, object?>
        {
            ["type"] = "rich",
            ["flags"] = 0
        };

        if (!string.IsNullOrWhiteSpace(embed.Title)) obj["title"] = embed.Title;
        if (!string.IsNullOrWhiteSpace(embed.Description)) obj["description"] = embed.Description;
        if (!string.IsNullOrWhiteSpace(embed.Url)) obj["url"] = embed.Url;
        if (embed.Color.HasValue) obj["color"] = (int)embed.Color.Value;
        if (embed.Timestamp.HasValue) obj["timestamp"] = embed.Timestamp.Value.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ss+00:00");

        if (embed.Fields != null && embed.Fields.Count > 0)
        {
            obj["fields"] = embed.Fields.Select(f => new Dictionary<string, object?>
            {
                ["name"] = f.Name,
                ["value"] = f.Value,
                ["inline"] = f.Inline ?? false
            }).ToList();
        }

        if (!string.IsNullOrWhiteSpace(embed.ThumbnailUrl))
        {
            obj["thumbnail"] = new Dictionary<string, object?> { ["url"] = embed.ThumbnailUrl };
        }

        if (!string.IsNullOrWhiteSpace(embed.ImageUrl))
        {
            obj["image"] = new Dictionary<string, object?> { ["url"] = embed.ImageUrl };
        }

        var json = JsonSerializer.Serialize(obj);
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.Clone();
    }

    private static JsonElement BuildButtonsElement(IReadOnlyList<EmbedButtonDto> buttons)
    {
        var list = buttons.Select(b =>
        {
            var dict = new Dictionary<string, object?>
            {
                ["label"] = b.Label,
                ["style"] = b.Style.HasValue ? (int?)b.Style.Value : null,
                ["width"] = b.Width,
                ["rowIndex"] = b.RowIndex
            };
            if (!string.IsNullOrWhiteSpace(b.CustomId)) dict["customId"] = b.CustomId;
            if (!string.IsNullOrWhiteSpace(b.Url)) dict["url"] = b.Url;
            if (!string.IsNullOrWhiteSpace(b.Emoji)) dict["emoji"] = b.Emoji;
            if (b.MaxSignups.HasValue) dict["maxSignups"] = b.MaxSignups;
            return dict;
        }).ToList();

        var json = JsonSerializer.Serialize(list);
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.Clone();
    }

    private static void AssertJsonEqual(JsonElement expected, JsonElement actual)
        => Assert.Equal(expected.GetRawText(), actual.GetRawText());
}
