using System;
using System.Collections.Generic;
using DiscordHelper;

namespace DalamudPlugin;

public class UiRenderer : IDisposable
{
    private readonly List<EmbedDto> _embeds = new();

    public void Draw(EmbedDto dto)
    {
        _embeds.Add(dto);
        // In a real plugin, rendering logic would load textures here.
    }

    public void DrawWindow()
    {
        foreach (var dto in _embeds)
        {
            // In a real plugin, rendering logic would go here.
        }

        _embeds.Clear();
    }

    public void Dispose()
    {
        _embeds.Clear();
    }
}
