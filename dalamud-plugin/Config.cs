namespace DalamudPlugin;

public class Config
{
    public bool Enabled { get; set; } = true;

    public string HelperBaseUrl { get; set; } = "http://localhost:5000";

    public int PollIntervalSeconds { get; set; } = 5;

    public string? AuthToken { get; set; }

    public string ChatChannelId { get; set; } = string.Empty;

    public bool UseCharacterName { get; set; } = false;
}
