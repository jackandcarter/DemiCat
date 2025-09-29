using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace DemiCatPlugin;

internal sealed class GuildRolesResponseDto
{
    [JsonPropertyName("roles")]
    public List<RoleDto> Roles { get; set; } = new();

    [JsonPropertyName("mention_role_ids")]
    public List<string> MentionRoleIds { get; set; } = new();
}

