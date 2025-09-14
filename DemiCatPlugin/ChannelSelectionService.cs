using System;
using System.Collections.Generic;

namespace DemiCatPlugin;

public class ChannelSelectionService
{
    private readonly Config _config;
    private readonly Dictionary<string, string> _channels = new();

    public ChannelSelectionService(Config config)
    {
        _config = config;
        _channels[ChannelKind.Event] = config.EventChannelId;
        _channels[ChannelKind.FcChat] = config.FcChannelId;
        _channels[ChannelKind.OfficerChat] = config.OfficerChannelId;
    }

    public event Action<string, string, string>? ChannelChanged;

    public string GetChannel(string kind)
        => _channels.TryGetValue(kind, out var id) ? id : string.Empty;

    public void SetChannel(string kind, string id)
    {
        var old = GetChannel(kind);
        if (old == id) return;
        _channels[kind] = id;
        switch (kind)
        {
            case ChannelKind.Event:
                _config.EventChannelId = id;
                break;
            case ChannelKind.FcChat:
                _config.FcChannelId = id;
                break;
            case ChannelKind.OfficerChat:
                _config.OfficerChannelId = id;
                break;
        }
        PluginServices.Instance?.PluginInterface.SavePluginConfig(_config);
        ChannelChanged?.Invoke(kind, old, id);
    }
}

