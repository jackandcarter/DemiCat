using System;

namespace DemiCatPlugin.Emoji;

public sealed class UnicodeEmoji
{
    public string Emoji { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
    public string ImageUrl { get; init; } = string.Empty;
}

public sealed record class CustomEmoji(string Id, string Name, bool Animated, string ImageUrl);

public readonly record struct EmojiLoadStatus(bool Loading, bool Loaded, string? Error)
{
    public bool HasError => !string.IsNullOrEmpty(Error);
}
