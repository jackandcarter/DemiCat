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
    private bool _premiumSubscriber;

    [JsonIgnore]
    public bool PremiumSubscriber
    {
        get => _premiumSubscriber;
        set => _premiumSubscriber = value;
    }

    [JsonPropertyName("premium_subscriber")]
    [JsonInclude]
    public bool LegacyPremiumSubscriber
    {
        get => _premiumSubscriber;
        set => _premiumSubscriber = value;
    }

    [JsonPropertyName("premiumSubscriber")]
    [JsonInclude]
    public bool PremiumSubscriberCamel
    {
        get => _premiumSubscriber;
        set => _premiumSubscriber = value;
    }
}

