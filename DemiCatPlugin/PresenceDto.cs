using System;
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

    private string? _statusText;
    private bool _statusTextProvided;

    [JsonIgnore]
    public bool StatusTextProvided => _statusTextProvided;

    [JsonIgnore]
    public string? StatusText
    {
        get => _statusText;
        set
        {
            _statusTextProvided = true;
            _statusText = string.IsNullOrWhiteSpace(value) ? null : value;
        }
    }

    [JsonPropertyName("status_text")]
    [JsonInclude]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? LegacyStatusText
    {
        get => _statusText;
        set => StatusText = value;
    }

    [JsonPropertyName("statusText")]
    [JsonInclude]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? StatusTextCamel
    {
        get => _statusText;
        set => StatusText = value;
    }

    private string? _avatarUrl;

    [JsonIgnore]
    public string? AvatarUrl
    {
        get => _avatarUrl;
        set => _avatarUrl = string.IsNullOrWhiteSpace(value) ? null : value;
    }

    [JsonPropertyName("avatar_url")]
    [JsonInclude]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? LegacyAvatarUrl
    {
        get => _avatarUrl;
        set => AvatarUrl = value;
    }

    [JsonPropertyName("avatarUrl")]
    [JsonInclude]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? AvatarUrlCamel
    {
        get => _avatarUrl;
        set => AvatarUrl = value;
    }

    [JsonIgnore] public ISharedImmediateTexture? AvatarTexture { get; set; }
    [JsonIgnore] public bool AvatarLoadRequested { get; set; }
    [JsonIgnore] public bool AvatarLoadFailed { get; set; }
    [JsonIgnore] public DateTime? AvatarFailedAt { get; set; }

    private string? _bannerUrl;

    [JsonIgnore]
    public string? BannerUrl
    {
        get => _bannerUrl;
        set => _bannerUrl = string.IsNullOrWhiteSpace(value) ? null : value;
    }

    [JsonPropertyName("banner_url")]
    [JsonInclude]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? LegacyBannerUrl
    {
        get => _bannerUrl;
        set => BannerUrl = value;
    }

    [JsonPropertyName("bannerUrl")]
    [JsonInclude]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? BannerUrlCamel
    {
        get => _bannerUrl;
        set => BannerUrl = value;
    }

    [JsonIgnore] public ISharedImmediateTexture? BannerTexture { get; set; }
    [JsonIgnore] public bool BannerLoadRequested { get; set; }
    [JsonIgnore] public bool BannerLoadFailed { get; set; }
    [JsonIgnore] public DateTime? BannerFailedAt { get; set; }

    private uint? _accentColorValue;

    [JsonIgnore]
    public uint? AccentColorValue
    {
        get => _accentColorValue;
        set
        {
            _accentColorValue = value;
            AccentColor = value.HasValue ? ColorUtils.RgbToVector4(value.Value) : null;
        }
    }

    [JsonPropertyName("accent_color")]
    [JsonInclude]
    public uint? LegacyAccentColor
    {
        get => _accentColorValue;
        set => AccentColorValue = value;
    }

    [JsonPropertyName("accentColor")]
    [JsonInclude]
    public uint? AccentColorCamel
    {
        get => _accentColorValue;
        set => AccentColorValue = value;
    }

    [JsonIgnore] public Vector4? AccentColor { get; private set; }

    [JsonIgnore]
    public long Revision { get; private set; }

    private List<string> _roles = new();
    private bool _rolesProvided;

    [JsonIgnore]
    public bool RolesProvided => _rolesProvided;

    [JsonPropertyName("roles")]
    public List<string> Roles
    {
        get => _roles;
        set
        {
            _rolesProvided = true;
            _roles = value ?? new List<string>();
        }
    }

    private List<PresenceRoleDto> _roleDetails = new();
    private bool _roleDetailsProvided;

    [JsonIgnore]
    public bool RoleDetailsProvided => _roleDetailsProvided;

    [JsonIgnore]
    public List<PresenceRoleDto> RoleDetails
    {
        get => _roleDetails;
        set
        {
            _roleDetailsProvided = true;
            _roleDetails = value ?? new List<PresenceRoleDto>();
        }
    }

    [JsonPropertyName("role_details")]
    [JsonInclude]
    public List<PresenceRoleDto> LegacyRoleDetails
    {
        get => _roleDetails;
        set => RoleDetails = value;
    }

    [JsonPropertyName("roleDetails")]
    [JsonInclude]
    public List<PresenceRoleDto> RoleDetailsCamel
    {
        get => _roleDetails;
        set => RoleDetails = value;
    }

    public void Touch()
    {
        if (Revision == long.MaxValue)
        {
            Revision = 0;
        }
        else
        {
            Revision++;
        }
    }

    public void ResetTransientState()
    {
        AvatarLoadRequested = false;
        BannerLoadRequested = false;
        AvatarLoadFailed = false;
        BannerLoadFailed = false;
        AvatarFailedAt = null;
        BannerFailedAt = null;
    }
}

public class PresenceRoleDto
{
    [JsonPropertyName("id")] public string Id { get; set; } = string.Empty;
    [JsonPropertyName("name")] public string Name { get; set; } = string.Empty;
}
