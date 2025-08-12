using System;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Numerics;
using System.Threading.Tasks;
using Dalamud.Bindings.ImGui;

namespace DemiCatPlugin;

public class TemplatesWindow : IDisposable
{
    private readonly Config _config;
    private readonly HttpClient _httpClient = new();
    private int _selectedIndex = -1;
    private bool _showPreview;
    private string _previewContent = string.Empty;

    public string ChannelId { get; set; } = string.Empty;

    public TemplatesWindow(Config config)
    {
        _config = config;
    }

    public void Draw()
    {
        ImGui.BeginChild("TemplateList", new Vector2(150, 0), true);
        for (var i = 0; i < _config.Templates.Count; i++)
        {
            var name = _config.Templates[i].Name;
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
            var tmpl = _config.Templates[_selectedIndex];
            if (ImGui.Button("Preview"))
            {
                _previewContent = tmpl.Content;
                _showPreview = true;
            }
            ImGui.SameLine();
            if (ImGui.Button("Post"))
            {
                _ = PostTemplate(tmpl.Content);
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
                ImGui.TextUnformatted(_previewContent);
            }
            ImGui.End();
        }
    }

    private async Task PostTemplate(string content)
    {
        if (string.IsNullOrWhiteSpace(ChannelId))
        {
            return;
        }
        try
        {
            var body = new { channelId = ChannelId, content, useCharacterName = _config.UseCharacterName };
            var request = new HttpRequestMessage(HttpMethod.Post, $"{_config.HelperBaseUrl.TrimEnd('/')}/api/messages");
            request.Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
            if (!string.IsNullOrEmpty(_config.AuthToken))
            {
                request.Headers.Add("X-Api-Key", _config.AuthToken);
            }
            await _httpClient.SendAsync(request);
        }
        catch
        {
            // ignored
        }
    }

    public void Dispose()
    {
        _httpClient.Dispose();
    }
}

