using System;
using System.Collections.Generic;

namespace DiscordHelper;

public class EmbedDto
{
    public string Id { get; set; } = string.Empty;
    public DateTimeOffset? Timestamp { get; set; }
    public uint? Color { get; set; }
    public string? AuthorName { get; set; }
    public string? AuthorIconUrl { get; set; }
    public List<EmbedAuthorDto>? Authors { get; set; }
    public string? Title { get; set; }
    public string? Description { get; set; }
    public string? Url { get; set; }
    public List<EmbedFieldDto>? Fields { get; set; }
    public string? ThumbnailUrl { get; set; }
    public string? ImageUrl { get; set; }
    public string? ProviderName { get; set; }
    public string? ProviderUrl { get; set; }
    public string? FooterText { get; set; }
    public string? FooterIconUrl { get; set; }
    public string? VideoUrl { get; set; }
    public int? VideoWidth { get; set; }
    public int? VideoHeight { get; set; }
    public List<EmbedButtonDto>? Buttons { get; set; }
    public ulong? ChannelId { get; set; }
    public List<ulong>? Mentions { get; set; }
}

public class EmbedFieldDto
{
    public string Name { get; set; } = string.Empty;
    public string Value { get; set; } = string.Empty;
    public bool? Inline { get; set; }
}

public class EmbedButtonDto
{
    public string Label { get; set; } = string.Empty;
    public string? Url { get; set; }
    public string? CustomId { get; set; }
    public string? Emoji { get; set; }
    public ButtonStyle? Style { get; set; }
    public int? MaxSignups { get; set; }
    public int? Width { get; set; }
    public int? Height { get; set; }
    public int? RowIndex { get; set; }
}

public class EmbedAuthorDto
{
    public string? Name { get; set; }
    public string? Url { get; set; }
    public string? IconUrl { get; set; }
}

public enum ButtonStyle
{
    Primary = 1,
    Secondary = 2,
    Success = 3,
    Danger = 4,
    Link = 5,
}
