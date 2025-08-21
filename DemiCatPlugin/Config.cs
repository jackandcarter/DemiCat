using Dalamud.Configuration;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DemiCatPlugin;

public class Config : IPluginConfiguration
{ 
    // Required by Dalamud
    public int Version { get; set; } = 3;

    public bool Enabled { get; set; } = true;
    public string ApiBaseUrl { get; set; } = "http://localhost:5050";
    public string WebSocketPath { get; set; } = "/ws/embeds";
    public int PollIntervalSeconds { get; set; } = 5;
    public string? AuthToken { get; set; }
    public string ChatChannelId { get; set; } = string.Empty;
    public string EventChannelId { get; set; } = string.Empty;
    public string FcChannelId { get; set; } = string.Empty;
    public string FcChannelName { get; set; } = string.Empty;
    public string OfficerChannelId { get; set; } = string.Empty;
    public bool EnableFcChat { get; set; } = true;
    public bool UseCharacterName { get; set; } = false;
    public List<string> Roles { get; set; } = new();
    public List<Template> Templates { get; set; } = new();
    public List<SignupPreset> SignupPresets { get; set; } = new();

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? ExtensionData { get; set; }

    public void Migrate()
    {
        if (Version < 3)
        {
            if (ExtensionData != null)
            {
                if (ExtensionData.TryGetValue("HelperBaseUrl", out var helperUrl) && helperUrl.ValueKind == JsonValueKind.String)
                {
                    ApiBaseUrl = helperUrl.GetString() ?? ApiBaseUrl;
                }
                else if (ExtensionData.TryGetValue("ServerAddress", out var serverUrl) && serverUrl.ValueKind == JsonValueKind.String)
                {
                    ApiBaseUrl = serverUrl.GetString() ?? ApiBaseUrl;
                }
            }
            Version = 3;
            ExtensionData = null;
        }
    }
}
