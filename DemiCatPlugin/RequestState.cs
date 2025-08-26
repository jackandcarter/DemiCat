using System;

namespace DemiCatPlugin;

public enum RequestStatus
{
    Open,
    Claimed,
    InProgress,
    AwaitingConfirm,
    Completed
}

public class RequestState
{
    public string Id { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public RequestStatus Status { get; set; }
        = RequestStatus.Open;
    public int Version { get; set; }
        = 0;
}
