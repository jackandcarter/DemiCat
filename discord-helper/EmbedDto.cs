namespace DiscordHelper;

public class EmbedDto
{
    public string? Id { get; set; }
    public DateTimeOffset? Timestamp { get; set; }
    public uint? Color { get; set; }
    public string? AuthorName { get; set; }
    public string? AuthorIconUrl { get; set; }
    public string? Title { get; set; }
    public string? Description { get; set; }
    public List<EmbedFieldDto>? Fields { get; set; }
    public string? ThumbnailUrl { get; set; }
    public string? ImageUrl { get; set; }
    public List<EmbedButtonDto>? Buttons { get; set; }
    public ulong? ChannelId { get; set; }
    public List<ulong>? Mentions { get; set; }
}

public class EmbedFieldDto
{
    public string Name { get; set; } = string.Empty;
    public string Value { get; set; } = string.Empty;
}

public class EmbedButtonDto
{
    public string Label { get; set; } = string.Empty;
    public string? Url { get; set; }
    public string? CustomId { get; set; }
}
