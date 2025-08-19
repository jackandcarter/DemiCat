using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using DiscordHelper;
using Dalamud.Plugin;
using Dalamud.IoC;
using Dalamud.Plugin.Services;

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
    private readonly MainWindow _mainWindow;
    private Config _config;
    private readonly HttpClient _httpClient = new();
    private readonly Action _openMainUi;
    private readonly Action _openConfigUi;

    public Plugin()
    {
        _services = PluginInterface.Create<PluginServices>()!;
        if (_services.PluginInterface == null || _services.Log == null)
        {
            throw new InvalidOperationException("Failed to initialize plugin services.");
        }

        _config = _services.PluginInterface.GetPluginConfig() as Config ?? new Config();
        var oldVersion = _config.Version;
        _config.Migrate();
        var rolesRemoved = _config.Roles.RemoveAll(r => r == "chat") > 0;
        if (rolesRemoved || _config.Version != oldVersion)
        {
            _services.PluginInterface.SavePluginConfig(_config);
        }

        _ui = new UiRenderer(_config, _httpClient);
        _settings = new SettingsWindow(_config, _httpClient, () => RefreshRoles(_services.Log), _ui.StartNetworking, _services.Log, _services.PluginInterface);
        _chatWindow = new FcChatWindow(_config, _httpClient);
        _officerChatWindow = new OfficerChatWindow(_config, _httpClient);
        _mainWindow = new MainWindow(_config, _ui, _chatWindow, _officerChatWindow, _settings, _httpClient);
        _settings.MainWindow = _mainWindow;
        _settings.ChatWindow = _chatWindow;
        _settings.OfficerChatWindow = _officerChatWindow;

        _mainWindow.HasOfficerRole = _config.Roles.Contains("officer");

        if (_config.Enabled && _config.Roles.Count == 0)
        {
            _ = RefreshRoles(_services.Log);
        }

        _services.PluginInterface.UiBuilder.Draw += _mainWindow.Draw;
        _services.PluginInterface.UiBuilder.Draw += _settings.Draw;
        _openMainUi = () => _mainWindow.IsOpen = true;
        _services.PluginInterface.UiBuilder.OpenMainUi += _openMainUi;
        _openConfigUi = () => _settings.IsOpen = true;
        _services.PluginInterface.UiBuilder.OpenConfigUi += _openConfigUi;

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

        _httpClient.Dispose();
        _chatWindow.Dispose();
        _officerChatWindow.Dispose();
        _mainWindow.Dispose();
        _ui.Dispose();
        _settings.Dispose();
    }

    private async Task<bool> RefreshRoles(IPluginLog log)
    {
        if (string.IsNullOrEmpty(_config.AuthToken) || !ApiHelpers.ValidateApiBaseUrl(_config))
        {
            return false;
        }

        try
        {
            var url = $"{_config.ApiBaseUrl.TrimEnd('/')}/roles";
            var request = new HttpRequestMessage(HttpMethod.Post, url);
            if (!string.IsNullOrEmpty(_config.AuthToken))
            {
                request.Headers.Add("X-Api-Key", _config.AuthToken);
            }
            log.Info($"Requesting roles from {url}");
            var response = await _httpClient.SendAsync(request);
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
            if (!string.IsNullOrEmpty(_config.AuthToken))
            {
                channelRequest.Headers.Add("X-Api-Key", _config.AuthToken);
            }
            List<ChannelDto> chatChannels = new();
            try
            {
                var channelResponse = await _httpClient.SendAsync(channelRequest);
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
                _config.EnableFcChat = hasChat;
                _chatWindow.ChannelsLoaded = false;
                _services.PluginInterface.SavePluginConfig(_config);
            });
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
        [JsonPropertyName("fc_chat")] public List<ChannelDto> Chat { get; set; } = new();
    }
}
