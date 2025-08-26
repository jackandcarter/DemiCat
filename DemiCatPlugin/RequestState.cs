using System;

namespace DemiCatPlugin;

public enum RequestStatus
{
    Open,
    Claimed,
    InProgress,
    AwaitingConfirm,
    Completed,
    Cancelled
}

public class RequestState
{
    public string Id { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public RequestStatus Status { get; set; }
        = RequestStatus.Open;
    public int Version { get; set; }
        = 0;
    public uint? ItemId { get; set; }
        = null;
    public uint? DutyId { get; set; }
        = null;
    public bool Hq { get; set; }
        = false;
    public int Quantity { get; set; }
        = 0;
}
