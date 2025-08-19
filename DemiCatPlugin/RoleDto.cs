using System.Text.Json.Serialization;

namespace DemiCatPlugin;

public class RoleDto
{
    [JsonPropertyName("id")] public ulong Id { get; set; }
    [JsonPropertyName("name")] public string Name { get; set; } = string.Empty;
}

