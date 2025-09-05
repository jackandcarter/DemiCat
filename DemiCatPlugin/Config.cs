using Dalamud.Configuration;
using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DemiCatPlugin;

public class Config : IPluginConfiguration
{ 
    // Required by Dalamud
    public int Version { get; set; } = 4;

    public bool Enabled { get; set; } = true;
    public string ApiBaseUrl { get; set; } = "http://127.0.0.1:5050";
    public string WebSocketPath { get; set; } = "/ws/embeds";
    public int PollIntervalSeconds { get; set; } = 5;
    public string ChatChannelId { get; set; } = string.Empty;
    public string EventChannelId { get; set; } = string.Empty;
    public string FcChannelId { get; set; } = string.Empty;
    public string FcChannelName { get; set; } = string.Empty;
    public string OfficerChannelId { get; set; } = string.Empty;
    public bool EnableFcChat { get; set; } = true;
    public bool EnableFcChatUserSet { get; set; } = false;
    public bool UseCharacterName { get; set; } = false;
    public List<string> Roles { get; set; } = new();
    public List<RoleDto> GuildRoles { get; set; } = new();
    public List<Template> Templates { get; set; } = new();
    public List<SignupPreset> SignupPresets { get; set; } = new();

    [JsonPropertyName("requestStates")]
    public List<RequestState> RequestStates { get; set; } = new();

    [JsonPropertyName("requestsDeltaToken")]
    public string? RequestsDeltaToken { get; set; }

    [JsonPropertyName("syncEnabled")]
    public bool SyncEnabled { get; set; } = false;

    [JsonPropertyName("autoApply")]
    public Dictionary<string, bool> AutoApply { get; set; } = new();

    [JsonPropertyName("penumbraChoices")]
    public Dictionary<string, bool> PenumbraChoices { get; set; } = new();

    [JsonPropertyName("categories")]
    public Dictionary<string, CategoryState> Categories { get; set; } = new();

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? ExtensionData { get; set; }

    public class CategoryState
    {
        [JsonPropertyName("lastPullAt")]
        public DateTimeOffset? LastPullAt { get; set; }

        [JsonPropertyName("seenAssets")]
        public HashSet<string> SeenAssets { get; set; } = new();

        [JsonPropertyName("paused")]
        public bool Paused { get; set; }

        [JsonPropertyName("lastResyncAt")]
        public DateTimeOffset? LastResyncAt { get; set; }

        [JsonExtensionData]
        public Dictionary<string, JsonElement>? ExtensionData { get; set; }
    }

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
        if (Version < 4)
        {
            if (ExtensionData != null)
            {
                if (ExtensionData.TryGetValue("requestStates", out var reqStates) && reqStates.ValueKind == JsonValueKind.Array)
                {
                    try
                    {
                        RequestStates = JsonSerializer.Deserialize<List<RequestState>>(reqStates.GetRawText()) ?? new();
                    }
                    catch
                    {
                        // ignore
                    }
                }
                if (ExtensionData.TryGetValue("requestsDeltaToken", out var tokEl) && tokEl.ValueKind == JsonValueKind.String)
                {
                    RequestsDeltaToken = tokEl.GetString();
                }
            }
            Version = 4;
            ExtensionData = null;
        }
    }
}
