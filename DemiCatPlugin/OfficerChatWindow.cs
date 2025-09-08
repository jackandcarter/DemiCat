using Dalamud.Bindings.ImGui;
using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Net;
using System.Text.Json;
using System.Threading.Tasks;

namespace DemiCatPlugin;

public class OfficerChatWindow : ChatWindow
{
    public OfficerChatWindow(Config config, HttpClient httpClient, DiscordPresenceService? presence, TokenManager tokenManager)
        : base(config, httpClient, presence, tokenManager)
    {
        _channelId = config.OfficerChannelId;
    }

    public override void StartNetworking()
    {
        if (!_config.Officer || !_config.Roles.Contains("officer"))
        {
            return;
        }
        base.StartNetworking();
    }

    public override void Draw()
    {
        if (!_config.Officer)
        {
            ImGui.TextUnformatted("Feature disabled");
            return;
        }

        if (!_tokenManager.IsReady())
        {
            base.Draw();
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
            var request = new HttpRequestMessage(HttpMethod.Get,
                $"{_config.ApiBaseUrl.TrimEnd('/')}/api/channels?kind={ChannelKind.OfficerChat}");
            ApiHelpers.AddAuthHeader(request, _tokenManager);
            var response = await _httpClient.SendAsync(request);
            if (!response.IsSuccessStatusCode)
            {
                var responseBody = await response.Content.ReadAsStringAsync();
                PluginServices.Instance!.Log.Warning($"Failed to fetch channels. Status: {response.StatusCode}. Response Body: {responseBody}");
                _ = PluginServices.Instance!.Framework.RunOnTick(() =>
                {
                    _channelFetchFailed = true;
                    _channelErrorMessage = response.StatusCode == HttpStatusCode.Forbidden
                        ? "Forbidden – check API key/roles"
                        : "Failed to load channels";
                    _channelsLoaded = true;
                });
                return;
            }
            var stream = await response.Content.ReadAsStreamAsync();
            var channels = await JsonSerializer.DeserializeAsync<List<ChannelDto>>(stream) ?? new();
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

