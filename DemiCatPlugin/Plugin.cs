using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.IO;
using System.Threading;
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
    private CancellationTokenSource? _pollCts;
    private readonly HttpClient _httpClient = new();
    private ClientWebSocket? _webSocket;
    private readonly List<EmbedDto> _embeds = new();
    private readonly Action _openMainUi;
    private readonly Action _openConfigUi;

    public Plugin()
    {
        _config = PluginInterface.GetPluginConfig() as Config ?? new Config();

        _ui = new UiRenderer(_config);
        _settings = new SettingsWindow(_config, CheckOfficerRole);
        _chatWindow = _config.EnableFcChat ? new FcChatWindow(_config) : null;
        _officerChatWindow = new OfficerChatWindow(_config);
        _mainWindow = new MainWindow(_config, _ui, _chatWindow, _officerChatWindow, _settings);

        if (_config.Enabled)
        {
            _ = ConnectWebSocket();
            _ = CheckOfficerRole();
        }

        PluginInterface.UiBuilder.Draw += _mainWindow.Draw;
        PluginInterface.UiBuilder.Draw += _settings.Draw;
        _openMainUi = () => _mainWindow.IsOpen = true;
        PluginInterface.UiBuilder.OpenMainUi += _openMainUi;
        _openConfigUi = () => _settings.IsOpen = true;
        PluginInterface.UiBuilder.OpenConfigUi += _openConfigUi;

        Log.Info("DemiCat loaded.");
    }

    private void StartPolling()
    {
        if (_pollCts != null)
        {
            return;
        }

        _pollCts = new CancellationTokenSource();
        _ = PollLoop(_pollCts.Token);
    }

    private void StopPolling()
    {
        _pollCts?.Cancel();
        _pollCts = null;
    }

    private async Task PollLoop(CancellationToken token)
    {
        var interval = TimeSpan.FromSeconds(_config.PollIntervalSeconds);
        using var timer = new PeriodicTimer(interval);
        try
        {
            while (await timer.WaitForNextTickAsync(token))
            {
                await PollEmbeds();
                if (_webSocket == null || _webSocket.State != WebSocketState.Open)
                {
                    _ = ConnectWebSocket();
                }
            }
        }
        catch (OperationCanceledException)
        {
            // ignored
        }
    }

    private async Task PollEmbeds()
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
            _ = PluginServices.Framework.RunOnTick(() =>
            {
                _embeds.Clear();
                _embeds.AddRange(embeds);
                _ui.SetEmbeds(_embeds);
            });
        }
        catch
        {
            // ignored
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
            StopPolling();
            await ReceiveLoop();
        }
        catch
        {
            StartPolling();
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
                    _ = PluginServices.Framework.RunOnTick(() =>
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
                    });
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
    StartPolling();
}

public void Dispose()
{
    // Unsubscribe UI draw handlers
    PluginInterface.UiBuilder.Draw -= _mainWindow.Draw;
    PluginInterface.UiBuilder.Draw -= _settings.Draw;

    // Unsubscribe UI open handlers (from codex/add-fields-for-ui-delegate-subscriptions)
    PluginInterface.UiBuilder.OpenMainUi -= _openMainUi;
    PluginInterface.UiBuilder.OpenConfigUi -= _openConfigUi;

    // Stop background work (from main)
    StopPolling();

    // Close and dispose websocket safely
    if (_webSocket != null)
    {
        try
        {
            if (_webSocket.State == WebSocketState.Open)
            {
                _webSocket.CloseAsync(
                    WebSocketCloseStatus.NormalClosure,
                    string.Empty,
                    CancellationToken.None
                ).Wait();
            }
        }
        catch
        {
            // ignored
        }
        _webSocket.Dispose();
        _webSocket = null;
    }

    // Dispose remaining resources
    _httpClient.Dispose();
    _chatWindow?.Dispose();
    _officerChatWindow.Dispose();
    _ui.Dispose();
    _settings.Dispose();
}


    private async Task CheckOfficerRole()
    {
        if (string.IsNullOrEmpty(_config.AuthToken))
        {
            return;
        }

        try
        {
            var meRequest = new HttpRequestMessage(HttpMethod.Get, $"{_config.HelperBaseUrl.TrimEnd('/')}/api/me");
            meRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _config.AuthToken);
            var meResponse = await _httpClient.SendAsync(meRequest);
            if (!meResponse.IsSuccessStatusCode)
            {
                return;
            }
            var meStream = await meResponse.Content.ReadAsStreamAsync();
            var me = await JsonSerializer.DeserializeAsync<MeDto>(meStream);
            var userId = me?.UserId;
            if (string.IsNullOrEmpty(userId))
            {
                return;
            }

            var rolesRequest = new HttpRequestMessage(HttpMethod.Get, $"{_config.HelperBaseUrl.TrimEnd('/')}/api/me/roles?userId={userId}");
            rolesRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _config.AuthToken);
            var rolesResponse = await _httpClient.SendAsync(rolesRequest);
            if (!rolesResponse.IsSuccessStatusCode)
            {
                return;
            }
            var rolesStream = await rolesResponse.Content.ReadAsStreamAsync();
            var roles = await JsonSerializer.DeserializeAsync<RolesDto>(rolesStream) ?? new RolesDto();
            _ = PluginServices.Framework.RunOnTick(() =>
            {
                _mainWindow.HasOfficerRole = roles.HasOfficerRole;
                _mainWindow.HasChatRole = roles.HasChatRole;
            });
        }
        catch
        {
            // ignored
        }
    }

    private class MeDto
    {
        public string? UserId { get; set; }
    }

    private class RolesDto
    {
        public bool HasOfficerRole { get; set; }
        public bool HasChatRole { get; set; }
    }
}

