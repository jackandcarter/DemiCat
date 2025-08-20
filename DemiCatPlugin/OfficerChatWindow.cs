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
    public OfficerChatWindow(Config config, HttpClient httpClient, PresenceSidebar presence) : base(config, httpClient, presence)
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
                _ = PluginServices.Instance!.Framework.RunOnTick(() =>
                {
                    _input = string.Empty;
                    _statusMessage = string.Empty;
                });
                await RefreshMessages();
            }
            else
            {
                var responseBody = await response.Content.ReadAsStringAsync();
                PluginServices.Instance!.Log.Warning($"Failed to send officer message. Status: {response.StatusCode}. Response Body: {responseBody}");
                _ = PluginServices.Instance!.Framework.RunOnTick(() => _statusMessage = "Failed to send message");
            }
        }
        catch (Exception ex)
        {
            PluginServices.Instance!.Log.Error(ex, "Error sending officer message");
            _ = PluginServices.Instance!.Framework.RunOnTick(() => _statusMessage = "Failed to send message");
        }
    }

    protected override async Task FetchChannels()
    {
        if (!ApiHelpers.ValidateApiBaseUrl(_config))
        {
            PluginServices.Instance!.Log.Warning("Cannot fetch channels: API base URL is not configured.");
            _channelFetchFailed = true;
            _channelErrorMessage = "Invalid API URL";
            _channelsLoaded = true;
            return;
        }

        try
        {
            var request = new HttpRequestMessage(HttpMethod.Get, $"{_config.ApiBaseUrl.TrimEnd('/')}/api/channels");
            if (!string.IsNullOrEmpty(_config.AuthToken))
            {
                request.Headers.Add("X-Api-Key", _config.AuthToken);
            }
            var response = await _httpClient.SendAsync(request);
            if (!response.IsSuccessStatusCode)
            {
                var responseBody = await response.Content.ReadAsStringAsync();
                PluginServices.Instance!.Log.Warning($"Failed to fetch channels. Status: {response.StatusCode}. Response Body: {responseBody}");
                _channelFetchFailed = true;
                _channelErrorMessage = "Failed to load channels";
                _channelsLoaded = true;
                return;
            }
            var stream = await response.Content.ReadAsStreamAsync();
            var dto = await JsonSerializer.DeserializeAsync<OfficerChannelListDto>(stream) ?? new OfficerChannelListDto();
            _ = PluginServices.Instance!.Framework.RunOnTick(() =>
            {
                SetChannels(dto.Officer);
                _channelsLoaded = true;
                _channelFetchFailed = false;
                _channelErrorMessage = string.Empty;
            });
        }
        catch (Exception ex)
        {
            PluginServices.Instance!.Log.Error(ex, "Error fetching channels");
            _channelFetchFailed = true;
            _channelErrorMessage = "Failed to load channels";
            _channelsLoaded = true;
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
        public List<ChannelDto> Officer { get; set; } = new();
    }
}

