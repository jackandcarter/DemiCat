using Dalamud.Bindings.ImGui;
using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Net.Http.Headers;
using System.Threading;
using System.Threading.Tasks;
using System.Numerics;
using System.IO;

namespace DemiCatPlugin;

public class OfficerChatWindow : ChatWindow
{
    public OfficerChatWindow(Config config, HttpClient httpClient, DiscordPresenceService? presence, TokenManager tokenManager, ChannelService channelService)
        : base(config, httpClient, presence, tokenManager, channelService)
    {
        _channelId = config.OfficerChannelId;
    }

    public override void StartNetworking()
    {
        _bridge.Start();
        if (_config.Roles.Contains("officer"))
        {
            _bridge.Subscribe(_channelId);
            _presence?.Reset();
            _ = RefreshMessages();
        }
    }

    public override void Draw()
    {
        if (!_config.Roles.Contains("officer"))
        {
            return;
        }

        var originalChatChannel = _config.ChatChannelId;
        base.Draw();

        // Reserved padded area beneath the standard chat input for upcoming officer tools.
        ImGui.Dummy(new Vector2(0, ImGui.GetFrameHeightWithSpacing()));

        if (_config.ChatChannelId != originalChatChannel || _config.OfficerChannelId != _channelId)
        {
            _config.ChatChannelId = originalChatChannel;
            _config.OfficerChannelId = _channelId;
            SaveConfig();
        }
    }

    protected override string MessagesPath => "/api/officer-messages";

    protected override async Task<HttpRequestMessage> BuildMultipartRequest(string content)
    {
        var url = $"{_config.ApiBaseUrl.TrimEnd('/')}{MessagesPath}";
        var form = new MultipartFormDataContent();
        form.Add(new StringContent(_channelId), "channelId");
        form.Add(new StringContent(content), "content");
        form.Add(new StringContent(_useCharacterName ? "true" : "false"), "useCharacterName");
        if (!string.IsNullOrEmpty(_replyToId))
        {
            var refJson = JsonSerializer.Serialize(new { messageId = _replyToId, channelId = _channelId });
            form.Add(new StringContent(refJson, Encoding.UTF8), "message_reference");
        }
        foreach (var path in _attachments)
        {
            try
            {
                var bytes = await File.ReadAllBytesAsync(path);
                var part = new ByteArrayContent(bytes);
                part.Headers.ContentType = MediaTypeHeaderValue.Parse("application/octet-stream");
                form.Add(part, "files", Path.GetFileName(path));
            }
            catch
            {
                // ignore individual file errors
            }
        }
        return new HttpRequestMessage(HttpMethod.Post, url) { Content = form };
    }

    protected override async Task EditMessage(string messageId, string channelId, string content)
    {
        if (!ApiHelpers.ValidateApiBaseUrl(_config))
        {
            PluginServices.Instance!.Log.Warning("Cannot edit: API base URL is not configured.");
            return;
        }
        if (string.IsNullOrWhiteSpace(messageId) || string.IsNullOrWhiteSpace(content))
        {
            return;
        }

        try
        {
            var body = new { content };
            var url = $"{_config.ApiBaseUrl.TrimEnd('/')}{MessagesPath}/{messageId}";
            var request = new HttpRequestMessage(HttpMethod.Patch, url)
            {
                Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json")
            };
            ApiHelpers.AddAuthHeader(request, _tokenManager);
            var response = await _httpClient.SendAsync(request);
            if (!response.IsSuccessStatusCode)
            {
                var responseBody = await response.Content.ReadAsStringAsync();
                PluginServices.Instance!.Log.Warning($"Failed to edit message. Status: {response.StatusCode}. Response Body: {responseBody}");
            }
        }
        catch (Exception ex)
        {
            PluginServices.Instance!.Log.Error(ex, "Error editing message");
        }
    }

    protected override async Task DeleteMessage(string messageId, string channelId)
    {
        if (!ApiHelpers.ValidateApiBaseUrl(_config))
        {
            PluginServices.Instance!.Log.Warning("Cannot delete: API base URL is not configured.");
            return;
        }
        if (string.IsNullOrWhiteSpace(messageId))
        {
            return;
        }

        try
        {
            var url = $"{_config.ApiBaseUrl.TrimEnd('/')}{MessagesPath}/{messageId}";
            var request = new HttpRequestMessage(HttpMethod.Delete, url);
            ApiHelpers.AddAuthHeader(request, _tokenManager);
            var response = await _httpClient.SendAsync(request);
            if (!response.IsSuccessStatusCode)
            {
                var responseBody = await response.Content.ReadAsStringAsync();
                PluginServices.Instance!.Log.Warning($"Failed to delete message. Status: {response.StatusCode}. Response Body: {responseBody}");
            }
        }
        catch (Exception ex)
        {
            PluginServices.Instance!.Log.Error(ex, "Error deleting message");
        }
    }

    protected override async Task FetchChannels(bool refreshed = false)
    {
        if (!_tokenManager.IsReady())
        {
            _channelsLoaded = true;
            return;
        }

        if (!ApiHelpers.ValidateApiBaseUrl(_config))
        {
            PluginServices.Instance!.Log.Warning("Cannot fetch channels: API base URL is not configured.");
            _ = PluginServices.Instance!.Framework.RunOnTick(() =>
            {
                _channelFetchFailed = true;
                _channelErrorMessage = "Invalid API URL";
                _channelsLoaded = true;
            });
            return;
        }

        try
        {
            var channels = (await _channelService.FetchAsync(ChannelKind.OfficerChat, CancellationToken.None)).ToList();
            if (await ChannelNameResolver.Resolve(channels, _httpClient, _config, refreshed, () => FetchChannels(true)))
                return;
            _ = PluginServices.Instance!.Framework.RunOnTick(() =>
            {
                SetChannels(channels);
                _channelsLoaded = true;
                _channelFetchFailed = false;
                _channelErrorMessage = string.Empty;
            });
        }
        catch (HttpRequestException ex)
        {
            PluginServices.Instance!.Log.Warning($"Failed to fetch channels. Status: {ex.StatusCode}");
            _ = PluginServices.Instance!.Framework.RunOnTick(() =>
            {
                _channelFetchFailed = true;
                _channelErrorMessage = ex.StatusCode == HttpStatusCode.Forbidden
                    ? "Forbidden – check API key/roles"
                    : "Failed to load channels";
                _channelsLoaded = true;
            });
        }
        catch (Exception ex)
        {
            PluginServices.Instance!.Log.Error(ex, "Error fetching channels");
            _ = PluginServices.Instance!.Framework.RunOnTick(() =>
            {
                _channelFetchFailed = true;
                _channelErrorMessage = "Failed to load channels";
                _channelsLoaded = true;
            });
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

}

