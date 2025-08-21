using System;
using System.Collections.Generic;
using System.Net.Http;
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

    public FcChatWindow(Config config, HttpClient httpClient, PresenceSidebar presence) : base(config, httpClient, presence)
    {
        _channelId = config.FcChannelId;
    }

    public override void Draw()
    {
        var originalChatChannel = _config.ChatChannelId;

        if (DateTime.UtcNow - _lastUserFetch > TimeSpan.FromSeconds(_config.PollIntervalSeconds))
        {
            _ = RefreshUsers();
        }

        ImGui.BeginChild("##fcChat", new Vector2(-150, 0), false);
        base.Draw();
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

        if (_config.ChatChannelId != originalChatChannel || _config.FcChannelId != _channelId)
        {
            _config.ChatChannelId = originalChatChannel;
            _config.FcChannelId = _channelId;
            SaveConfig();
        }
        else
        {
            _config.ChatChannelId = originalChatChannel;
        }
    }

    protected override async Task SendMessage()
    {
        if (!ApiHelpers.ValidateApiBaseUrl(_config))
        {
            PluginServices.Instance!.Log.Warning("Cannot send message: API base URL is not configured.");
            _ = PluginServices.Instance!.Framework.RunOnTick(() => _statusMessage = "Invalid API URL");
            return;
        }
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
            var request = new HttpRequestMessage(HttpMethod.Post, $"{_config.ApiBaseUrl.TrimEnd('/')}/api/messages");
            request.Content = new StringContent(JsonSerializer.Serialize(body), System.Text.Encoding.UTF8, "application/json");
            if (!string.IsNullOrEmpty(_config.AuthToken))
            {
                request.Headers.Add("X-Api-Key", _config.AuthToken);
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
        if (!ApiHelpers.ValidateApiBaseUrl(_config)) return;

        try
        {
            var request = new HttpRequestMessage(HttpMethod.Get, $"{_config.ApiBaseUrl.TrimEnd('/')}/api/users");
            if (!string.IsNullOrEmpty(_config.AuthToken))
            {
                request.Headers.Add("X-Api-Key", _config.AuthToken);
            }
            var response = await _httpClient.SendAsync(request);
            if (!response.IsSuccessStatusCode)
            {
                return;
            }
            var stream = await response.Content.ReadAsStreamAsync();
            var users = await JsonSerializer.DeserializeAsync<List<UserDto>>(stream) ?? new List<UserDto>();
            _ = PluginServices.Instance!.Framework.RunOnTick(() =>
            {
                _users.Clear();
                _users.AddRange(users);
                _lastUserFetch = DateTime.UtcNow;
            });
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

