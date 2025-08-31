using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text;
using System.IO;
using System.Text.RegularExpressions;
using System.Numerics;
using System.Threading.Tasks;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Textures;

namespace DemiCatPlugin;

public class FcChatWindow : ChatWindow
{
    private readonly List<UserDto> _users = new();
    private DateTime _lastUserFetch = DateTime.MinValue;

    public FcChatWindow(Config config, HttpClient httpClient, PresenceSidebar? presence) : base(config, httpClient, presence)
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

        _ = RoleCache.EnsureLoaded(_httpClient, _config);

        ImGui.BeginChild("##fcChat", new Vector2(-150, 0), false);
        base.Draw();
        ImGui.EndChild();

        ImGui.SameLine();
        ImGui.BeginChild("##userList", new Vector2(150, -30), true);
        foreach (var user in _users)
        {
            var color = user.Status == "online"
                ? new Vector4(0f, 1f, 0f, 1f)
                : new Vector4(0.5f, 0.5f, 0.5f, 1f);
            ImGui.PushStyleColor(ImGuiCol.Text, color);
            ImGui.TextUnformatted("●");
            ImGui.PopStyleColor();
            ImGui.SameLine();
            if (!string.IsNullOrEmpty(user.AvatarUrl) && user.AvatarTexture == null)
            {
                LoadTexture(user.AvatarUrl, t => user.AvatarTexture = t);
            }
            if (user.AvatarTexture != null)
            {
                var wrap = user.AvatarTexture.GetWrapOrEmpty();
                ImGui.Image(wrap.Handle, new Vector2(24, 24));
            }
            else
            {
                ImGui.Dummy(new Vector2(24, 24));
            }
            ImGui.SameLine();
            if (ImGui.Selectable(user.Name))
            {
                _input += $"@{user.Name} ";
            }
        }
        if (RoleCache.Roles.Count > 0)
        {
            ImGui.Separator();
            foreach (var role in RoleCache.Roles)
            {
                if (ImGui.Selectable($"@{role.Name}"))
                {
                    _input += $"@{role.Name} ";
                }
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

    public override Task RefreshMessages()
    {
        if (!_config.EnableFcChat)
        {
            return Task.CompletedTask;
        }
        return base.RefreshMessages();
    }

    protected override Task FetchChannels(bool refreshed = false)
    {
        if (!_config.EnableFcChat)
        {
            return Task.CompletedTask;
        }
        return base.FetchChannels(refreshed);
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
        foreach (var r in RoleCache.Roles)
        {
            content = Regex.Replace(content, $"@{Regex.Escape(r.Name)}\\b", $"<@&{r.Id}>");
        }

        try
        {
            var url = $"{_config.ApiBaseUrl.TrimEnd('/')}/api/channels/{_channelId}/messages";
            var form = new MultipartFormDataContent();
            form.Add(new StringContent(content), "content");
            form.Add(new StringContent(_useCharacterName ? "true" : "false"), "useCharacterName");
            if (!string.IsNullOrEmpty(_replyToId))
            {
                var refJson = JsonSerializer.Serialize(new { messageId = _replyToId });
                form.Add(new StringContent(refJson, Encoding.UTF8), "message_reference");
            }
            foreach (var path in _attachments)
            {
                try
                {
                    var bytes = await File.ReadAllBytesAsync(path);
                    var contentPart = new ByteArrayContent(bytes);
                    contentPart.Headers.ContentType = MediaTypeHeaderValue.Parse("application/octet-stream");
                    form.Add(contentPart, "files", Path.GetFileName(path));
                }
                catch
                {
                    // ignore individual file errors
                }
            }
            var request = new HttpRequestMessage(HttpMethod.Post, url)
            {
                Content = form
            };
            if (!string.IsNullOrEmpty(_config.AuthToken))
            {
                request.Headers.Add("X-Api-Key", _config.AuthToken);
            }
            var response = await _httpClient.SendAsync(request);
            if (response.IsSuccessStatusCode)
            {
                _input = string.Empty;
                _attachments.Clear();
                _replyToId = null;
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
        [JsonPropertyName("id")] public string Id { get; set; } = string.Empty;
        [JsonPropertyName("name")] public string Name { get; set; } = string.Empty;
        [JsonPropertyName("status")] public string Status { get; set; } = string.Empty;
        [JsonPropertyName("avatar_url")] public string? AvatarUrl { get; set; }
        [JsonIgnore] public ISharedImmediateTexture? AvatarTexture { get; set; }
    }
}

