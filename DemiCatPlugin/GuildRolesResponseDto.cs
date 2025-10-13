using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace DemiCatPlugin;

internal sealed class GuildRolesResponseDto
{
    [JsonPropertyName("roles")]
    public List<RoleDto> Roles { get; set; } = new();

    private List<string> _mentionRoleIds = new();

    [JsonIgnore]
    public List<string> MentionRoleIds
    {
        get => _mentionRoleIds;
        set => _mentionRoleIds = value ?? new List<string>();
    }

    [JsonPropertyName("mention_role_ids")]
    [JsonInclude]
    public List<string> LegacyMentionRoleIds
    {
        get => _mentionRoleIds;
        set => MentionRoleIds = value;
    }

    [JsonPropertyName("mentionRoleIds")]
    [JsonInclude]
    public List<string> MentionRoleIdsCamel
    {
        get => _mentionRoleIds;
        set => MentionRoleIds = value;
    }
}

