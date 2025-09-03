using System.Text.Json.Serialization;
using System.Collections.Generic;
using Dalamud.Interface.Textures;

namespace DemiCatPlugin;

public class PresenceDto
{
    [JsonPropertyName("id")] public string Id { get; set; } = string.Empty;
    [JsonPropertyName("name")] public string Name { get; set; } = string.Empty;
    [JsonPropertyName("status")] public string Status { get; set; } = string.Empty;
    [JsonPropertyName("avatarUrl")] public string? AvatarUrl { get; set; }
    [JsonIgnore] public ISharedImmediateTexture? AvatarTexture { get; set; }
    [JsonPropertyName("roles")] public List<string> Roles { get; set; } = new();
}
