using System.Collections.Generic;
using System.Numerics;
using System.Text.Json.Serialization;
using Dalamud.Interface.Textures;

namespace DemiCatPlugin;

public class PresenceDto
{
    [JsonPropertyName("id")] public string Id { get; set; } = string.Empty;
    [JsonPropertyName("name")] public string Name { get; set; } = string.Empty;
    [JsonPropertyName("status")] public string Status { get; set; } = string.Empty;
    [JsonPropertyName("status_text")] public string? StatusText { get; set; }
    [JsonPropertyName("avatar_url")] public string? AvatarUrl { get; set; }
    [JsonIgnore] public ISharedImmediateTexture? AvatarTexture { get; set; }
    [JsonPropertyName("banner_url")] public string? BannerUrl { get; set; }
    [JsonIgnore] public ISharedImmediateTexture? BannerTexture { get; set; }
    private uint? _accentColorValue;
    [JsonPropertyName("accent_color")]
    public uint? AccentColorValue
    {
        get => _accentColorValue;
        set
        {
            _accentColorValue = value;
            AccentColor = value.HasValue ? ColorUtils.RgbToVector4(value.Value) : null;
        }
    }
    [JsonIgnore] public Vector4? AccentColor { get; private set; }
    [JsonPropertyName("roles")] public List<string> Roles { get; set; } = new();
    [JsonPropertyName("role_details")] public List<PresenceRoleDto> RoleDetails { get; set; } = new();
}

public class PresenceRoleDto
{
    [JsonPropertyName("id")] public string Id { get; set; } = string.Empty;
    [JsonPropertyName("name")] public string Name { get; set; } = string.Empty;
}
