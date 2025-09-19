using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using DiscordHelper;
using Dalamud.Game.Gui.Toast;
using Dalamud.Game.Text.SeStringHandling;
using Dalamud.Game.Text.SeStringHandling.Payloads;
using Dalamud.Plugin;
using Dalamud.IoC;
using Dalamud.Plugin.Services;
using DemiCatPlugin.Avatars;
using DemiCatPlugin.Emoji;

namespace DemiCatPlugin;

public class Plugin : IDalamudPlugin
{
    public string Name => "DemiCat";

    [PluginService] internal IDalamudPluginInterface PluginInterface { get; private set; } = null!;
    [PluginService] internal ITextureProvider TextureProvider { get; private set; } = null!;

    private const uint InvalidTokenLinkCommandId = 0x44434B49;

    private readonly PluginServices _services;
    private readonly UiRenderer _ui;
    private readonly SettingsWindow _settings;
    private readonly AvatarCache _avatarCache;
    private readonly ChatWindow _chatWindow;
    private readonly OfficerChatWindow _officerChatWindow;
    private readonly DiscordPresenceService? _presenceService;
    private readonly MainWindow _mainWindow;
    private readonly ChannelWatcher _channelWatcher;
    private readonly RequestWatcher _requestWatcher;
    private readonly ChannelSelectionService _channelSelection;
    private readonly EmojiManager _emojiManager;

    private Config _config = null!;
    private readonly HttpClient _httpClient;
    private readonly ChannelService _channelService;
    private readonly Action _openMainUi;
    private readonly Action _openConfigUi;
    private readonly TokenManager _tokenManager;
    private bool _officerWatcherRunning;
    private bool _invalidTokenToastShown;

    public Plugin()
    {
        _services = PluginInterface.Create<PluginServices>()!;
        if (_services.PluginInterface == null || _services.Log == null)
            throw new InvalidOperationException("Failed to initialize plugin services.");

        _config = _services.PluginInterface.GetPluginConfig() as Config ?? new Config();
        _tokenManager = new TokenManager(_services.PluginInterface);

        var oldVersion = _config.Version;
        _config.Migrate();
        var rolesRemoved = _config.Roles.RemoveAll(r => r == "chat") > 0;
        if (rolesRemoved || _config.Version != oldVersion)
            _services.PluginInterface.SavePluginConfig(_config);

        RequestStateService.Load(_config);

        var handler = new SocketsHttpHandler
        {
            MaxConnectionsPerServer = 10,
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            AutomaticDecompression = DecompressionMethods.All
        };
        _httpClient = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(10) };

        PingService.Instance = new PingService(_httpClient, _config, _tokenManager);

        _channelSelection = new ChannelSelectionService(_config);
        _emojiManager = new EmojiManager(_httpClient, _tokenManager, _config);
        _ui = new UiRenderer(_config, _httpClient, _channelSelection, _emojiManager);
        _settings = new SettingsWindow(_config, _tokenManager, _httpClient, () => RefreshRoles(_services.Log), _ui.StartNetworking, _services.Log, _services.PluginInterface);

        _presenceService = _config.SyncedChat && _config.EnableFcChat
            ? new DiscordPresenceService(_config, _httpClient)
            : null;

        _channelService = new ChannelService(_config, _httpClient, _tokenManager);
        _avatarCache = new AvatarCache(TextureProvider, _httpClient);
        _chatWindow = new FcChatWindow(_config, _httpClient, _presenceService, _tokenManager, _channelService, _channelSelection, _avatarCache, _emojiManager);
        _officerChatWindow = new OfficerChatWindow(_config, _httpClient, _presenceService, _tokenManager, _channelService, _channelSelection, _avatarCache, _emojiManager);

        _presenceService?.Reset();

        _mainWindow = new MainWindow(
            _config,
            _ui,
            _chatWindow,
            _officerChatWindow,
            _settings,
            _httpClient,
            _channelService,
            _channelSelection,
            () => RefreshRoles(_services.Log),
            _emojiManager
        );

        _channelWatcher = new ChannelWatcher(_config, _ui, _mainWindow.EventCreateWindow, _mainWindow.TemplatesWindow, _chatWindow, _officerChatWindow, _tokenManager, _httpClient);
        _requestWatcher = new RequestWatcher(_config, _httpClient, _tokenManager);

        _channelSelection.ChannelChanged += HandleChannelSelectionValidation;

        _settings.MainWindow = _mainWindow;
        _settings.ChatWindow = _chatWindow;
        _settings.OfficerChatWindow = _officerChatWindow;
        _settings.ChannelWatcher = _channelWatcher;
        _settings.RequestWatcher = _requestWatcher;

        _mainWindow.HasOfficerRole = _config.Roles.Contains("officer");

        _ = RoleCache.EnsureLoaded(_httpClient, _config);

        // Note: API 13 removed UiBuilder.BuildFonts/RebuildFonts and the old ImGuiNET attach path.
        // We intentionally skip emoji font merging since ImGui/Dalamud cannot render SMP color emoji anyway.

        _services.PluginInterface.UiBuilder.Draw += _mainWindow.Draw;
        _services.PluginInterface.UiBuilder.Draw += _settings.Draw;

        _openMainUi = () => _mainWindow.IsOpen = true;
        _services.PluginInterface.UiBuilder.OpenMainUi += _openMainUi;

        _openConfigUi = () => _settings.IsOpen = true;
        _services.PluginInterface.UiBuilder.OpenConfigUi += _openConfigUi;

        _tokenManager.OnLinked += HandleTokenLinked;
        _tokenManager.OnUnlinked += HandleTokenUnlinked;

        if (_tokenManager.IsReady())
            HandleTokenLinked();

        _services.Log.Info("DemiCat loaded.");
    }

    public void Dispose()
    {
        // Unsubscribe UI draw handlers
        _services.PluginInterface.UiBuilder.Draw -= _mainWindow.Draw;
        _services.PluginInterface.UiBuilder.Draw -= _settings.Draw;

        // Unsubscribe UI open handlers
        _services.PluginInterface.UiBuilder.OpenMainUi -= _openMainUi;
        _services.PluginInterface.UiBuilder.OpenConfigUi -= _openConfigUi;

        _tokenManager.OnLinked -= HandleTokenLinked;
        _tokenManager.OnUnlinked -= HandleTokenUnlinked;

        try { _services.ChatGui.RemoveChatLinkHandler(InvalidTokenLinkCommandId); } catch { }

        _channelSelection.ChannelChanged -= HandleChannelSelectionValidation;

        _channelWatcher.Dispose();
        _requestWatcher.Dispose();
        _presenceService?.Dispose();
        _chatWindow.Dispose();
        _officerChatWindow.Dispose();
        _mainWindow.Dispose();
        _ui.DisposeAsync().GetAwaiter().GetResult();
        _settings.Dispose();
        _avatarCache.Dispose();
        _emojiManager.Dispose();
        _httpClient.Dispose();
    }

    private async void HandleChannelSelectionValidation(string kind, string guildId, string oldId, string newId)
    {
        await ValidateChannelSelectionAsync(kind, guildId, newId);
    }

    private async Task ValidateChannelSelectionAsync(string kind, string guildId, string channelId)
    {
        if (string.IsNullOrWhiteSpace(channelId))
        {
            return;
        }

        if (
            !string.Equals(
                ChannelKeyHelper.NormalizeGuildId(guildId),
                ChannelKeyHelper.NormalizeGuildId(_config.GuildId),
                StringComparison.Ordinal
            )
        )
        {
            return;
        }

        if (!_tokenManager.IsReady())
        {
            return;
        }

        try
        {
            var response = await _channelService.ValidateAsync(kind, channelId, CancellationToken.None);
            if (response == null)
            {
                return;
            }

            if (response.Ok)
            {
                return;
            }

            var reason = response.Reason ?? string.Empty;

            if (string.Equals(reason, "WRONG_KIND", StringComparison.OrdinalIgnoreCase))
            {
                PluginServices.Instance?.Framework.RunOnTick(() =>
                {
                    _channelWatcher.TriggerRefresh(true);
                });
            }
            else if (string.Equals(reason, "FORBIDDEN", StringComparison.OrdinalIgnoreCase))
            {
                PluginServices.Instance?.Framework.RunOnTick(() =>
                {
                    PluginServices.Instance?.ToastGui.ShowError(
                        "You do not have permission to use that channel."
                    );
                });
            }
            else if (string.Equals(reason, "NOT_FOUND", StringComparison.OrdinalIgnoreCase))
            {
                PluginServices.Instance?.Framework.RunOnTick(() =>
                {
                    PluginServices.Instance?.ToastGui.ShowError(
                        "Selected channel is no longer configured."
                    );
                });
            }
        }
        catch (Exception ex)
        {
            PluginServices.Instance?.Log.Warning(
                ex,
                "Failed to validate channel selection {ChannelId} ({Kind})",
                channelId,
                kind
            );
        }
    }

    private void StartWatchers()
    {
        _services.Log.Info("Starting watchers");
        _ = StartWatchersAsync();
    }

    private void HandleTokenLinked()
    {
        _invalidTokenToastShown = false;
        try { _services.ChatGui.RemoveChatLinkHandler(InvalidTokenLinkCommandId); } catch { }
        StartWatchers();
    }

    private void HandleTokenUnlinked(string? reason)
    {
        StopWatchers();

        if (IsAuthenticationFailure(reason))
        {
            ShowInvalidTokenToast();
        }
    }

    private async Task StartWatchersAsync()
    {
        if (!_tokenManager.IsReady() || !ApiHelpers.ValidateApiBaseUrl(_config))
            return;

        var pingService = PingService.Instance ?? new PingService(_httpClient, _config, _tokenManager);
        HttpResponseMessage? response = null;
        try
        {
            response = await pingService.PingAsync(CancellationToken.None);
        }
        catch (Exception ex)
        {
            _services.Log.Warning(ex, "Failed to ping DemiCat backend while starting watchers");
        }

        if (response == null)
        {
            HandleWatcherStartupFailure(string.Empty);
            return;
        }

        if (response.StatusCode == HttpStatusCode.Unauthorized || response.StatusCode == HttpStatusCode.Forbidden)
        {
            _services.Log.Warning($"Ping returned {(int)response.StatusCode} {response.StatusCode}; clearing token");
            _tokenManager.Clear("Invalid API key");
            return;
        }

        if (!response.IsSuccessStatusCode)
        {
            var statusText = $" ({(int)response.StatusCode} {response.StatusCode})";
            HandleWatcherStartupFailure(statusText);
            return;
        }

        if (_config.Requests)
        {
            _services.Log.Info("Starting request watcher");
            _requestWatcher.Start();
        }

        var hasOfficerRole = _config.Roles.Contains("officer");
        if (_config.Events || _config.SyncedChat || hasOfficerRole)
        {
            _services.Log.Info("Starting channel watcher");
            _ = _channelWatcher.Start();
        }

        if (_config.SyncedChat && _config.EnableFcChat)
        {
            _services.Log.Info("Starting chat window networking");
            _chatWindow.StartNetworking();
        }

        if (hasOfficerRole)
        {
            if (!_officerWatcherRunning)
            {
                _services.Log.Info("Starting officer chat window networking");
                _officerChatWindow.StartNetworking();
                _officerWatcherRunning = true;
            }
        }
        else
        {
        _officerWatcherRunning = false;
    }

    private static bool IsAuthenticationFailure(string? reason)
        => string.Equals(reason, "Invalid API key", StringComparison.Ordinal)
            || string.Equals(reason, "Authentication failed", StringComparison.Ordinal);

    private void ShowInvalidTokenToast()
    {
        if (_invalidTokenToastShown)
            return;

        var toastGui = _services.ToastGui;
        var chatGui = _services.ChatGui;

        if (toastGui == null || chatGui == null)
            return;

        if (_openConfigUi == null)
            return;

        _invalidTokenToastShown = true;

        try { chatGui.RemoveChatLinkHandler(InvalidTokenLinkCommandId); } catch { }

        DalamudLinkPayload linkPayload;
        try
        {
            linkPayload = chatGui.AddChatLinkHandler(
                InvalidTokenLinkCommandId,
                (_, _) =>
                {
                    try
                    {
                        var framework = _services.Framework;
                        if (framework != null)
                        {
                            _ = framework.RunOnTick(() => _openConfigUi());
                        }
                        else
                        {
                            _openConfigUi();
                        }
                    }
                    catch
                    {
                    }
                });
        }
        catch
        {
            return;
        }

        var message = new SeStringBuilder()
            .AddText("DemiCat sync is disabled because the stored API key is invalid.")
            .Add(new NewLinePayload())
            .Add(linkPayload)
            .AddUiForeground("Open settings", 45)
            .Add(RawPayload.LinkTerminator)
            .AddText(" to update your sync key.")
            .Build();

        var options = new QuestToastOptions
        {
            Position = QuestToastPosition.Centre,
            DisplayCheckmark = true,
            PlaySound = true
        };

        toastGui.ShowQuest(message, options);
    }

        if (_config.Events)
        {
            _services.Log.Info("Starting event watchers");
            _ = _ui.StartNetworking();
            _mainWindow.EventCreateWindow.StartNetworking();
            _mainWindow.TemplatesWindow.StartNetworking();
        }

        _services.Log.Info("Watchers started");
    }

    private void HandleWatcherStartupFailure(string statusDetails)
    {
        var suffix = string.IsNullOrEmpty(statusDetails) ? string.Empty : statusDetails;
        var message = $"Unable to reach the DemiCat backend{suffix}. Watchers were not started.";
        _services.Log.Warning(message);
        PluginServices.Instance?.ToastGui.ShowError(message);
    }

    private void StopWatchers()
    {
        _services.Log.Info("Stopping watchers");
        _requestWatcher.Stop();
        _channelWatcher.Stop();
        _chatWindow.StopNetworking();
        _officerChatWindow.StopNetworking();
        _officerWatcherRunning = false;
        _ui.StopNetworking();
        _mainWindow.TemplatesWindow.StopNetworking();
        _services.Log.Info("Watchers stopped");
    }

    private async Task<bool> RefreshRoles(IPluginLog log)
    {
        if (!_tokenManager.IsReady() || !ApiHelpers.ValidateApiBaseUrl(_config))
            return false;

        try
        {
            var url = $"{_config.ApiBaseUrl.TrimEnd('/')}/roles";
            var request = new HttpRequestMessage(HttpMethod.Post, url);
            ApiHelpers.AddAuthHeader(request, _tokenManager);
            log.Info($"Requesting roles from {url}");
            var response = await _httpClient.SendAsync(request);
            if (response.StatusCode == HttpStatusCode.Unauthorized || response.StatusCode == HttpStatusCode.Forbidden)
            {
                var responseBody = await response.Content.ReadAsStringAsync();
                log.Error($"Failed to fetch roles: {response.StatusCode}. Response Body: {responseBody}");
                _tokenManager.Clear("Authentication failed");
                return false;
            }
            if (!response.IsSuccessStatusCode)
            {
                var responseBody = await response.Content.ReadAsStringAsync();
                log.Error($"Failed to fetch roles: {response.StatusCode}. Response Body: {responseBody}");
                return false;
            }

            var stream = await response.Content.ReadAsStreamAsync();
            var dto = await JsonSerializer.DeserializeAsync<RolesDto>(stream) ?? new RolesDto();
            log.Info($"Roles received: {string.Join(", ", dto.Roles)}");

            var channelUrl = $"{_config.ApiBaseUrl.TrimEnd('/')}/api/channels";
            var channelRequest = new HttpRequestMessage(HttpMethod.Get, channelUrl);
            ApiHelpers.AddAuthHeader(channelRequest, _tokenManager);
            List<ChannelDto> chatChannels = new();

            try
            {
                var channelResponse = await _httpClient.SendAsync(channelRequest);
                if (channelResponse.StatusCode == HttpStatusCode.Unauthorized || channelResponse.StatusCode == HttpStatusCode.Forbidden)
                {
                    var responseBody = await channelResponse.Content.ReadAsStringAsync();
                    log.Error($"Failed to fetch channels: {channelResponse.StatusCode}. Response Body: {responseBody}");
                    _tokenManager.Clear("Authentication failed");
                    return false;
                }

                if (channelResponse.IsSuccessStatusCode)
                {
                    var channelStream = await channelResponse.Content.ReadAsStreamAsync();
                    var channelsDto = await JsonSerializer.DeserializeAsync<ChannelListDto>(channelStream) ?? new ChannelListDto();
                    chatChannels = channelsDto.Chat;
                    foreach (var channel in chatChannels)
                    {
                        channel.EnsureKind(ChannelKind.FcChat);
                    }
                }
                else
                {
                    var responseBody = await channelResponse.Content.ReadAsStringAsync();
                    log.Error($"Failed to fetch channels: {channelResponse.StatusCode}. Response Body: {responseBody}");
                }
            }
            catch (Exception ex)
            {
                log.Error(ex, "Error fetching channels.");
            }

            var hasChat = chatChannels.Count > 0;

            _ = _services.Framework.RunOnTick(() =>
            {
                dto.Roles.RemoveAll(r => r == "chat");
                _config.Roles = dto.Roles;
                _mainWindow.HasOfficerRole = _config.Roles.Contains("officer");

                if (!hasChat)
                {
                    _config.EnableFcChat = false;
                    _config.EnableFcChatUserSet = false;
                }
                else if (!_config.EnableFcChatUserSet)
                {
                    _config.EnableFcChat = true;
                }

                _chatWindow.ChannelsLoaded = false;
                if (!_config.EnableFcChat)
                {
                    _chatWindow.StopNetworking();
                    _presenceService?.Dispose();
                }
                _services.PluginInterface.SavePluginConfig(_config);
                StopWatchers();
                StartWatchers();
                if (_config.Requests)
                {
                    _services.Log.Info("Restarting request watcher");
                    _requestWatcher.Start();
                }
            });

            await RoleCache.Refresh(_httpClient, _config);
            return true;
        }
        catch (Exception ex)
        {
            log.Error(ex, "Error refreshing roles.");
            return false;
        }
    }

    private class RolesDto
    {
        [JsonPropertyName("roles")]
        public List<string> Roles { get; set; } = new();
    }

    private class ChannelListDto
    {
        [JsonPropertyName(ChannelKind.FcChat)] public List<ChannelDto> Chat { get; set; } = new();
    }
}
