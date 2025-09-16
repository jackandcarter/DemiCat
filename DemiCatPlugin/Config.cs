using Dalamud.Configuration;
using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DemiCatPlugin;

public class Config : IPluginConfiguration
{
    // Required by Dalamud
    public int Version { get; set; } = 5;

    public bool Enabled { get; set; } = true;
    public string ApiBaseUrl { get; set; } = "http://127.0.0.1:5050";
    public string WebSocketPath { get; set; } = "/ws/embeds";
    public int PollIntervalSeconds { get; set; } = 5;
    [JsonPropertyName("guildId")]
    public string GuildId { get; set; } = string.Empty;
    public string ChatChannelId { get; set; } = string.Empty;
    public Dictionary<string, long> ChatCursors { get; set; } = new();
    public Dictionary<string, string> ChannelSelections { get; set; } = new();
    public string EventChannelId { get; set; } = string.Empty;
    public string FcChannelId { get; set; } = string.Empty;
    public string FcChannelName { get; set; } = string.Empty;
    public string OfficerChannelId { get; set; } = string.Empty;
    public bool EnableFcChat { get; set; } = true;
    public bool EnableFcChatUserSet { get; set; } = false;

    [JsonPropertyName("syncedChat")]
    public bool SyncedChat { get; set; } = true;
    [JsonPropertyName("events")]
    public bool Events { get; set; } = true;
    [JsonPropertyName("templatesEnabled")]
    public bool Templates { get; set; } = true;
    [JsonPropertyName("requests")]
    public bool Requests { get; set; } = true;
    [JsonPropertyName("officer")]
    public bool Officer { get; set; } = true;
    [JsonPropertyName("fcSyncShell")]
    public bool FCSyncShell { get; set; } = false;
    public bool UseCharacterName { get; set; } = false;
    public List<string> Roles { get; set; } = new();
    [JsonPropertyName("mentionRoleIds")]
    public List<string> MentionRoleIds { get; set; } = new();
    public List<RoleDto> GuildRoles { get; set; } = new();
    [JsonPropertyName("templates")]
    public List<Template> TemplateData { get; set; } = new();
    public List<SignupPreset> SignupPresets { get; set; } = new();

    [JsonPropertyName("requestStates")]
    public List<RequestState> RequestStates { get; set; } = new();

    [JsonPropertyName("requestsDeltaToken")]
    public string? RequestsDeltaToken { get; set; }


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
        if (Version < 5)
        {
            ChannelSelections ??= new Dictionary<string, string>();

            void EnsureSelection(string kind, string? channelId)
            {
                if (string.IsNullOrEmpty(channelId)) return;
                var key = ChannelKeyHelper.BuildSelectionKey(GuildId, kind);
                if (!ChannelSelections.ContainsKey(key))
                {
                    ChannelSelections[key] = channelId;
                }
            }

            EnsureSelection(ChannelKind.Chat, ChatChannelId);
            EnsureSelection(ChannelKind.Event, EventChannelId);
            EnsureSelection(ChannelKind.FcChat, FcChannelId);
            EnsureSelection(ChannelKind.OfficerChat, OfficerChannelId);

            if (ChatCursors.Count > 0)
            {
                var migrated = new Dictionary<string, long>();
                foreach (var kvp in ChatCursors)
                {
                    var channelId = kvp.Key;
                    var cursor = kvp.Value;
                    if (string.IsNullOrEmpty(channelId))
                        continue;

                    var kinds = new List<string>();
                    if (!string.IsNullOrEmpty(EventChannelId) && channelId == EventChannelId)
                        kinds.Add(ChannelKind.Event);
                    if (!string.IsNullOrEmpty(FcChannelId) && channelId == FcChannelId)
                        kinds.Add(ChannelKind.FcChat);
                    if (!string.IsNullOrEmpty(OfficerChannelId) && channelId == OfficerChannelId)
                        kinds.Add(ChannelKind.OfficerChat);
                    if (!string.IsNullOrEmpty(ChatChannelId) && channelId == ChatChannelId)
                        kinds.Add(ChannelKind.Chat);
                    if (kinds.Count == 0)
                        kinds.Add(ChannelKind.FcChat);

                    foreach (var kind in kinds)
                    {
                        var key = ChannelKeyHelper.BuildCursorKey(GuildId, kind, channelId);
                        if (!migrated.ContainsKey(key))
                        {
                            migrated[key] = cursor;
                        }
                    }
                }
                ChatCursors = migrated;
            }

            Version = 5;
            ExtensionData = null;
        }
    }
}
