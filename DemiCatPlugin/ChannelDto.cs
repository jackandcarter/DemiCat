using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Serialization;

namespace DemiCatPlugin;

public class ChannelDto
{
    [JsonPropertyName("id")] public string Id { get; set; } = string.Empty;
    [JsonPropertyName("name")] public string Name { get; set; } = string.Empty;
    [JsonPropertyName("parentId")] public string? ParentId { get; set; }
    [JsonPropertyName("kind")] public string Kind { get; set; } = string.Empty;
}

public static class ChannelDtoExtensions
{
    public static void EnsureKind(this ChannelDto? channel, string fallbackKind)
    {
        if (channel == null) return;
        if (string.IsNullOrEmpty(channel.Kind))
        {
            channel.Kind = ChannelKeyHelper.NormalizeKind(fallbackKind);
        }
        else
        {
            channel.Kind = ChannelKeyHelper.NormalizeKind(channel.Kind);
        }
    }

    public static List<ChannelDto> SortForDisplay(IEnumerable<ChannelDto> channels)
    {
        var parents = channels.Where(c => string.IsNullOrEmpty(c.ParentId)).ToList();
        var lookup = channels.Where(c => !string.IsNullOrEmpty(c.ParentId))
            .GroupBy(c => c.ParentId!)
            .ToDictionary(g => g.Key, g => g.ToList());
        var ordered = new List<ChannelDto>();
        foreach (var parent in parents)
        {
            ordered.Add(parent);
            if (lookup.TryGetValue(parent.Id, out var children))
            {
                ordered.AddRange(children);
            }
        }
        foreach (var ch in channels)
        {
            if (!ordered.Contains(ch)) ordered.Add(ch);
        }
        return ordered;
    }
}
