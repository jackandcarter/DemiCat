using System;
using System.Collections.Generic;
using System.Linq;

namespace DemiCatPlugin;

public sealed class MessageCache
{
    // channelId -> recent messages (newest last)
    private readonly Dictionary<string, LinkedList<DiscordMessageDto>> _perChannel = new();
    private const int MaxPerChannel = 180; // ~3 pages for image-heavy channels

    public IReadOnlyList<DiscordMessageDto> Snapshot(string channelId)
    {
        if (string.IsNullOrEmpty(channelId))
        {
            return Array.Empty<DiscordMessageDto>();
        }

        if (!_perChannel.TryGetValue(channelId, out var list))
        {
            return Array.Empty<DiscordMessageDto>();
        }

        return list.ToList(); // small
    }

    public void Upsert(string channelId, IEnumerable<DiscordMessageDto> messages)
    {
        if (string.IsNullOrEmpty(channelId))
        {
            return;
        }

        if (!_perChannel.TryGetValue(channelId, out var list))
        {
            list = new LinkedList<DiscordMessageDto>();
            _perChannel[channelId] = list;
        }

        var byId = new HashSet<string>(list.Select(m => m.Id ?? string.Empty), StringComparer.Ordinal);
        foreach (var message in messages)
        {
            if (message?.Id is not { } id)
            {
                continue;
            }

            if (byId.Contains(id))
            {
                continue;
            }

            list.AddLast(message);
            byId.Add(id);

            while (list.Count > MaxPerChannel)
            {
                list.RemoveFirst();
            }
        }
    }

    public void UpsertOne(string channelId, DiscordMessageDto message)
        => Upsert(channelId, new[] { message });

    public void RemoveOne(string channelId, string id)
    {
        if (string.IsNullOrEmpty(channelId) || string.IsNullOrEmpty(id))
        {
            return;
        }

        if (!_perChannel.TryGetValue(channelId, out var list))
        {
            return;
        }

        var node = list.First;
        while (node != null)
        {
            if (string.Equals(node.Value?.Id, id, StringComparison.Ordinal))
            {
                list.Remove(node);
                break;
            }

            node = node.Next;
        }
    }
}
