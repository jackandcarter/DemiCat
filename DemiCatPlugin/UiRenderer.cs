using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Numerics;
using System.Threading.Tasks;
using System.IO;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using DiscordHelper;
using Dalamud.Bindings.ImGui;

namespace DemiCatPlugin;

public class UiRenderer : IDisposable
{
    private readonly HttpClient _httpClient;
    private readonly Config _config;
    private readonly Dictionary<string, EventView> _embeds = new();
    private readonly List<EmbedDto> _embedDtos = new();
    private string _channelId;
    private EventView? _current;
    private ClientWebSocket? _webSocket;
    private CancellationTokenSource? _pollCts;

    public UiRenderer(Config config, HttpClient httpClient)
    {
        _config = config;
        _httpClient = httpClient;
        _channelId = config.EventChannelId;

        if (_config.Enabled)
        {
            StartPolling();
            _ = ConnectWebSocket();
        }
    }

    public string ChannelId
    {
        get => _channelId;
        set => _channelId = value;
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
            var request = new HttpRequestMessage(HttpMethod.Get, $"{_config.ApiBaseUrl.TrimEnd('/')}/api/embeds");
            if (!string.IsNullOrEmpty(_config.AuthToken))
            {
                request.Headers.Add("X-Api-Key", _config.AuthToken);
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
                _embedDtos.Clear();
                _embedDtos.AddRange(embeds);
                SetEmbeds(_embedDtos);
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
            if (!string.IsNullOrEmpty(_config.AuthToken))
            {
                _webSocket.Options.SetRequestHeader("X-Api-Key", _config.AuthToken);
            }
            var baseUrl = _config.ApiBaseUrl.TrimEnd('/');
            var wsUrl = new Uri(($"{baseUrl}/ws/embeds")
                .Replace("http://", "ws://")
                .Replace("https://", "wss://"));
            await _webSocket.ConnectAsync(wsUrl, CancellationToken.None);
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
                        var index = _embedDtos.FindIndex(e => e.Id == embed.Id);
                        if (index >= 0)
                        {
                            _embedDtos[index] = embed;
                        }
                        else
                        {
                            _embedDtos.Add(embed);
                        }
                        SetEmbeds(_embedDtos);
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
    }

    public void SetEmbeds(IEnumerable<EmbedDto> embeds)
    {
        var ids = new HashSet<string>();
        foreach (var dto in embeds)
        {
            ids.Add(dto.Id);
            if (_embeds.TryGetValue(dto.Id, out var view))
            {
                view.Update(dto);
            }
            else
            {
                _embeds[dto.Id] = new EventView(dto, _config, _httpClient, RefreshEmbeds);
            }
        }

        foreach (var key in _embeds.Keys.Where(k => !ids.Contains(k)).ToList())
        {
            _embeds[key].Dispose();
            _embeds.Remove(key);
        }
    }

    public async Task RefreshEmbeds()
    {
        try
        {
            var request = new HttpRequestMessage(HttpMethod.Get, $"{_config.ApiBaseUrl.TrimEnd('/')}/api/embeds");
            if (!string.IsNullOrEmpty(_config.AuthToken))
            {
                request.Headers.Add("X-Api-Key", _config.AuthToken);
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
                _embedDtos.Clear();
                _embedDtos.AddRange(embeds);
                SetEmbeds(_embedDtos);
            });
        }
        catch
        {
            // ignored
        }
    }

    public void Draw()
    {
        ImGui.BeginChild("##eventButtons", new Vector2(120, 0), true);
        _current?.DrawButtons();
        ImGui.EndChild();

        ImGui.SameLine();

        ImGui.BeginChild("##eventScroll", new Vector2(0, 0), true);

        var scrollY = ImGui.GetScrollY();
        _current = null;

        foreach (var view in _embeds.Values.Where(v => string.IsNullOrEmpty(_channelId) || v.ChannelId == _channelId))
        {
            var start = ImGui.GetCursorPosY();
            view.Draw();
            var end = ImGui.GetCursorPosY();
            if (_current == null && scrollY >= start && scrollY < end)
            {
                _current = view;
            }
        }

        ImGui.EndChild();
    }

    public void Dispose()
    {
        StopPolling();
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
            _webSocket = null;
        }

        foreach (var view in _embeds.Values)
        {
            view.Dispose();
        }
        _embeds.Clear();
    }
}

