using Dalamud.Configuration;
using System.Collections.Generic;

namespace DemiCatPlugin;

public class Config : IPluginConfiguration
{ 
    // Required by Dalamud
    public int Version { get; set; } = 2;

    public bool Enabled { get; set; } = true;
    public string HelperBaseUrl { get; set; } = "http://localhost:8000";
    public string WebSocketPath { get; set; } = "/ws/embeds";
    public int PollIntervalSeconds { get; set; } = 5;
    public string? AuthToken { get; set; }
    public string ServerAddress { get; set; } = "http://localhost:8000";
    public string ChatChannelId { get; set; } = string.Empty;
    public string EventChannelId { get; set; } = string.Empty;
    public string FcChannelId { get; set; } = string.Empty;
    public string FcChannelName { get; set; } = string.Empty;
    public string OfficerChannelId { get; set; } = string.Empty;
    public bool EnableFcChat { get; set; } = false;
    public bool UseCharacterName { get; set; } = false;
    public List<string> Roles { get; set; } = new();
    public List<Template> Templates { get; set; } = new();
}
