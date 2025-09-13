using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using DiscordHelper;
using Dalamud.Plugin;
using Dalamud.IoC;
using Dalamud.Plugin.Services;
using Dalamud.Bindings.ImGui;
using System.IO;

namespace DemiCatPlugin;

public class Plugin : IDalamudPlugin
{
    public string Name => "DemiCat";

    [PluginService] internal IDalamudPluginInterface PluginInterface { get; private set; } = null!;

    private readonly PluginServices _services;
    private readonly UiRenderer _ui;
    private readonly SettingsWindow _settings;
    private readonly ChatWindow _chatWindow;
    private readonly OfficerChatWindow _officerChatWindow;
    private readonly DiscordPresenceService? _presenceService;
    private readonly MainWindow _mainWindow;
    private readonly ChannelWatcher _channelWatcher;
    private readonly RequestWatcher _requestWatcher;
    private Config _config;
    private readonly HttpClient _httpClient;
    private readonly ChannelService _channelService;
    private readonly Action _openMainUi;
    private readonly Action _openConfigUi;
    private readonly TokenManager _tokenManager;
    private readonly Action<string?> _unlinkedHandler;

    private static readonly uint[] EmojiRanges =
    {
        0x1F300, 0x1F5FF,
        0x1F600, 0x1F64F,
        0x1F680, 0x1F6FF,
        0x1F700, 0x1F77F,
        0x1F780, 0x1F7FF,
        0x1F800, 0x1F8FF,
        0x1F900, 0x1F9FF,
        0x1FA00, 0x1FAFF,
        0
    };

    public Plugin()
    {
        _services = PluginInterface.Create<PluginServices>()!;
        if (_services.PluginInterface == null || _services.Log == null)
        {
            throw new InvalidOperationException("Failed to initialize plugin services.");
        }

        _config = _services.PluginInterface.GetPluginConfig() as Config ?? new Config();
        _tokenManager = new TokenManager(_services.PluginInterface);
        var oldVersion = _config.Version;
        _config.Migrate();
        var rolesRemoved = _config.Roles.RemoveAll(r => r == "chat") > 0;
        if (rolesRemoved || _config.Version != oldVersion)
        {
            _services.PluginInterface.SavePluginConfig(_config);
        }

        RequestStateService.Load(_config);

        var handler = new SocketsHttpHandler
        {
            MaxConnectionsPerServer = 10,
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            AutomaticDecompression = DecompressionMethods.All
        };
        _httpClient = new HttpClient(handler);
        _httpClient.Timeout = TimeSpan.FromSeconds(10);

        _ui = new UiRenderer(_config, _httpClient);
        _settings = new SettingsWindow(_config, _tokenManager, _httpClient, () => RefreshRoles(_services.Log), _ui.StartNetworking, _services.Log, _services.PluginInterface);
        _presenceService = _config.SyncedChat && _config.EnableFcChat
            ? new DiscordPresenceService(_config, _httpClient)
            : null;
        _channelService = new ChannelService(_config, _httpClient, _tokenManager);
        _chatWindow = new FcChatWindow(_config, _httpClient, _presenceService, _tokenManager, _channelService);
        _officerChatWindow = new OfficerChatWindow(_config, _httpClient, _presenceService, _tokenManager, _channelService);
        _presenceService?.Reset();
        _mainWindow = new MainWindow(
            _config,
            _ui,
            _chatWindow,
            _officerChatWindow,
            _settings,
            _httpClient,
            _channelService,
            () => RefreshRoles(_services.Log));
        _channelWatcher = new ChannelWatcher(_config, _ui, _mainWindow.EventCreateWindow, _chatWindow, _officerChatWindow, _tokenManager, _httpClient);
        _requestWatcher = new RequestWatcher(_config, _httpClient, _tokenManager);
        _settings.MainWindow = _mainWindow;
        _settings.ChatWindow = _chatWindow;
        _settings.OfficerChatWindow = _officerChatWindow;
        _settings.ChannelWatcher = _channelWatcher;
        _settings.RequestWatcher = _requestWatcher;

        _mainWindow.HasOfficerRole = _config.Roles.Contains("officer");

        _ = RoleCache.EnsureLoaded(_httpClient, _config);

        AddEmojiFont();

        _services.PluginInterface.UiBuilder.Draw += _mainWindow.Draw;
        _services.PluginInterface.UiBuilder.Draw += _settings.Draw;
        _openMainUi = () => _mainWindow.IsOpen = true;
        _services.PluginInterface.UiBuilder.OpenMainUi += _openMainUi;
        _openConfigUi = () => _settings.IsOpen = true;
        _services.PluginInterface.UiBuilder.OpenConfigUi += _openConfigUi;

        _unlinkedHandler = _ => StopWatchers();
        _tokenManager.OnLinked += StartWatchers;
        _tokenManager.OnUnlinked += _unlinkedHandler;
        if (_tokenManager.IsReady())
        {
            StartWatchers();
        }
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

        _tokenManager.OnLinked -= StartWatchers;
        _tokenManager.OnUnlinked -= _unlinkedHandler;

        _channelWatcher.Dispose();
        _requestWatcher.Dispose();
        _presenceService?.Dispose();
        _chatWindow.Dispose();
        _officerChatWindow.Dispose();
        _mainWindow.Dispose();
        _ui.DisposeAsync().GetAwaiter().GetResult();
        _settings.Dispose();
        _httpClient.Dispose();
    }

    private void StartWatchers() => _ = StartWatchersAsync();

    private async Task StartWatchersAsync()
    {
        if (!_tokenManager.IsReady() || !ApiHelpers.ValidateApiBaseUrl(_config))
            return;

        var response = await ApiHelpers.PingAsync(_httpClient, _config, _tokenManager, CancellationToken.None);
        if (response?.IsSuccessStatusCode != true)
        {
            var reason = response?.StatusCode == HttpStatusCode.Unauthorized ||
                         response?.StatusCode == HttpStatusCode.Forbidden
                ? "Invalid API key"
                : "Network error";
            _tokenManager.Clear(reason);
            return;
        }

        if (_config.Requests)
        {
            _requestWatcher.Start();
        }
        var hasOfficerRole = _config.Roles.Contains("officer");
        if (_config.Events || _config.SyncedChat || hasOfficerRole)
        {
            _ = _channelWatcher.Start();
        }
        if (_config.SyncedChat && _config.EnableFcChat)
        {
            _chatWindow.StartNetworking();
        }
        if (hasOfficerRole)
        {
            _officerChatWindow.StartNetworking();
        }
        if (_config.Events)
        {
            _ = _ui.StartNetworking();
            _mainWindow.EventCreateWindow.StartNetworking();
            _mainWindow.TemplatesWindow.StartNetworking();
        }
    }

    private void StopWatchers()
    {
        _requestWatcher.Stop();
        _channelWatcher.Stop();
        _chatWindow.StopNetworking();
        _officerChatWindow.StopNetworking();
        _ui.StopNetworking();
        _mainWindow.TemplatesWindow.StopNetworking();
    }

    private async Task<bool> RefreshRoles(IPluginLog log)
    {
        if (!_tokenManager.IsReady() || !ApiHelpers.ValidateApiBaseUrl(_config))
        {
            return false;
        }

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

    private unsafe void AddEmojiFont()
    {
        try
        {
            var dir = _services.PluginInterface.AssemblyLocation.Directory?.FullName;
            if (dir == null) return;
            var fontPath = Path.Combine(dir, "NotoColorEmoji.ttf");
            if (!File.Exists(fontPath))
            {
                _services.Log.Warning($"Emoji font not found at {fontPath}; emojis may appear monochrome.");
                return;
            }
            var io = ImGui.GetIO();
            var cfg = ImGuiNative.ImFontConfig_ImFontConfig();
            cfg.MergeMode = true;
            fixed (uint* ranges = EmojiRanges)
            {
                io.Fonts.AddFontFromFileTTF(fontPath, io.Fonts.Fonts[0]?.FontSize ?? 16f, cfg, (IntPtr)ranges);
            }
            cfg.Destroy();
            _services.PluginInterface.UiBuilder.RebuildFonts();
        }
        catch (Exception ex)
        {
            _services.Log.Error(ex, "Failed to load emoji font");
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
