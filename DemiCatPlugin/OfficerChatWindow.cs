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
using System.Linq;
using DemiCatPlugin.Avatars;
using DemiCatPlugin.Emoji;

namespace DemiCatPlugin;

public class OfficerChatWindow : ChatWindow
{
    private DateTime _lastRolesRefresh = DateTime.MinValue;
    private bool _subscribed;

    public OfficerChatWindow(
        Config config,
        HttpClient httpClient,
        DiscordPresenceService? presence,
        TokenManager tokenManager,
        ChannelService channelService,
        ChannelSelectionService channelSelection,
        AvatarCache avatarCache,
        EmojiManager emojiManager)
        : base(
            config,
            httpClient,
            presence,
            tokenManager,
            channelService,
            channelSelection,
            global::DemiCatPlugin.ChannelKind.OfficerChat,
            avatarCache,
            emojiManager)
    {
        _bridge.StatusChanged += s =>
        {
            if (s.Contains("Forbidden", StringComparison.OrdinalIgnoreCase))
            {
                TryRefreshRoles();
            }
        };
    }

#if TEST
    internal OfficerChatWindow(
        Config config,
        HttpClient httpClient,
        DiscordPresenceService? presence,
        TokenManager tokenManager,
        ChannelService channelService)
        : this(
            config,
            httpClient,
            presence,
            tokenManager,
            channelService,
            new ChannelSelectionService(config),
            null!,
            new EmojiManager(httpClient, tokenManager, config))
    {
    }
#endif

    public override void StartNetworking()
    {
        _bridge.Start();
        if (_config.Roles.Contains("officer"))
        {
            Subscribe();
        }
        else
        {
            TryRefreshRoles();
        }
    }

    public override void Draw()
    {
        if (!_config.Roles.Contains("officer"))
        {
            TryRefreshRoles();
            if (!string.IsNullOrEmpty(_statusMessage))
            {
                ImGui.TextUnformatted(_statusMessage);
            }
            else
            {
                ImGui.TextUnformatted("Link DemiCat…");
            }
            return;
        }

        if (!_bridge.IsReady())
        {
            if (!string.IsNullOrEmpty(_statusMessage))
            {
                ImGui.TextUnformatted(_statusMessage);
            }
            else
            {
                ImGui.TextUnformatted("Link DemiCat…");
            }
            return;
        }

        if (!_subscribed)
        {
            Subscribe();
        }

        base.Draw();

        // Reserved padded area beneath the standard chat input for upcoming officer tools.
        ImGui.Dummy(new Vector2(0, ImGui.GetFrameHeightWithSpacing()));
    }

    protected override string MessagesPath => "/api/officer-messages";

    protected override HttpRequestMessage BuildTextMessageRequest(string channelId, string content, object? messageReference)
    {
        var url = $"{_config.ApiBaseUrl.TrimEnd('/')}{MessagesPath}";
        var body = new Dictionary<string, object?>
        {
            ["channelId"] = channelId,
            ["content"] = content,
            ["useCharacterName"] = _useCharacterName
        };
        if (messageReference != null)
        {
            body["messageReference"] = messageReference;
        }

        var request = new HttpRequestMessage(HttpMethod.Post, url);
        request.Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
        return request;
    }

    protected override async Task<HttpRequestMessage> BuildMultipartRequest(string content)
    {
        var channelId = CurrentChannelId;
        var url = $"{_config.ApiBaseUrl.TrimEnd('/')}/api/channels/{channelId}/officer-messages";
        var form = new MultipartFormDataContent();
        form.Add(new StringContent(content), "content");
        form.Add(new StringContent(_useCharacterName ? "true" : "false"), "useCharacterName");
        if (!string.IsNullOrEmpty(_replyToId))
        {
            var reference = new MessageBuilder()
                .WithMessageReference(_replyToId, channelId)
                .BuildMessageReference();
            if (reference != null)
            {
                var refJson = JsonSerializer.Serialize(reference);
                form.Add(new StringContent(refJson, Encoding.UTF8), "message_reference");
            }
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
            var channels = ChannelDtoExtensions.SortForDisplay((await _channelService.FetchAsync(global::DemiCatPlugin.ChannelKind.OfficerChat, CancellationToken.None)).ToList());
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

    private void Subscribe()
    {
        var chan = CurrentChannelId;
        _bridge.Unsubscribe(chan);
        _bridge.Subscribe(chan, _config.GuildId, ChannelKind);
        _presence?.Reset();
        _ = RefreshMessages();
        _subscribed = true;
    }

    private async Task RefreshRoles()
    {
        if (!_tokenManager.IsReady() || !ApiHelpers.ValidateApiBaseUrl(_config))
        {
            return;
        }
        try
        {
            var url = $"{_config.ApiBaseUrl.TrimEnd('/')}/roles";
            var request = new HttpRequestMessage(HttpMethod.Post, url);
            ApiHelpers.AddAuthHeader(request, _tokenManager);
            var response = await _httpClient.SendAsync(request);
            if (!response.IsSuccessStatusCode)
            {
                return;
            }
            var stream = await response.Content.ReadAsStreamAsync();
            var dto = await JsonSerializer.DeserializeAsync<RolesDto>(stream) ?? new RolesDto();
            _ = PluginServices.Instance!.Framework.RunOnTick(() =>
            {
                _config.Roles = dto.Roles;
                PluginServices.Instance!.PluginInterface.SavePluginConfig(_config);
            });
        }
        catch (Exception ex)
        {
            PluginServices.Instance!.Log.Error(ex, "Error refreshing roles");
        }
    }

    private void TryRefreshRoles()
    {
        if (DateTime.UtcNow - _lastRolesRefresh < TimeSpan.FromSeconds(30))
        {
            return;
        }
        _lastRolesRefresh = DateTime.UtcNow;
        _ = PluginServices.Instance!.Framework.RunOnTick(async () => await RefreshRoles());
    }

    private class RolesDto
    {
        public List<string> Roles { get; set; } = new();
    }

    protected override Uri BuildWebSocketUri()
    {
        return base.BuildWebSocketUri();
    }
}

