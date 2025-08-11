using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Numerics;
using System.Threading.Tasks;
using Dalamud.Bindings.ImGui;

namespace DemiCatPlugin;

public class OfficerChatWindow : ChatWindow
{
    public OfficerChatWindow(Config config) : base(config)
    {
        _channelId = config.OfficerChannelId;
    }

    public override void Draw()
    {
        if (string.IsNullOrEmpty(_channelId))
        {
            ImGui.TextUnformatted("No officer channel configured");
            return;
        }

        if (ImGui.Checkbox("Use Character Name", ref _useCharacterName))
        {
            _config.UseCharacterName = _useCharacterName;
            SaveConfig();
        }

        if (DateTime.UtcNow - _lastFetch > TimeSpan.FromSeconds(_config.PollIntervalSeconds))
        {
            _ = RefreshMessages();
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
            _ = SendMessage();
        }
    }

    protected override async Task SendMessage()
    {
        if (string.IsNullOrWhiteSpace(_channelId) || string.IsNullOrWhiteSpace(_input))
        {
            return;
        }

        try
        {
            var body = new { channelId = _channelId, content = _input, useCharacterName = _useCharacterName };
            var request = new HttpRequestMessage(HttpMethod.Post, $"{_config.HelperBaseUrl.TrimEnd('/')}/officer-messages");
            request.Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
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

    public new async Task RefreshMessages()
    {
        if (string.IsNullOrEmpty(_channelId))
        {
            return;
        }

        try
        {
            var request = new HttpRequestMessage(HttpMethod.Get, $"{_config.HelperBaseUrl.TrimEnd('/')}/officer-messages/{_channelId}");
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
            _ = PluginServices.Framework.RunOnTick(() =>
            {
                _messages.Clear();
                _messages.AddRange(msgs);
                _lastFetch = DateTime.UtcNow;
            });
        }
        catch
        {
            // ignored
        }
    }

    private void SaveConfig()
    {
        PluginServices.PluginInterface.SavePluginConfig(_config);
    }
}
