using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace DemiCatPlugin.SyncShell;

/// <summary>
/// Root manifest payload exchanged with the SyncShell service.
/// </summary>
public sealed class SyncManifest
{
    [JsonPropertyName("protocolVersion")]
    public int ProtocolVersion { get; set; } = 1;

    [JsonPropertyName("clientId")]
    public string? ClientId { get; set; }

    [JsonPropertyName("appearance")]
    public Appearance Appearance { get; set; } = new();

    [JsonPropertyName("collections")]
    public List<CollectionDelta> Collections { get; } = new();

    [JsonPropertyName("sizeHints")]
    public List<SizeHint> SizeHints { get; } = new();

    [JsonPropertyName("meta")]
    public List<MetaEntry> Meta { get; } = new();

    [JsonPropertyName("wantBlobs")]
    public WantBlobs WantBlobs { get; set; } = new();

    [JsonPropertyName("presence")]
    public PresenceStatus Presence { get; set; } = new();
}

/// <summary>
/// Appearance specific data derived from the user's active Penumbra collections.
/// </summary>
public sealed class Appearance
{
    [JsonPropertyName("collectionId")]
    public string? CollectionId { get; set; }

    [JsonPropertyName("fingerprint")]
    public string? Fingerprint { get; set; }

    [JsonPropertyName("activeMods")]
    public List<string> ActiveMods { get; } = new();

    [JsonPropertyName("customState")]
    public Dictionary<string, string> CustomState { get; } = new();

    [JsonPropertyName("lastUpdated")]
    public DateTimeOffset? LastUpdated { get; set; }
}

/// <summary>
/// Delta information for a single collection describing mods, patches and metadata changes.
/// </summary>
public sealed class CollectionDelta
{
    [JsonPropertyName("collectionId")]
    public string CollectionId { get; set; } = string.Empty;

    [JsonPropertyName("fingerprint")]
    public string? Fingerprint { get; set; }

    [JsonPropertyName("mods")]
    public List<ModEntry> Mods { get; } = new();

    [JsonPropertyName("removedMods")]
    public List<string> RemovedMods { get; } = new();

    [JsonPropertyName("patches")]
    public List<PatchEntry> Patches { get; } = new();

    [JsonPropertyName("removedPatches")]
    public List<string> RemovedPatches { get; } = new();

    [JsonPropertyName("meta")]
    public List<MetaEntry> Meta { get; } = new();
}

/// <summary>
/// Individual mod entry containing a hashed file manifest and supplemental metadata.
/// </summary>
public sealed class ModEntry
{
    [JsonPropertyName("modId")]
    public string ModId { get; set; } = string.Empty;

    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("hash")]
    public string Hash { get; set; } = string.Empty;

    [JsonPropertyName("size")]
    public long Size { get; set; }

    [JsonPropertyName("files")]
    public List<FileHash> Files { get; } = new();

    [JsonPropertyName("options")]
    public Dictionary<string, string> Options { get; } = new();

    [JsonPropertyName("tags")]
    public List<string> Tags { get; } = new();

    [JsonPropertyName("enabled")]
    public bool Enabled { get; set; } = true;

    [JsonPropertyName("patches")]
    public List<PatchEntry> Patches { get; } = new();

    [JsonPropertyName("meta")]
    public List<MetaEntry> Meta { get; } = new();
}

/// <summary>
/// Hash information for a single file within a mod archive.
/// </summary>
public sealed class FileHash
{
    [JsonPropertyName("path")]
    public string Path { get; set; } = string.Empty;

    [JsonPropertyName("hash")]
    public string Hash { get; set; } = string.Empty;

    [JsonPropertyName("size")]
    public long Size { get; set; }
}

/// <summary>
/// Patch entries describe lightweight replacements or metadata updates not tied to a mod archive.
/// </summary>
public sealed class PatchEntry
{
    [JsonPropertyName("path")]
    public string Path { get; set; } = string.Empty;

    [JsonPropertyName("hash")]
    public string Hash { get; set; } = string.Empty;

    [JsonPropertyName("size")]
    public long Size { get; set; }

    [JsonPropertyName("kind")]
    public string? Kind { get; set; }

    [JsonPropertyName("target")]
    public string? Target { get; set; }
}

/// <summary>
/// Arbitrary metadata entries that can be attached to manifests, collections or mods.
/// </summary>
public sealed class MetaEntry
{
    [JsonPropertyName("key")]
    public string Key { get; set; } = string.Empty;

    [JsonPropertyName("value")]
    public string Value { get; set; } = string.Empty;

    [JsonPropertyName("scope")]
    public string? Scope { get; set; }
}

/// <summary>
/// Provides file size estimates to optimise chunking heuristics during transfers.
/// </summary>
public sealed class SizeHint
{
    [JsonPropertyName("path")]
    public string Path { get; set; } = string.Empty;

    [JsonPropertyName("size")]
    public long Size { get; set; }

    [JsonPropertyName("compressedSize")]
    public long? CompressedSize { get; set; }
}

/// <summary>
/// Payload describing the binary blobs or individual chunks required from the SyncShell vault.
/// </summary>
public sealed class WantBlobs
{
    [JsonPropertyName("blobs")]
    public List<string> Blobs { get; } = new();

    [JsonPropertyName("chunks")]
    public List<BlobChunk> Chunks { get; } = new();

    [JsonPropertyName("sizeHints")]
    public List<SizeHint> SizeHints { get; } = new();
}

/// <summary>
/// Individual chunk reference for partial blob transfers.
/// </summary>
public sealed class BlobChunk
{
    [JsonPropertyName("blob")]
    public string Blob { get; set; } = string.Empty;

    [JsonPropertyName("offset")]
    public long Offset { get; set; }

    [JsonPropertyName("length")]
    public int Length { get; set; }

    [JsonPropertyName("hash")]
    public string? Hash { get; set; }
}

/// <summary>
/// Current status of a paired client for presence broadcasting.
/// </summary>
public sealed class PresenceStatus
{
    [JsonPropertyName("state")]
    public string State { get; set; } = "offline";

    [JsonPropertyName("message")]
    public string? Message { get; set; }

    [JsonPropertyName("updatedAt")]
    public DateTimeOffset? UpdatedAt { get; set; }
}
