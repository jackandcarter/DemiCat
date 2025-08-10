namespace DemiCatPlugin;

public class Config
{
    public bool Enabled { get; set; } = true;

    public string HelperBaseUrl { get; set; } = "http://localhost:3000";

    public int PollIntervalSeconds { get; set; } = 5;

    public string? AuthToken { get; set; }

    public string SyncKey { get; set; } = string.Empty;

    public string ChatChannelId { get; set; } = string.Empty;

    public string EventChannelId { get; set; } = string.Empty;

    public string FcChannelId { get; set; } = string.Empty;

    public string FcChannelName { get; set; } = string.Empty;

    public string OfficerChannelId { get; set; } = string.Empty;

    public bool EnableFcChat { get; set; } = false;

    public bool UseCharacterName { get; set; } = false;
}
