using System.Text.Json.Serialization;

namespace DemiCatPlugin;

public class ChannelValidationResponse
{
    [JsonPropertyName("ok")]
    public bool Ok { get; set; }

    [JsonPropertyName("reason")]
    public string? Reason { get; set; }

    [JsonPropertyName("guildId")]
    public string? GuildId { get; set; }

    [JsonPropertyName("kind")]
    public string? Kind { get; set; }

    [JsonPropertyName("name")]
    public string? Name { get; set; }
}
