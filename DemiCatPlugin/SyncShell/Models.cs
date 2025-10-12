using System;
using System.Collections.Generic;
using System.Text.Json;
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

public sealed class MembershipEntryDto
{
    [JsonPropertyName("id")]
    public string? Id { get; set; }

    [JsonPropertyName("displayName")]
    public string? DisplayName { get; set; }

    [JsonPropertyName("presence")]
    public string? Presence { get; set; }

    [JsonPropertyName("syncStatus")]
    public string? SyncStatus { get; set; }

    [JsonPropertyName("lastSeen")]
    public string? LastSeen { get; set; }

    [JsonPropertyName("syncedAt")]
    public string? SyncedAt { get; set; }

    [JsonPropertyName("tokenLinked")]
    public bool TokenLinked { get; set; }
}

public sealed class MembershipsResponseDto
{
    [JsonPropertyName("members")]
    public List<MembershipEntryDto> Members { get; set; } = new();

    [JsonPropertyName("currentlySynced")]
    public List<MembershipEntryDto> CurrentlySynced { get; set; } = new();

    [JsonPropertyName("pendingApprovals")]
    public List<JsonElement> PendingApprovals { get; set; } = new();

    [JsonPropertyName("invites")]
    public List<JsonElement> Invites { get; set; } = new();
}

public sealed class PresenceUpdateDto
{
    [JsonPropertyName("activeMemberIds")]
    public List<long> ActiveMemberIds { get; set; } = new();
}

public sealed class PresenceResponseEntryDto
{
    [JsonPropertyName("id")]
    public string? Id { get; set; }

    [JsonPropertyName("displayName")]
    public string? DisplayName { get; set; }

    [JsonPropertyName("presence")]
    public string? Presence { get; set; }

    [JsonPropertyName("syncStatus")]
    public string? SyncStatus { get; set; }

    [JsonPropertyName("lastSeen")]
    public string? LastSeen { get; set; }

    [JsonPropertyName("syncedAt")]
    public string? SyncedAt { get; set; }

    [JsonPropertyName("tokenLinked")]
    public bool TokenLinked { get; set; }
}

public sealed class PresenceResponseDto
{
    [JsonPropertyName("presence")]
    public List<PresenceResponseEntryDto> Presence { get; set; } = new();

    [JsonPropertyName("currentlySynced")]
    public List<PresenceResponseEntryDto> CurrentlySynced { get; set; } = new();
}

public sealed class SyncshellMemberStatus
{
    public string Id { get; init; } = string.Empty;

    public string DisplayName { get; init; } = string.Empty;

    public string Presence { get; init; } = "offline";

    public string? SyncStatus { get; init; }

    public DateTimeOffset? LastSeen { get; init; }

    public DateTimeOffset? SyncedAt { get; init; }

    public bool TokenLinked { get; init; }

    [JsonIgnore]
    public bool IsActive => string.Equals(SyncStatus, "syncing", StringComparison.OrdinalIgnoreCase);
}
