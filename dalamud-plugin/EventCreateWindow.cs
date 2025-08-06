using System;
using System.Collections.Generic;
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
    private readonly List<string> _channels = new();
    private int _selectedIndex;
    private bool _channelsLoaded;
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

        if (!_channelsLoaded)
        {
            FetchChannels();
        }

        ImGui.InputText("Title", ref _title, 256);
        if (_channels.Count > 0)
        {
            if (ImGui.Combo("Channel", ref _selectedIndex, _channels.ToArray(), _channels.Count))
            {
                _channelId = _channels[_selectedIndex];
            }
        }
        else
        {
            ImGui.TextUnformatted("No channels available");
        }
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

    private async void FetchChannels()
    {
        _channelsLoaded = true;
        try
        {
            var response = await _httpClient.GetAsync($"{_config.HelperBaseUrl.TrimEnd('/')}/channels");
            if (!response.IsSuccessStatusCode)
            {
                return;
            }
            var stream = await response.Content.ReadAsStreamAsync();
            var dto = await JsonSerializer.DeserializeAsync<ChannelListDto>(stream) ?? new ChannelListDto();
            _channels.Clear();
            _channels.AddRange(dto.Event);
            if (_channels.Count > 0)
            {
                _channelId = _channels[_selectedIndex];
            }
        }
        catch
        {
            // ignored
        }
    }

    private class ChannelListDto
    {
        public List<string> Event { get; set; } = new();
        public List<string> Chat { get; set; } = new();
    }
}
