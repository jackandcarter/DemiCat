using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Timers;
using DiscordHelper;
using Dalamud.Plugin;

namespace DalamudPlugin;

public class Plugin : IDalamudPlugin
{
    public string Name => "SamplePlugin";

    private readonly UiRenderer _ui;
    private readonly SettingsWindow _settings;
    private readonly EventCreateWindow _createWindow;
    private readonly ChatWindow _chatWindow;
    private readonly MainWindow _mainWindow;
    private Config _config;
    private readonly System.Timers.Timer _timer;
    private readonly HttpClient _httpClient = new();

    private readonly DalamudPluginInterface _pluginInterface;

    public Plugin(DalamudPluginInterface pluginInterface)
    {
        _pluginInterface = pluginInterface;
        _pluginInterface.Create<PluginServices>();

        _config = _pluginInterface.GetPluginConfig() as Config ?? new Config();

        _ui = new UiRenderer(_config);
        _settings = new SettingsWindow(_config);
        _createWindow = new EventCreateWindow(_config) { IsOpen = true };
        _chatWindow = new ChatWindow(_config);
        _mainWindow = new MainWindow(_config, _ui, _chatWindow, _settings) { IsOpen = true };

        _timer = new System.Timers.Timer(_config.PollIntervalSeconds * 1000);
        _timer.Elapsed += OnPollTimer;
        _timer.AutoReset = true;

        if (_config.Enabled)
        {
            _timer.Start();
        }

        _pluginInterface.UiBuilder.Draw += _mainWindow.Draw;
        _pluginInterface.UiBuilder.Draw += _settings.Draw;
        _pluginInterface.UiBuilder.Draw += _createWindow.Draw;
    }

    private async void OnPollTimer(object? sender, ElapsedEventArgs e)
    {
        try
        {
            var request = new HttpRequestMessage(HttpMethod.Get, $"{_config.HelperBaseUrl.TrimEnd('/')}/embeds");
            if (!string.IsNullOrEmpty(_config.AuthToken))
            {
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _config.AuthToken);
            }

            var response = await _httpClient.SendAsync(request);
            if (!response.IsSuccessStatusCode)
            {
                return;
            }

            var stream = await response.Content.ReadAsStreamAsync();
            var embeds = await JsonSerializer.DeserializeAsync<List<EmbedDto>>(stream) ?? new List<EmbedDto>();
            _ui.SetEmbeds(embeds);
        }
        catch
        {
            // ignored
        }
    }

    public void Dispose()
    {
        _pluginInterface.UiBuilder.Draw -= _mainWindow.Draw;
        _pluginInterface.UiBuilder.Draw -= _settings.Draw;
        _pluginInterface.UiBuilder.Draw -= _createWindow.Draw;
        _timer.Stop();
        _timer.Dispose();
        _httpClient.Dispose();
        _ui.Dispose();
        _settings.Dispose();
        _createWindow.Dispose();
        _chatWindow.Dispose();
    }
}

