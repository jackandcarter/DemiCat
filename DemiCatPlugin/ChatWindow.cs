using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Numerics;
using Dalamud.Bindings.ImGui;

namespace DemiCatPlugin;

public class ChatWindow : IDisposable
{
    protected readonly Config _config;
    protected readonly HttpClient _httpClient = new();
    protected readonly List<ChatMessageDto> _messages = new();
    protected readonly List<string> _channels = new();
    protected int _selectedIndex;
    protected bool _channelsLoaded;
    protected string _channelId;
    protected string _input = string.Empty;
    protected bool _useCharacterName;
    protected DateTime _lastFetch = DateTime.MinValue;

    public ChatWindow(Config config)
    {
        _config = config;
        _channelId = config.ChatChannelId;
        _useCharacterName = config.UseCharacterName;
    }

    public virtual void Draw()
    {
        if (!_channelsLoaded)
        {
            FetchChannels();
        }

        if (_channels.Count > 0)
        {
            if (ImGui.Combo("Channel", ref _selectedIndex, _channels.ToArray(), _channels.Count))
            {
                _channelId = _channels[_selectedIndex];
                _config.ChatChannelId = _channelId;
                SaveConfig();
                RefreshMessages();
            }
        }
        else
        {
            ImGui.TextUnformatted("No channels available");
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

    protected string FormatContent(ChatMessageDto msg)
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

    protected virtual async void SendMessage()
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
        PluginServices.PluginInterface.SavePluginConfig(_config);
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
            _channels.AddRange(dto.Chat);
            if (!string.IsNullOrEmpty(_channelId))
            {
                _selectedIndex = _channels.IndexOf(_channelId);
                if (_selectedIndex < 0) _selectedIndex = 0;
            }
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

    protected class ChatMessageDto
    {
        public string Id { get; set; } = string.Empty;
        public string ChannelId { get; set; } = string.Empty;
        public string AuthorName { get; set; } = string.Empty;
        public string Content { get; set; } = string.Empty;
        public List<MentionDto>? Mentions { get; set; }
    }

    protected class MentionDto
    {
        public string Id { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
    }

    protected class ChannelListDto
    {
        public List<string> Event { get; set; } = new();
        public List<string> Chat { get; set; } = new();
    }
}
