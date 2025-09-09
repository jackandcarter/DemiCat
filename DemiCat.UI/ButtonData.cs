using System;
using DiscordHelper;

namespace DemiCat.UI;

public class ButtonData
{
    public string Tag { get; set; } = Guid.NewGuid().ToString();
    public string Label { get; set; } = string.Empty;
    public ButtonStyle Style { get; set; } = ButtonStyle.Primary;
    public string? Emoji { get; set; }
    public int? MaxSignups { get; set; }
    public int? Width { get; set; }
    public int? Height { get; set; }
}
