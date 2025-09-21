using System.Text.Json.Serialization;

namespace DemiCatPlugin;

public class RoleDto
{
    [JsonPropertyName("id")] public string Id { get; set; } = string.Empty;
    [JsonPropertyName("name")] public string Name { get; set; } = string.Empty;
    [JsonPropertyName("position")] public int Position { get; set; }
    [JsonPropertyName("hoist")] public bool Hoist { get; set; }
    [JsonPropertyName("tags")] public RoleTagsDto? Tags { get; set; }

    [JsonIgnore]
    public bool IsPremiumSubscriber => Tags?.PremiumSubscriber == true;
}

public class RoleTagsDto
{
    [JsonPropertyName("premium_subscriber")]
    public bool PremiumSubscriber { get; set; }
}

