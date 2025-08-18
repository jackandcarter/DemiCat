using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;

namespace DemiCatPlugin;

public class OfficerChatWindow : ChatWindow
{
    public OfficerChatWindow(Config config, HttpClient httpClient) : base(config, httpClient)
    {
        _channelId = config.OfficerChannelId;
    }

    public override void Draw()
    {
        var originalChatChannel = _config.ChatChannelId;
        base.Draw();

        if (_config.ChatChannelId != originalChatChannel || _config.OfficerChannelId != _channelId)
        {
            _config.ChatChannelId = originalChatChannel;
            _config.OfficerChannelId = _channelId;
            SaveConfig();
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
            var request = new HttpRequestMessage(HttpMethod.Post, $"{_config.ApiBaseUrl.TrimEnd('/')}/api/officer-messages");
            request.Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
            if (!string.IsNullOrEmpty(_config.AuthToken))
            {
                request.Headers.Add("X-Api-Key", _config.AuthToken);
            }
            var response = await _httpClient.SendAsync(request);
            if (response.IsSuccessStatusCode)
            {
                _ = PluginServices.Framework.RunOnTick(() => _input = string.Empty);
                await RefreshMessages();
            }
        }
        catch
        {
            // ignored
        }
    }

    protected override async Task FetchChannels()
    {
        _channelsLoaded = true;
        try
        {
            var response = await _httpClient.GetAsync($"{_config.ApiBaseUrl.TrimEnd('/')}/api/channels");
            if (!response.IsSuccessStatusCode)
            {
                return;
            }
            var stream = await response.Content.ReadAsStreamAsync();
            var dto = await JsonSerializer.DeserializeAsync<OfficerChannelListDto>(stream) ?? new OfficerChannelListDto();
            _ = PluginServices.Framework.RunOnTick(() =>
            {
                SetChannels(dto.Officer);
            });
        }
        catch
        {
            // ignored
        }
    }

    protected override Uri BuildWebSocketUri()
    {
        var baseUri = _config.ApiBaseUrl.TrimEnd('/') + "/ws/officer-messages";
        var builder = new UriBuilder(baseUri);
        if (builder.Scheme == "https")
        {
            builder.Scheme = "wss";
        }
        else if (builder.Scheme == "http")
        {
            builder.Scheme = "ws";
        }
        return builder.Uri;
    }

    private class OfficerChannelListDto
    {
        [JsonPropertyName("officer_chat")]
        public List<string> Officer { get; set; } = new();
    }
}

