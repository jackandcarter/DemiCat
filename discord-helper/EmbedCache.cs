using System.Collections.Concurrent;

namespace DiscordHelper;

public class EmbedCache
{
    private const int MaxItems = 50;
    private readonly ConcurrentDictionary<string, EmbedDto> _embeds = new();
    private readonly Queue<string> _order = new();
    private readonly object _lock = new();

    public void Add(EmbedDto embed)
    {
        if (embed.Id == null)
        {
            return;
        }

        lock (_lock)
        {
            if (_embeds.TryAdd(embed.Id, embed))
            {
                _order.Enqueue(embed.Id);
                TrimExcess();
            }
        }
    }

    public EmbedDto? Get(string id)
    {
        _embeds.TryGetValue(id, out var embed);
        return embed;
    }

    public bool Update(EmbedDto embed)
    {
        if (embed.Id == null)
        {
            return false;
        }

        lock (_lock)
        {
            if (!_embeds.ContainsKey(embed.Id))
            {
                return false;
            }

            _embeds[embed.Id] = embed;
            return true;
        }
    }

    public IReadOnlyCollection<EmbedDto> GetAll()
    {
        lock (_lock)
        {
            return _order.Select(id => _embeds[id]).ToList().AsReadOnly();
        }
    }

    private void TrimExcess()
    {
        while (_order.Count > MaxItems)
        {
            var oldest = _order.Dequeue();
            _embeds.TryRemove(oldest, out _);
        }
    }
}
