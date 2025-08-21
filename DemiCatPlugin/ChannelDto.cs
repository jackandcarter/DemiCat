using System.Text.Json.Serialization;

namespace DemiCatPlugin;

public class ChannelDto
{
    [JsonPropertyName("id")] public string Id { get; set; } = string.Empty;
    [JsonPropertyName("name")] public string Name { get; set; } = string.Empty;
}
