using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;
using Dalamud.Interface.Textures;
using DiscordHelper;

namespace DemiCatPlugin;

public class DiscordMessageDto
{
    public string Id { get; set; } = string.Empty;
    public string ChannelId { get; set; } = string.Empty;
    public DiscordUserDto Author { get; set; } = new();
    public string Content { get; set; } = string.Empty;
    public List<EmbedDto>? Embeds { get; set; }
    public List<DiscordAttachmentDto>? Attachments { get; set; }
    public List<DiscordMentionDto>? Mentions { get; set; }
    public MessageReferenceDto? Reference { get; set; }
    public List<ButtonComponentDto>? Components { get; set; }
    public DateTime Timestamp { get; set; }
    public DateTime? EditedTimestamp { get; set; }
    public List<ReactionDto>? Reactions { get; set; }
    [JsonIgnore] public ISharedImmediateTexture? AvatarTexture { get; set; }
}

public class DiscordUserDto
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string? AvatarUrl { get; set; }
}

public class DiscordMentionDto
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Kind { get; set; } = "user";
}

public class DiscordAttachmentDto
{
    public string Url { get; set; } = string.Empty;
    public string? Filename { get; set; }
    public string? ContentType { get; set; }
    [JsonIgnore] public ISharedImmediateTexture? Texture { get; set; }
}

public class MessageReferenceDto
{
    public string MessageId { get; set; } = string.Empty;
    public string ChannelId { get; set; } = string.Empty;
}

public class ReactionDto
{
    public string Emoji { get; set; } = string.Empty;
    public string? EmojiId { get; set; }
    public bool IsAnimated { get; set; }
    public int Count { get; set; }
    public bool Me { get; set; }
    [JsonIgnore]
    public ISharedImmediateTexture? Texture { get; set; }
}

public class ButtonComponentDto
{
    public string Label { get; set; } = string.Empty;
    public string? CustomId { get; set; }
    public string? Url { get; set; }
    public ButtonStyle Style { get; set; } = ButtonStyle.Primary;
    public string? Emoji { get; set; }
}
