using System.Collections.Generic;
using DiscordHelper;

namespace DemiCatPlugin;

public class Template
{
    public string Name { get; set; } = string.Empty;
    public string Content { get; set; } = string.Empty;

    public TemplateType Type { get; set; } = TemplateType.Event;

    // Event template data
    public string Title { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string Time { get; set; } = string.Empty;
    public string Url { get; set; } = string.Empty;
    public string ImageUrl { get; set; } = string.Empty;
    public string ThumbnailUrl { get; set; } = string.Empty;
    public uint Color { get; set; }
    public List<TemplateField> Fields { get; set; } = new();
    public List<TemplateButton> Buttons { get; set; } = new();
    public List<ulong> Mentions { get; set; } = new();

    public class TemplateField
    {
        public string Name { get; set; } = string.Empty;
        public string Value { get; set; } = string.Empty;
        public bool Inline { get; set; }
    }

    public class TemplateButton
    {
        public string Tag { get; set; } = string.Empty;
        public bool Include { get; set; } = true;
        public string Label { get; set; } = string.Empty;
        public string Emoji { get; set; } = string.Empty;
        public ButtonStyle Style { get; set; } = ButtonStyle.Secondary;
        public int? MaxSignups { get; set; }
        public int? Width { get; set; }
    }
}

public enum TemplateType
{
    Event
}

