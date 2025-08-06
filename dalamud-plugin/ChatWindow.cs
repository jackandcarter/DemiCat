using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Numerics;
using System.IO;
using ImGuiNET;

namespace DalamudPlugin;

public class ChatWindow : IDisposable
{
    private readonly Config _config;
    private readonly HttpClient _httpClient = new();
    private readonly List<ChatMessageDto> _messages = new();
    private string _channelId;
    private string _input = string.Empty;
    private bool _useCharacterName;
    private DateTime _lastFetch = DateTime.MinValue;

    public ChatWindow(Config config)
    {
        _config = config;
        _channelId = config.ChatChannelId;
        _useCharacterName = config.UseCharacterName;
    }

    public void Draw()
    {
        if (ImGui.InputText("Channel Id", ref _channelId, 32))
        {
            _config.ChatChannelId = _channelId;
            SaveConfig();
        }
        if (ImGui.Checkbox("Use Character Name", ref _useCharacterName))
        {
            _config.UseCharacterName = _useCharacterName;
            SaveConfig();
        }

        if (DateTime.UtcNow - _lastFetch > TimeSpan.FromSeconds(_config.PollIntervalSeconds))
        {
            RefreshMessages();
        }

        ImGui.BeginChild("##chatScroll", new Vector2(0, -30), true);
        foreach (var msg in _messages)
        {
            ImGui.TextWrapped($"{msg.AuthorName}: {FormatContent(msg)}");
        }
        ImGui.EndChild();

        var send = ImGui.InputText("##chatInput", ref _input, 512, ImGuiInputTextFlags.EnterReturnsTrue);
        ImGui.SameLine();
        if (ImGui.Button("Send") || send)
        {
            SendMessage();
        }

    }

    private string FormatContent(ChatMessageDto msg)
    {
        var text = msg.Content;
        if (msg.Mentions != null)
        {
            foreach (var m in msg.Mentions)
            {
                text = text.Replace($"<@{m.Id}>", $"@{m.Name}");
            }
        }
        text = Regex.Replace(text, "<a?:([a-zA-Z0-9_]+):\\d+>", ":$1:");
        return text;
    }

    private async void SendMessage()
    {
        if (string.IsNullOrWhiteSpace(_channelId) || string.IsNullOrWhiteSpace(_input))
        {
            return;
        }

        try
        {
            var body = new { channelId = _channelId, content = _input, useCharacterName = _useCharacterName };
            var request = new HttpRequestMessage(HttpMethod.Post, $"{_config.HelperBaseUrl.TrimEnd('/')}/messages");
            request.Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
            if (!string.IsNullOrEmpty(_config.AuthToken))
            {
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _config.AuthToken);
            }
            var response = await _httpClient.SendAsync(request);
            if (response.IsSuccessStatusCode)
            {
                _input = string.Empty;
                RefreshMessages();
            }
        }
        catch
        {
            // ignored
        }
    }

    public async void RefreshMessages()
    {
        if (string.IsNullOrEmpty(_channelId))
        {
            return;
        }

        try
        {
            var request = new HttpRequestMessage(HttpMethod.Get, $"{_config.HelperBaseUrl.TrimEnd('/')}/messages/{_channelId}");
            if (!string.IsNullOrEmpty(_config.AuthToken))
            {
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _config.AuthToken);
            }
            var response = await _httpClient.SendAsync(request);
            if (!response.IsSuccessStatusCode)
            {
                return;
            }
            var stream = await response.Content.ReadAsStreamAsync();
            var msgs = await JsonSerializer.DeserializeAsync<List<ChatMessageDto>>(stream) ?? new List<ChatMessageDto>();
            _messages.Clear();
            _messages.AddRange(msgs);
            _lastFetch = DateTime.UtcNow;
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

    private void SaveConfig()
    {
        try
        {
            var path = Path.Combine(AppContext.BaseDirectory, "config.json");
            File.WriteAllText(path, JsonSerializer.Serialize(_config));
        }
        catch
        {
            // ignored
        }
    }

    private class ChatMessageDto
    {
        public string Id { get; set; } = string.Empty;
        public string ChannelId { get; set; } = string.Empty;
        public string AuthorName { get; set; } = string.Empty;
        public string Content { get; set; } = string.Empty;
        public List<MentionDto>? Mentions { get; set; }
    }

    private class MentionDto
    {
        public string Id { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
    }
}
