using System;
using System.Collections.Generic;

namespace DemiCatPlugin;

internal enum ChannelRefreshError
{
    None,
    TokenMissing,
    InvalidApiUrl,
    Unauthorized,
    Forbidden,
    Generic,
    FeatureDisabled
}

internal readonly struct ChannelRefreshResult
{
    public string Kind { get; }
    public IReadOnlyList<ChannelDto>? Channels { get; }
    public ChannelRefreshError Error { get; }

    public bool HasChannels => Channels != null;

    private ChannelRefreshResult(string kind, IReadOnlyList<ChannelDto>? channels, ChannelRefreshError error)
    {
        Kind = kind;
        Channels = channels;
        Error = error;
    }

    public static ChannelRefreshResult Success(string kind, IReadOnlyList<ChannelDto> channels)
        => new(kind, channels, ChannelRefreshError.None);

    public static ChannelRefreshResult Failure(string kind, ChannelRefreshError error)
        => new(kind, null, error);

    public static ChannelRefreshResult FeatureDisabled(string kind)
        => new(kind, Array.Empty<ChannelDto>(), ChannelRefreshError.FeatureDisabled);
}
