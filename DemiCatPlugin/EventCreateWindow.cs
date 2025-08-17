using System;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Numerics;
using System.Threading.Tasks;
using System.Collections.Generic;
using System.Linq;
using Dalamud.Bindings.ImGui;
using DiscordHelper;

namespace DemiCatPlugin;

public class EventCreateWindow
{
    private readonly Config _config;
    private readonly HttpClient _httpClient;

    private string _title = string.Empty;
    private string _description = string.Empty;
    private string _time = DateTime.UtcNow.ToString("o");
    private string _imageUrl = string.Empty;
    private string _url = string.Empty;
    private string _thumbnailUrl = string.Empty;
    private int _color;
    private readonly List<Field> _fields = new();
    private string? _lastResult;
    private readonly List<ButtonConfig> _buttons = new()
    {
        new ButtonConfig("yes", "Yes", "✅", ButtonStyle.Success),
        new ButtonConfig("maybe", "Maybe", "❔", ButtonStyle.Secondary),
        new ButtonConfig("no", "No", "❌", ButtonStyle.Danger),
    };

    public string ChannelId { private get; set; } = string.Empty;

    public EventCreateWindow(Config config, HttpClient httpClient)
    {
        _config = config;
        _httpClient = httpClient;
    }

    public void Draw()
    {
        ImGui.InputText("Title", ref _title, 256);
        ImGui.InputText("Time", ref _time, 64);
        ImGui.InputTextMultiline("Description", ref _description, 4096, new Vector2(400, 100));
        ImGui.InputText("URL", ref _url, 260);
        ImGui.InputText("Image URL", ref _imageUrl, 260);
        ImGui.InputText("Thumbnail URL", ref _thumbnailUrl, 260);
        ImGui.InputInt("Color", ref _color);
        foreach (var button in _buttons)
        {
            ImGui.PushID(button.Tag);
            ImGui.Checkbox("Include", ref button.Include);
            ImGui.SameLine();
            ImGui.InputText("Label", ref button.Label, 32);
            ImGui.SameLine();
            ImGui.InputText("Emoji", ref button.Emoji, 16);
            ImGui.SameLine();
            var style = button.Style.ToString();
            if (ImGui.BeginCombo("Style", style))
            {
                foreach (ButtonStyle bs in Enum.GetValues<ButtonStyle>())
                {
                    var sel = bs == button.Style;
                    if (ImGui.Selectable(bs.ToString(), sel)) button.Style = bs;
                    if (sel) ImGui.SetItemDefaultFocus();
                }
                ImGui.EndCombo();
            }
            ImGui.PopID();
        }

        if (ImGui.TreeNode("Fields"))
        {
            for (var i = 0; i < _fields.Count; i++)
            {
                var field = _fields[i];
                ImGui.InputText($"Name##{i}", ref field.Name, 256);
                ImGui.InputTextMultiline($"Value##{i}", ref field.Value, 1024, new Vector2(400, 60));
                ImGui.Checkbox($"Inline##{i}", ref field.Inline);
                if (ImGui.Button($"Remove##{i}"))
                {
                    _fields.RemoveAt(i);
                    i--;
                }
                ImGui.Separator();
            }
            if (ImGui.Button("Add Field"))
            {
                _fields.Add(new Field());
            }
            ImGui.TreePop();
        }
        if (ImGui.Button("Create"))
        {
            _ = CreateEvent();
        }

        if (!string.IsNullOrEmpty(_lastResult))
        {
            ImGui.TextUnformatted(_lastResult);
        }
    }

    private async Task CreateEvent()
    {
        try
        {
            var buttons = _buttons
                .Where(b => b.Include)
                .Select(b => new
                {
                    label = b.Label,
                    customId = $"rsvp:{b.Tag}",
                    emoji = string.IsNullOrWhiteSpace(b.Emoji) ? null : b.Emoji,
                    style = (int)b.Style
                })
                .ToList();

            var body = new
            {
                channelId = ChannelId,
                title = _title,
                time = _time,
                description = _description,
                url = string.IsNullOrWhiteSpace(_url) ? null : _url,
                imageUrl = string.IsNullOrWhiteSpace(_imageUrl) ? null : _imageUrl,
                thumbnailUrl = string.IsNullOrWhiteSpace(_thumbnailUrl) ? null : _thumbnailUrl,
                color = _color > 0 ? (uint?)_color : null,
                fields = _fields.Count > 0
                    ? _fields
                        .Where(f => !string.IsNullOrWhiteSpace(f.Name) && !string.IsNullOrWhiteSpace(f.Value))
                        .Select(f => new { name = f.Name, value = f.Value, inline = f.Inline })
                        .ToList()
                    : null,
                buttons = buttons.Count > 0 ? buttons : null
            };

            var request = new HttpRequestMessage(HttpMethod.Post, $"{_config.HelperBaseUrl.TrimEnd('/')}/api/events");
            request.Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
            if (!string.IsNullOrEmpty(_config.AuthToken))
            {
                request.Headers.Add("X-Api-Key", _config.AuthToken);
            }

            var response = await _httpClient.SendAsync(request);
            _lastResult = response.IsSuccessStatusCode ? "Event posted" : $"Error: {response.StatusCode}";
        }
        catch (Exception ex)
        {
            _lastResult = $"Failed: {ex.Message}";
        }
    }


    private class Field
    {
        public string Name = string.Empty;
        public string Value = string.Empty;
        public bool Inline;
    }

    private class ButtonConfig
    {
        public ButtonConfig(string tag, string label, string emoji, ButtonStyle style)
        {
            Tag = tag;
            Label = label;
            Emoji = emoji;
            Style = style;
        }

        public string Tag;
        public bool Include = true;
        public string Label;
        public string Emoji;
        public ButtonStyle Style;
    }
}
