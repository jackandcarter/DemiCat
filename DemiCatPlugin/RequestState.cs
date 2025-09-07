using System;

namespace DemiCatPlugin;

public enum RequestType
{
    Item,
    Run,
    Event
}

public enum RequestStatus
{
    Open,
    Claimed,
    InProgress,
    AwaitingConfirm,
    Completed,
    Cancelled
}

public enum RequestUrgency
{
    Low,
    Medium,
    High
}

public class RequestState
{
    public string Id { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public RequestStatus Status { get; set; } = RequestStatus.Open;
    public RequestType Type { get; set; } = RequestType.Item;
    public RequestUrgency Urgency { get; set; } = RequestUrgency.Low;
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
    public uint? AssigneeId { get; set; }
        = null;

    public string Description { get; set; } = string.Empty;
    public string CreatedBy { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; } = DateTime.MinValue;

    internal GameDataCache.CachedEntry? ItemData { get; set; } = null;
    internal GameDataCache.CachedEntry? DutyData { get; set; } = null;
}
