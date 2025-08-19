using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace DemiCatPlugin;

public class SignupPreset
{
    [JsonPropertyName("id")] public string Id { get; set; } = string.Empty;
    [JsonPropertyName("name")] public string Name { get; set; } = string.Empty;
    [JsonPropertyName("buttons")] public List<Template.TemplateButton> Buttons { get; set; } = new();
}
