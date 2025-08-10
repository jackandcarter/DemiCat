using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Numerics;
using System.Threading.Tasks;
using Dalamud.Bindings.ImGui;

namespace DemiCatPlugin;

public class FcChatWindow : ChatWindow
{
    private readonly List<UserDto> _users = new();
    private DateTime _lastUserFetch = DateTime.MinValue;
    private readonly string _channelName;

    public FcChatWindow(Config config) : base(config)
    {
        _channelId = config.FcChannelId;
        _channelName = string.IsNullOrEmpty(config.FcChannelName) ? config.FcChannelId : config.FcChannelName;
    }

    public override void Draw()
    {
        if (string.IsNullOrEmpty(_channelId))
        {
            ImGui.TextUnformatted("No FC channel configured");
            return;
        }

        if (DateTime.UtcNow - _lastFetch > TimeSpan.FromSeconds(_config.PollIntervalSeconds))
        {
            _ = RefreshMessages();
        }

        if (DateTime.UtcNow - _lastUserFetch > TimeSpan.FromSeconds(_config.PollIntervalSeconds))
        {
            _ = RefreshUsers();
        }

        ImGui.TextUnformatted($"Channel: #{_channelName}");

        ImGui.BeginChild("##chatScroll", new Vector2(-150, -30), true);
        foreach (var msg in _messages)
        {
            ImGui.TextWrapped($"{msg.AuthorName}: {FormatContent(msg)}");
        }
        ImGui.EndChild();

        ImGui.SameLine();

        ImGui.BeginChild("##userList", new Vector2(150, -30), true);
        foreach (var user in _users)
        {
            if (ImGui.Selectable(user.Name))
            {
                _input += $"@{user.Name} ";
            }
        }
        ImGui.EndChild();

        var send = ImGui.InputText("##chatInput", ref _input, 512, ImGuiInputTextFlags.EnterReturnsTrue);
        ImGui.SameLine();
        if (ImGui.Button("Send") || send)
        {
            _ = SendMessage();
        }
    }

    protected override async Task SendMessage()
    {
        if (string.IsNullOrWhiteSpace(_channelId) || string.IsNullOrWhiteSpace(_input))
        {
            return;
        }

        var content = _input;
        foreach (var u in _users)
        {
            content = Regex.Replace(content, $"@{Regex.Escape(u.Name)}\\b", $"<@{u.Id}>");
        }

        try
        {
            var body = new { channelId = _channelId, content, useCharacterName = _useCharacterName };
            var request = new HttpRequestMessage(HttpMethod.Post, $"{_config.HelperBaseUrl.TrimEnd('/')}/messages");
            request.Content = new StringContent(JsonSerializer.Serialize(body), System.Text.Encoding.UTF8, "application/json");
            if (!string.IsNullOrEmpty(_config.AuthToken))
            {
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _config.AuthToken);
            }
            var response = await _httpClient.SendAsync(request);
            if (response.IsSuccessStatusCode)
            {
                _input = string.Empty;
                await RefreshMessages();
            }
        }
        catch
        {
            // ignored
        }
    }

    private async Task RefreshUsers()
    {
        try
        {
            var request = new HttpRequestMessage(HttpMethod.Get, $"{_config.HelperBaseUrl.TrimEnd('/')}/users");
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
            var users = await JsonSerializer.DeserializeAsync<List<UserDto>>(stream) ?? new List<UserDto>();
            _users.Clear();
            _users.AddRange(users);
            _lastUserFetch = DateTime.UtcNow;
        }
        catch
        {
            // ignored
        }
    }

    private class UserDto
    {
        public string Id { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
    }
}

