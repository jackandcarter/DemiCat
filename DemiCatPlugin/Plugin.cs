using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Timers;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using DiscordHelper;
using Dalamud.Interface;
using Dalamud.Plugin;

namespace DemiCatPlugin;

public class Plugin : IDalamudPlugin
{
    public string Name => "DemiCat";

    private readonly UiRenderer _ui;
    private readonly SettingsWindow _settings;
    private readonly ChatWindow _chatWindow;
    private readonly MainWindow _mainWindow;
    private Config _config;
    private readonly System.Timers.Timer _timer;
    private readonly HttpClient _httpClient = new();
    private ClientWebSocket? _webSocket;
    private readonly List<EmbedDto> _embeds = new();

    private readonly IDalamudPluginInterface _pluginInterface;
    private readonly IUiBuilder _uiBuilder;
    public Plugin(IDalamudPluginInterface pluginInterface, IUiBuilder uiBuilder)
    {
        _pluginInterface = pluginInterface;
        _uiBuilder = uiBuilder;

        _config = _pluginInterface.GetPluginConfig() as Config ?? new Config();

        _ui = new UiRenderer(_config);
        _settings = new SettingsWindow(_config);
        _chatWindow = new ChatWindow(_config);
        _mainWindow = new MainWindow(_config, _ui, _chatWindow, _settings);

        _timer = new System.Timers.Timer(_config.PollIntervalSeconds * 1000);
        _timer.Elapsed += OnPollTimer;
        _timer.AutoReset = true;

        if (_config.Enabled)
        {
            _ = ConnectWebSocket();
        }

        _uiBuilder.Draw += _mainWindow.Draw;
        _uiBuilder.Draw += _settings.Draw;
        _uiBuilder.OpenMainUi += () => _mainWindow.IsOpen = true;
        _uiBuilder.OpenConfigUi += () => _settings.IsOpen = true;
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
            _embeds.Clear();
            _embeds.AddRange(embeds);
            _ui.SetEmbeds(_embeds);
        }
        catch
        {
            // ignored
        }

        if (_webSocket == null || _webSocket.State != WebSocketState.Open)
        {
            _ = ConnectWebSocket();
        }
    }

    private async Task ConnectWebSocket()
    {
        if (_webSocket != null && _webSocket.State == WebSocketState.Open)
        {
            return;
        }

        try
        {
            _webSocket?.Dispose();
            _webSocket = new ClientWebSocket();
            var wsUri = new Uri(_config.HelperBaseUrl.TrimEnd('/')
                .Replace("http://", "ws://")
                .Replace("https://", "wss://"));
            await _webSocket.ConnectAsync(wsUri, CancellationToken.None);
            _timer.Stop();
            await ReceiveLoop();
        }
        catch
        {
            if (!_timer.Enabled)
            {
                _timer.Start();
            }
        }
    }

    private async Task ReceiveLoop()
    {
        if (_webSocket == null)
        {
            return;
        }

        var buffer = new byte[8192];
        try
        {
            while (_webSocket.State == WebSocketState.Open)
            {
                var ms = new MemoryStream();
                WebSocketReceiveResult result;
                do
                {
                    result = await _webSocket.ReceiveAsync(buffer, CancellationToken.None);
                    if (result.MessageType == WebSocketMessageType.Close)
                    {
                        await _webSocket.CloseAsync(WebSocketCloseStatus.NormalClosure, string.Empty, CancellationToken.None);
                        return;
                    }
                    ms.Write(buffer, 0, result.Count);
                } while (!result.EndOfMessage);

                var json = Encoding.UTF8.GetString(ms.ToArray());
                var embed = JsonSerializer.Deserialize<EmbedDto>(json);
                if (embed != null)
                {
                    var index = _embeds.FindIndex(e => e.Id == embed.Id);
                    if (index >= 0)
                    {
                        _embeds[index] = embed;
                    }
                    else
                    {
                        _embeds.Add(embed);
                    }
                    _ui.SetEmbeds(_embeds);
                }
            }
        }
        catch
        {
            // ignored
        }
        finally
        {
            _webSocket?.Dispose();
            _webSocket = null;
            if (!_timer.Enabled)
            {
                _timer.Start();
            }
        }
    }

    public void Dispose()
    {
        _uiBuilder.Draw -= _mainWindow.Draw;
        _uiBuilder.Draw -= _settings.Draw;
        _timer.Stop();
        _timer.Dispose();
        if (_webSocket != null)
        {
            try
            {
                if (_webSocket.State == WebSocketState.Open)
                {
                    _webSocket.CloseAsync(WebSocketCloseStatus.NormalClosure, string.Empty, CancellationToken.None).Wait();
                }
            }
            catch
            {
                // ignored
            }
            _webSocket.Dispose();
        }
        _httpClient.Dispose();
        _ui.Dispose();
        _settings.Dispose();
        _chatWindow.Dispose();
    }
}

