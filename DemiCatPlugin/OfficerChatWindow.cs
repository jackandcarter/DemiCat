using Dalamud.Bindings.ImGui;
using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
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
    private const string NoOfficerAccessMessage = "No officer access for this key.";
    private DateTime _lastRolesRefresh = DateTime.MinValue;
    private bool _subscribed;
    private readonly PresenceSidebar? _presenceSidebar;
    private float _presenceWidth = 200f;

    protected override bool MentionsEnabled => true;

    public OfficerChatWindow(
        Config config,
        HttpClient httpClient,
        DiscordPresenceService? presence,
        TokenManager tokenManager,
        ChannelService channelService,
        ChannelSelectionService channelSelection,
        MessageCache messageCache,
        AvatarCache avatarCache,
        EmojiManager emojiManager,
        IChatBridge? chatBridge = null)
        : base(
            config,
            httpClient,
            presence,
            tokenManager,
            channelService,
            channelSelection,
            messageCache,
            global::DemiCatPlugin.ChannelKind.OfficerChat,
            avatarCache,
            emojiManager,
            chatBridge)
    {
        if (presence != null)
        {
            _presenceSidebar = new PresenceSidebar(presence)
            {
                TextureLoader = LoadTexture,
                TextureTouch = TextureTouchAction
            };
        }

        _bridge.StatusChanged += OnBridgeStatusChangedForOfficer;
    }

    private void OnBridgeStatusChangedForOfficer(string s)
    {
        if (s.Contains("Forbidden", StringComparison.OrdinalIgnoreCase))
        {
            TryRefreshRoles();
        }
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
            new MessageCache(),
            null!,
            new EmojiManager(httpClient, tokenManager, config))
    {
    }
#endif

    public override void StartNetworking()
    {
        if (!OfficerPermissions.HasAccess(_config))
        {
            MarkNetworkingStopped();
            _bridge.Stop();
            _presence?.SetPresenceReady(false);

            var message = NoOfficerAccessMessage;
            var services = PluginServices.Instance;
            var framework = services?.Framework;

            if (framework != null)
            {
                try
                {
                    framework.RunOnTick(() => _statusMessage = message);
                }
                catch (Exception ex)
                {
                    services?.Log.Warning(ex, "Failed to update officer status message on framework thread.");
                    _statusMessage = message;
                }
            }
            else
            {
                _statusMessage = message;
            }

            _subscribed = false;
            return;
        }

        if (!MarkNetworkingStarted())
        {
            Subscribe();
            TryRefreshRoles();
            return;
        }

        _presence?.SetPresenceReady(true);
        _bridge.Start();
        Subscribe();
        TryRefreshRoles();
        _presence?.Reset();
    }

    public override void Draw()
    {
        if (!OfficerPermissions.HasAccess(_config))
        {
            var message = string.IsNullOrEmpty(_statusMessage)
                ? NoOfficerAccessMessage
                : _statusMessage;
            ImGui.TextUnformatted(message);
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

        var showPresence = _presenceSidebar != null && _tokenManager.IsReady();
        if (showPresence)
        {
            _ = RoleCache.EnsureLoaded(_httpClient, _config);
            _presenceSidebar!.Draw(ref _presenceWidth);
            ImGui.SameLine();
            ImGui.BeginChild("##officerChat", ImGui.GetContentRegionAvail(), false);
        }

        base.Draw();

        // Reserved padded area beneath the standard chat input for upcoming officer tools.
        ImGui.Dummy(new Vector2(0, ImGui.GetFrameHeightWithSpacing()));

        if (showPresence)
        {
            ImGui.EndChild();
        }
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

    protected override async Task<HttpRequestMessage> BuildMultipartRequest(string channelId, string content)
    {
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
            var body = CreateEditMessageBody(content);
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

    protected override async Task FetchChannels(bool refreshed = false, CancellationToken cancellationToken = default)
    {
        if (_channelsLoading && !refreshed)
        {
            return;
        }

        if (!_tokenManager.IsReady())
        {
            _channelsLoaded = true;
            _channelsLoading = false;
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
                _channelsLoading = false;
            });
            return;
        }

        _channelsLoading = true;

        try
        {
            var channels = ChannelDtoExtensions.SortForDisplay((await _channelService.FetchAsync(global::DemiCatPlugin.ChannelKind.OfficerChat, cancellationToken)).ToList());
            if (await ChannelNameResolver.Resolve(channels, _httpClient, _config, refreshed, () => FetchChannels(true)))
            {
                if (refreshed)
                {
                    _ = PluginServices.Instance!.Framework.RunOnTick(() =>
                    {
                        _channelFetchFailed = true;
                        _channelErrorMessage = "Failed to load channels";
                        _channelsLoaded = true;
                        _channelsLoading = false;
                    });
                }
                return;
            }
            _ = PluginServices.Instance!.Framework.RunOnTick(() =>
            {
                var preparedChannels = PrepareChannelsForDisplay(channels);
                ApplyPreparedChannels(preparedChannels);
                _channelsLoaded = true;
                _channelFetchFailed = false;
                _channelErrorMessage = string.Empty;
                _channelsLoading = false;
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
                _channelsLoading = false;
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
                _channelsLoading = false;
            });
        }
    }

    private void Subscribe()
    {
        var subscribed = TrySubscribeCurrentChannel(force: true);
        if (!subscribed)
        {
            _subscribed = false;
        }
    }

    protected override void OnSubscriptionStateChanged(bool isSubscribed)
    {
        base.OnSubscriptionStateChanged(isSubscribed);
        _subscribed = isSubscribed && HasActiveSubscription;
        if (_subscribed)
        {
            _presence?.Reset();
        }
    }

    private async Task RefreshRoles()
    {
        if (!_tokenManager.IsReady() || !ApiHelpers.ValidateApiBaseUrl(_config))
        {
            return;
        }

        var services = PluginServices.Instance;
        try
        {
            var url = $"{_config.ApiBaseUrl.TrimEnd('/')}/roles";
            var request = new HttpRequestMessage(HttpMethod.Post, url);
            ApiHelpers.AddAuthHeader(request, _tokenManager);
            var response = await _httpClient.SendAsync(request);

            if (response.StatusCode == HttpStatusCode.Unauthorized)
            {
                var responseBody = await response.Content.ReadAsStringAsync();
                services?.Log.Error($"Failed to refresh roles: {response.StatusCode}. Response Body: {responseBody}");
                _tokenManager.Clear("Authentication failed");
                services?.ToastGui?.ShowError("Authentication failed – please relink.");
                return;
            }

            if (response.StatusCode == HttpStatusCode.Forbidden)
            {
                var responseBody = await response.Content.ReadAsStringAsync();
                services?.Log.Error($"Failed to refresh roles: {response.StatusCode}. Response Body: {responseBody}");
                services?.ToastGui?.ShowError("Forbidden – check API key/roles");
                return;
            }

            if (!response.IsSuccessStatusCode)
            {
                var responseBody = await response.Content.ReadAsStringAsync();
                services?.Log.Warning($"Failed to refresh roles. Status: {response.StatusCode}. Response Body: {responseBody}");
                services?.ToastGui?.ShowError("Failed to refresh officer roles.");
                return;
            }

            var stream = await response.Content.ReadAsStreamAsync();
            var dto = await JsonSerializer.DeserializeAsync<RolesDto>(stream) ?? new RolesDto();
            var filteredRoles = dto.Roles != null
                ? new List<string>(dto.Roles)
                : new List<string>();
            filteredRoles.RemoveAll(r => string.Equals(r, "chat", StringComparison.Ordinal));

            if (filteredRoles.Count == 0)
            {
                services?.Log.Warning("Received empty role list while refreshing officer roles.");
                if (_config.Roles.Count > 0)
                {
                    services?.ToastGui?.ShowError("Failed to refresh officer roles; keeping cached permissions.");
                }
                return;
            }

            void ApplyRoles()
            {
                _config.Roles = filteredRoles;
                services?.PluginInterface?.SavePluginConfig(_config);
                var mainWindow = MainWindow.Instance;
                if (mainWindow != null)
                {
                    mainWindow.HasOfficerAccess = OfficerPermissions.HasAccess(_config);
                }
            }

            var framework = services?.Framework;
            if (framework != null)
            {
                await framework.RunOnTick(ApplyRoles);
            }
            else
            {
                ApplyRoles();
            }
        }
        catch (Exception ex)
        {
            services?.Log.Error(ex, "Error refreshing roles");
            services?.ToastGui?.ShowError("Failed to refresh officer roles.");
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

    public new void Dispose()
    {
        _bridge.StatusChanged -= OnBridgeStatusChangedForOfficer;
        base.Dispose();
    }

    public void DrawThemedWindow(ref bool isOpen, ImGuiWindowFlags flags = ImGuiWindowFlags.None)
        => base.DrawThemedWindow("Officer Chat", ref isOpen, flags);

    private class RolesDto
    {
        [JsonPropertyName("roles")]
        public List<string> Roles { get; set; } = new();
    }

    protected override Uri BuildWebSocketUri()
    {
        return base.BuildWebSocketUri();
    }
}

