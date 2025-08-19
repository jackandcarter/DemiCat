using System;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Numerics;
using System.Threading.Tasks;
using System.Linq;
using Dalamud.Bindings.ImGui;
using DiscordHelper;

namespace DemiCatPlugin;

public class TemplatesWindow
{
    private readonly Config _config;
    private readonly HttpClient _httpClient;
    private int _selectedIndex = -1;
    private bool _showPreview;
    private string _previewContent = string.Empty;
    private EventView? _previewEvent;
    private TemplateType _selectedType;

    public string ChannelId { get; set; } = string.Empty;

    public TemplatesWindow(Config config, HttpClient httpClient)
    {
        _config = config;
        _httpClient = httpClient;
    }

    public void Draw()
    {
        ImGui.BeginChild("TemplateList", new Vector2(150, 0), true);
        var typeNames = Enum.GetNames<TemplateType>();
        var typeIndex = (int)_selectedType;
        ImGui.SetNextItemWidth(-1);
        if (ImGui.Combo("Type", ref typeIndex, typeNames, typeNames.Length))
        {
            _selectedType = (TemplateType)typeIndex;
            _selectedIndex = -1;
            _showPreview = false;
        }

        var filteredTemplates = _config.Templates.Where(t => t.Type == _selectedType).ToList();
        for (var i = 0; i < filteredTemplates.Count; i++)
        {
            var name = filteredTemplates[i].Name;
            if (ImGui.Selectable(name, _selectedIndex == i))
            {
                _selectedIndex = i;
                _showPreview = false;
            }
        }
        ImGui.EndChild();

        ImGui.SameLine();

        ImGui.BeginChild("TemplateContent", new Vector2(0, 0), false);
        if (_selectedIndex >= 0)
        {
            if (_selectedIndex < filteredTemplates.Count)
            {
                var tmpl = filteredTemplates[_selectedIndex];
                if (ImGui.Button("Preview"))
                {
                    if (tmpl.Type == TemplateType.Event)
                    {
                        _previewEvent?.Dispose();
                        _previewEvent = new EventView(ToEmbedDto(tmpl), _config, _httpClient, () => Task.CompletedTask);
                        _previewContent = string.Empty;
                    }
                    else
                    {
                        _previewContent = tmpl.Content;
                        _previewEvent?.Dispose();
                        _previewEvent = null;
                    }
                    _showPreview = true;
                }
                ImGui.SameLine();
                if (ImGui.Button("Post"))
                {
                    _ = PostTemplate(tmpl);
                }
            }
        }
        else
        {
            ImGui.TextUnformatted("Select a template");
        }
        ImGui.EndChild();

        if (_showPreview)
        {
            if (ImGui.Begin("Template Preview", ref _showPreview))
            {
                if (_previewEvent != null)
                {
                    _previewEvent.Draw();
                }
                else
                {
                    ImGui.TextUnformatted(_previewContent);
                }
            }
            ImGui.End();
        }
    }

    private EmbedDto ToEmbedDto(Template tmpl)
    {
        DateTimeOffset? ts = null;
        if (!string.IsNullOrWhiteSpace(tmpl.Time) && DateTimeOffset.TryParse(tmpl.Time, out var parsed))
        {
            ts = parsed;
        }

        return new EmbedDto
        {
            Title = tmpl.Title,
            Description = tmpl.Description,
            Url = string.IsNullOrWhiteSpace(tmpl.Url) ? null : tmpl.Url,
            Timestamp = ts,
            ImageUrl = string.IsNullOrWhiteSpace(tmpl.ImageUrl) ? null : tmpl.ImageUrl,
            ThumbnailUrl = string.IsNullOrWhiteSpace(tmpl.ThumbnailUrl) ? null : tmpl.ThumbnailUrl,
            Color = tmpl.Color != 0 ? (uint?)tmpl.Color : null,
            Fields = tmpl.Fields?.Select(f => new EmbedFieldDto { Name = f.Name, Value = f.Value, Inline = f.Inline }).ToList(),
            Buttons = tmpl.Buttons?.Where(b => b.Include).Select(b => new EmbedButtonDto
            {
                Label = b.Label,
                CustomId = $"rsvp:{b.Tag}",
                Emoji = string.IsNullOrWhiteSpace(b.Emoji) ? null : b.Emoji,
                Style = b.Style
            }).ToList(),
            Mentions = tmpl.Mentions != null && tmpl.Mentions.Count > 0 ? tmpl.Mentions : null
        };
    }

    private async Task PostTemplate(Template tmpl)
    {
        if (string.IsNullOrWhiteSpace(ChannelId) || !ApiHelpers.ValidateApiBaseUrl(_config))
        {
            return;
        }
        try
        {
            if (tmpl.Type == TemplateType.Event)
            {
                var buttons = tmpl.Buttons?
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
                    title = tmpl.Title,
                    time = string.IsNullOrWhiteSpace(tmpl.Time)
                        ? DateTime.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss.ffffff'Z'")
                        : tmpl.Time,
                    description = tmpl.Description,
                    url = string.IsNullOrWhiteSpace(tmpl.Url) ? null : tmpl.Url,
                    imageUrl = string.IsNullOrWhiteSpace(tmpl.ImageUrl) ? null : tmpl.ImageUrl,
                    thumbnailUrl = string.IsNullOrWhiteSpace(tmpl.ThumbnailUrl) ? null : tmpl.ThumbnailUrl,
                    color = tmpl.Color != 0 ? (uint?)tmpl.Color : null,
                    fields = tmpl.Fields != null && tmpl.Fields.Count > 0
                        ? tmpl.Fields
                            .Where(f => !string.IsNullOrWhiteSpace(f.Name) && !string.IsNullOrWhiteSpace(f.Value))
                            .Select(f => new { name = f.Name, value = f.Value, inline = f.Inline })
                            .ToList()
                    : null,
                    buttons = buttons != null && buttons.Count > 0 ? buttons : null,
                    mentions = tmpl.Mentions != null && tmpl.Mentions.Count > 0 ? tmpl.Mentions : null
                };

                var request = new HttpRequestMessage(HttpMethod.Post, $"{_config.ApiBaseUrl.TrimEnd('/')}/api/events");
                request.Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
                if (!string.IsNullOrEmpty(_config.AuthToken))
                {
                    request.Headers.Add("X-Api-Key", _config.AuthToken);
                }
                await _httpClient.SendAsync(request);
            }
            else
            {
                var body = new { channelId = ChannelId, content = tmpl.Content, useCharacterName = _config.UseCharacterName };
                var request = new HttpRequestMessage(HttpMethod.Post, $"{_config.ApiBaseUrl.TrimEnd('/')}/api/messages");
                request.Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
                if (!string.IsNullOrEmpty(_config.AuthToken))
                {
                    request.Headers.Add("X-Api-Key", _config.AuthToken);
                }
                await _httpClient.SendAsync(request);
            }
        }
        catch
        {
            // ignored
        }
    }

}

