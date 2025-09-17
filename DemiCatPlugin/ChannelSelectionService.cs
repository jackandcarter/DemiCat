using System;
using System.Collections.Generic;

namespace DemiCatPlugin;

public class ChannelSelectionService
{
    private const string EventSelectionPrefix = "Event:";
    private static readonly string NormalizedEventKind = ChannelKeyHelper.NormalizeKind(ChannelKind.Event);
    private readonly Config _config;

    public ChannelSelectionService(Config config)
    {
        _config = config;
        _config.ChannelSelections ??= new Dictionary<string, string>();
    }

    public event Action<string, string, string, string>? ChannelChanged;

    private static string BuildEventSelectionKey(string normalizedGuildId)
        => $"{EventSelectionPrefix}{normalizedGuildId}";

    public string GetChannel(string kind, string? guildId)
        => GetChannel(kind, guildId, out _);

    public string GetChannel(string kind, string? guildId, out bool hasStoredSelection)
    {
        guildId ??= string.Empty;
        hasStoredSelection = false;
        var normalizedKind = ChannelKeyHelper.NormalizeKind(kind);
        var normalizedGuild = ChannelKeyHelper.NormalizeGuildId(guildId);

        if (normalizedKind == NormalizedEventKind)
        {
            var scopedKey = BuildEventSelectionKey(normalizedGuild);
            if (_config.ChannelSelections.TryGetValue(scopedKey, out var scopedId) && !string.IsNullOrEmpty(scopedId))
            {
                hasStoredSelection = true;
                return scopedId;
            }
        }

        var key = ChannelKeyHelper.BuildSelectionKey(guildId, kind);
        if (_config.ChannelSelections.TryGetValue(key, out var id) && !string.IsNullOrEmpty(id))
        {
            hasStoredSelection = normalizedKind != NormalizedEventKind;
            return id;
        }

        if (!ChannelKeyHelper.IsDefaultGuild(guildId))
        {
            return string.Empty;
        }

        return (kind switch
        {
            ChannelKind.Event => _config.EventChannelId,
            ChannelKind.FcChat => _config.FcChannelId,
            ChannelKind.OfficerChat => _config.OfficerChannelId,
            ChannelKind.Chat => _config.ChatChannelId,
            _ => string.Empty
        }) ?? string.Empty;
    }

    public void SetChannel(string kind, string? guildId, string id)
    {
        guildId ??= string.Empty;
        var normalizedKind = ChannelKeyHelper.NormalizeKind(kind);
        var normalizedGuild = ChannelKeyHelper.NormalizeGuildId(guildId);
        var old = GetChannel(kind, guildId, out _);
        var scopedKey = normalizedKind == NormalizedEventKind ? BuildEventSelectionKey(normalizedGuild) : null;

        if (old == id)
        {
            if (scopedKey == null)
            {
                return;
            }

            if (_config.ChannelSelections.TryGetValue(scopedKey, out var existingScoped) && existingScoped == id)
            {
                return;
            }
        }

        var key = ChannelKeyHelper.BuildSelectionKey(guildId, kind);
        if (string.IsNullOrEmpty(id))
        {
            _config.ChannelSelections.Remove(key);
            if (scopedKey != null)
            {
                _config.ChannelSelections.Remove(scopedKey);
            }
        }
        else
        {
            _config.ChannelSelections[key] = id;
            if (scopedKey != null)
            {
                _config.ChannelSelections[scopedKey] = id;
            }
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
