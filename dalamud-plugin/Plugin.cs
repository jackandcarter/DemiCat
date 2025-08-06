using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Timers;
using DiscordHelper;

namespace DalamudPlugin;

public interface IDalamudPlugin : IDisposable
{
    string Name { get; }
}

public class Plugin : IDalamudPlugin
{
    public string Name => "SamplePlugin";

    private readonly UiRenderer _ui;
    private readonly SettingsWindow _settings;
    private readonly EventCreateWindow _createWindow;
    private readonly ChatWindow _chatWindow;
    private Config _config;
    private readonly System.Timers.Timer _timer;
    private readonly HttpClient _httpClient = new();

    public Plugin()
    {
        _config = LoadConfig();

        _ui = new UiRenderer(_config);
        _settings = new SettingsWindow(_config) { IsOpen = true };
        _createWindow = new EventCreateWindow(_config) { IsOpen = true };
        _chatWindow = new ChatWindow(_config) { IsOpen = true };

        _timer = new System.Timers.Timer(_config.PollIntervalSeconds * 1000);
        _timer.Elapsed += OnPollTimer;
        _timer.AutoReset = true;

        if (_config.Enabled)
        {
            _timer.Start();
        }

        Service.Interface.UiBuilder.Draw += _ui.DrawWindow;
        Service.Interface.UiBuilder.Draw += _settings.Draw;
        Service.Interface.UiBuilder.Draw += _createWindow.Draw;
        Service.Interface.UiBuilder.Draw += _chatWindow.Draw;
    }

    private Config LoadConfig()
    {
        try
        {
            var path = Path.Combine(AppContext.BaseDirectory, "config.json");
            if (File.Exists(path))
            {
                var json = File.ReadAllText(path);
                var cfg = JsonSerializer.Deserialize<Config>(json);
                if (cfg != null)
                {
                    return cfg;
                }
            }
        }
        catch
        {
            // ignored
        }

        return new Config();
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
        Service.Interface.UiBuilder.Draw -= _ui.DrawWindow;
        Service.Interface.UiBuilder.Draw -= _settings.Draw;
        Service.Interface.UiBuilder.Draw -= _createWindow.Draw;
        Service.Interface.UiBuilder.Draw -= _chatWindow.Draw;
        _timer.Stop();
        _timer.Dispose();
        _httpClient.Dispose();
        _ui.Dispose();
        _settings.Dispose();
        _createWindow.Dispose();
        _chatWindow.Dispose();
    }
}

