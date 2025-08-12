using System;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Numerics;
using System.Threading.Tasks;
using System.Collections.Generic;
using System.Linq;
using Dalamud.Bindings.ImGui;

namespace DemiCatPlugin;

public class EventCreateWindow : IDisposable
{
    private readonly Config _config;
    private readonly HttpClient _httpClient = new();

    private string _title = string.Empty;
    private string _description = string.Empty;
    private string _time = DateTime.UtcNow.ToString("o");
    private string _imageUrl = string.Empty;
    private string _url = string.Empty;
    private string _thumbnailUrl = string.Empty;
    private int _color;
    private readonly List<Field> _fields = new();
    private string? _lastResult;

    public string ChannelId { private get; set; } = string.Empty;

    public EventCreateWindow(Config config)
    {
        _config = config;
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
                    : null
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

    public void Dispose()
    {
        _httpClient.Dispose();
    }

    private class Field
    {
        public string Name = string.Empty;
        public string Value = string.Empty;
        public bool Inline;
    }
}
