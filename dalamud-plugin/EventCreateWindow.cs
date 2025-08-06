using System;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Numerics;
using ImGuiNET;

namespace DalamudPlugin;

public class EventCreateWindow : IDisposable
{
    private readonly Config _config;
    private readonly HttpClient _httpClient = new();

    private string _title = string.Empty;
    private string _description = string.Empty;
    private string _time = DateTime.UtcNow.ToString("o");
    private string _channelId = string.Empty;
    private string _imagePath = string.Empty;
    private string? _lastResult;

    public bool IsOpen;

    public EventCreateWindow(Config config)
    {
        _config = config;
    }

    public void Draw()
    {
        if (!IsOpen)
        {
            return;
        }

        if (!ImGui.Begin("Create Event", ref IsOpen))
        {
            ImGui.End();
            return;
        }

        ImGui.InputText("Title", ref _title, 256);
        ImGui.InputText("Channel Id", ref _channelId, 32);
        ImGui.InputText("Time", ref _time, 64);
        ImGui.InputTextMultiline("Description", ref _description, 4096, new Vector2(400, 100));
        ImGui.InputText("Image Path", ref _imagePath, 260);
        if (ImGui.Button("Create"))
        {
            CreateEvent();
        }

        if (!string.IsNullOrEmpty(_lastResult))
        {
            ImGui.TextUnformatted(_lastResult);
        }

        ImGui.End();
    }

    private async void CreateEvent()
    {
        try
        {
            string? imageBase64 = null;
            if (!string.IsNullOrEmpty(_imagePath) && File.Exists(_imagePath))
            {
                var bytes = await File.ReadAllBytesAsync(_imagePath);
                imageBase64 = Convert.ToBase64String(bytes);
            }

            var body = new
            {
                channelId = _channelId,
                title = _title,
                time = _time,
                description = _description,
                imageBase64
            };

            var request = new HttpRequestMessage(HttpMethod.Post, $"{_config.HelperBaseUrl.TrimEnd('/')}/events");
            request.Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
            if (!string.IsNullOrEmpty(_config.AuthToken))
            {
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _config.AuthToken);
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
