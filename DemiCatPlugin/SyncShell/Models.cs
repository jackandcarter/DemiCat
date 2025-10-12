using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace DemiCatPlugin.SyncShell;

public sealed class BlobRef
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("sha256")]
    public string Sha256 { get; set; } = string.Empty;

    [JsonPropertyName("size")]
    public long Size { get; set; }
}

public sealed class AppearanceMeta
{
    [JsonPropertyName("actorHash")]
    public string ActorHash { get; set; } = string.Empty;

    [JsonPropertyName("glamourer")]
    public string? GlamourerJson { get; set; }

    [JsonPropertyName("blobs")]
    public List<BlobRef> Blobs { get; } = new();
}

public sealed class PublishPayload
{
    [JsonPropertyName("discordId")]
    public string DiscordId { get; set; } = string.Empty;

    [JsonPropertyName("appearance")]
    public AppearanceMeta Appearance { get; set; } = new();

    [JsonPropertyName("complete")]
    public bool Complete { get; set; }
}

public sealed class PublishResultDto
{
    [JsonPropertyName("missing")]
    public List<string> Missing { get; set; } = new();
}

public sealed class UserMetaDto
{
    [JsonPropertyName("discordId")]
    public string DiscordId { get; set; } = string.Empty;

    [JsonPropertyName("appearance")]
    public AppearanceMeta? Appearance { get; set; }

    [JsonPropertyName("hash")]
    public string? Hash { get; set; }
}

public sealed class ApplyPayload
{
    [JsonPropertyName("discordId")]
    public string DiscordId { get; set; } = string.Empty;

    [JsonPropertyName("appearance")]
    public AppearanceMeta Appearance { get; set; } = new();
}

public sealed class PresenceSnapshot
{
    public PresenceSnapshot(IReadOnlyList<string> discordIds)
    {
        DiscordIds = discordIds;
        Timestamp = DateTimeOffset.UtcNow;
    }

    [JsonPropertyName("discordIds")]
    public IReadOnlyList<string> DiscordIds { get; }

    [JsonPropertyName("timestamp")]
    public DateTimeOffset Timestamp { get; }
}
