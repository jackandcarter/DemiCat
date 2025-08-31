using System;
using System.Collections.Generic;
using System.Linq;

namespace DemiCatPlugin;

internal static class ChannelNameResolver
{
    internal static bool Resolve(List<ChannelDto> channels)
    {
        var unresolved = false;
        foreach (var c in channels)
        {
            if (string.IsNullOrWhiteSpace(c.Name) || c.Name == c.Id || c.Name.All(char.IsDigit))
            {
                PluginServices.Instance!.Log.Warning($"Channel name missing or invalid for {c.Id}.");
                c.Name = c.Id;
                unresolved = true;
            }
        }
        return unresolved;
    }
}
