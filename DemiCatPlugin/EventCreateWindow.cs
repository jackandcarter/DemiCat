using System;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Numerics;
using System.Threading.Tasks;
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
        ImGui.InputText("Image URL", ref _imageUrl, 260);
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
                imageUrl = string.IsNullOrWhiteSpace(_imageUrl) ? null : _imageUrl
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
}
