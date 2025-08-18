using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text.Json;
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
    [PluginService] internal IPluginLog Log { get; private set; } = null!;

    private readonly UiRenderer _ui;
    private readonly SettingsWindow _settings;
    private readonly ChatWindow? _chatWindow;
    private readonly OfficerChatWindow _officerChatWindow;
    private readonly MainWindow _mainWindow;
    private Config _config;
    private readonly HttpClient _httpClient = new();
    private readonly Action _openMainUi;
    private readonly Action _openConfigUi;

    public Plugin()
    {
        PluginInterface.Create<PluginServices>();
        if (PluginServices.PluginInterface == null || PluginServices.Log == null)
        {
            throw new InvalidOperationException("Failed to initialize plugin services.");
        }

        _config = PluginInterface.GetPluginConfig() as Config ?? new Config();
        var oldVersion = _config.Version;
        _config.Migrate();
        var rolesRemoved = _config.Roles.RemoveAll(r => r == "chat") > 0;
        if (rolesRemoved || _config.Version != oldVersion)
        {
            PluginServices.PluginInterface.SavePluginConfig(_config);
        }

        _ui = new UiRenderer(_config, _httpClient);
        _settings = new SettingsWindow(_config, _httpClient, () => RefreshRoles(Log), Log);
        _chatWindow = _config.EnableFcChat ? new FcChatWindow(_config, _httpClient) : null;
        _officerChatWindow = new OfficerChatWindow(_config, _httpClient);
        _mainWindow = new MainWindow(_config, _ui, _chatWindow, _officerChatWindow, _settings, _httpClient);

        _mainWindow.HasOfficerRole = _config.Roles.Contains("officer");

        if (_config.Enabled && _config.Roles.Count == 0)
        {
            _ = RefreshRoles(Log);
        }

        PluginInterface.UiBuilder.Draw += _mainWindow.Draw;
        PluginInterface.UiBuilder.Draw += _settings.Draw;
        _openMainUi = () => _mainWindow.IsOpen = true;
        PluginInterface.UiBuilder.OpenMainUi += _openMainUi;
        _openConfigUi = () => _settings.IsOpen = true;
        PluginInterface.UiBuilder.OpenConfigUi += _openConfigUi;

        Log.Info("DemiCat loaded.");
    }


    public void Dispose()
    {
        // Unsubscribe UI draw handlers
        PluginInterface.UiBuilder.Draw -= _mainWindow.Draw;
        PluginInterface.UiBuilder.Draw -= _settings.Draw;

        // Unsubscribe UI open handlers
        PluginInterface.UiBuilder.OpenMainUi -= _openMainUi;
        PluginInterface.UiBuilder.OpenConfigUi -= _openConfigUi;

        _httpClient.Dispose();
        _chatWindow?.Dispose();
        _officerChatWindow.Dispose();
        _ui.Dispose();
        _settings.Dispose();
    }

    private async Task RefreshRoles(IPluginLog log)
    {
        if (string.IsNullOrEmpty(_config.AuthToken))
        {
            return;
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
                return;
            }
            var stream = await response.Content.ReadAsStreamAsync();
            var dto = await JsonSerializer.DeserializeAsync<RolesDto>(stream) ?? new RolesDto();
            log.Info($"Roles received: {string.Join(", ", dto.Roles)}");
            _ = PluginServices.Framework.RunOnTick(() =>
            {
                dto.Roles.RemoveAll(r => r == "chat");
                _config.Roles = dto.Roles;
                _mainWindow.HasOfficerRole = _config.Roles.Contains("officer");
                PluginServices.PluginInterface.SavePluginConfig(_config);
            });
        }
        catch (Exception ex)
        {
            log.Error(ex, "Error refreshing roles.");
        }
    }

    private class RolesDto
    {
        public List<string> Roles { get; set; } = new();
    }
}
