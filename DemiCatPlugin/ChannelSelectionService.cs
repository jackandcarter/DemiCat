using System;
using System.Collections.Generic;

namespace DemiCatPlugin;

public class ChannelSelectionService
{
    private readonly Config _config;

    public ChannelSelectionService(Config config)
    {
        _config = config;
        _config.ChannelSelections ??= new Dictionary<string, string>();
    }

    public event Action<string, string, string, string>? ChannelChanged;

    public string GetChannel(string kind, string? guildId)
    {
        guildId ??= string.Empty;
        var key = ChannelKeyHelper.BuildSelectionKey(guildId, kind);
        if (_config.ChannelSelections.TryGetValue(key, out var id))
        {
            return id;
        }

        if (!ChannelKeyHelper.IsDefaultGuild(guildId))
        {
            return string.Empty;
        }

        return kind switch
        {
            ChannelKind.Event => _config.EventChannelId,
            ChannelKind.FcChat => _config.FcChannelId,
            ChannelKind.OfficerChat => _config.OfficerChannelId,
            ChannelKind.Chat => _config.ChatChannelId,
            _ => string.Empty
        } ?? string.Empty;
    }

    public void SetChannel(string kind, string? guildId, string id)
    {
        guildId ??= string.Empty;
        var old = GetChannel(kind, guildId);
        if (old == id) return;

        var key = ChannelKeyHelper.BuildSelectionKey(guildId, kind);
        if (string.IsNullOrEmpty(id))
        {
            _config.ChannelSelections.Remove(key);
        }
        else
        {
            _config.ChannelSelections[key] = id;
        }

        if (ChannelKeyHelper.IsDefaultGuild(guildId))
        {
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
                case ChannelKind.Chat:
                    _config.ChatChannelId = id;
                    break;
            }
        }

        PluginServices.Instance?.PluginInterface.SavePluginConfig(_config);
        ChannelChanged?.Invoke(kind, guildId, old, id);
    }
}
