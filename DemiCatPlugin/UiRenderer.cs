using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Numerics;
using System.Threading.Tasks;
using System.IO;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using DiscordHelper;
using Dalamud.Bindings.ImGui;

namespace DemiCatPlugin;

public class UiRenderer : IAsyncDisposable, IDisposable
{
    private readonly HttpClient _httpClient;
    private readonly Config _config;
    private readonly Dictionary<string, EventView> _embeds = new();
    private readonly object _embedLock = new();
    private readonly List<EmbedDto> _embedDtos = new();
    private string _channelId;
    private ClientWebSocket? _webSocket;
    private CancellationTokenSource? _pollCts;
    private readonly List<ChannelDto> _channels = new();
    private bool _channelsLoaded;
    private bool _channelFetchFailed;
    private string _channelErrorMessage = string.Empty;
    private int _selectedIndex;
    private readonly SemaphoreSlim _connectGate = new(1, 1);

    public UiRenderer(Config config, HttpClient httpClient)
    {
        _config = config;
        _httpClient = httpClient;
        _channelId = config.EventChannelId;
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

    public async Task StartNetworking()
    {
        StopPolling();

        if (_webSocket != null)
        {
            try
            {
                if (_webSocket.State == WebSocketState.Open)
                {
                    await _webSocket.CloseAsync(WebSocketCloseStatus.NormalClosure, string.Empty, CancellationToken.None);
                }
            }
            catch
            {
                // ignored
            }
            _webSocket.Dispose();
            _webSocket = null;
        }

        if (string.IsNullOrEmpty(_config.AuthToken) || !_config.Enabled)
        {
            return;
        }

        StartPolling();
        await ConnectWebSocket();
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
        if (!ApiHelpers.ValidateApiBaseUrl(_config)) return;

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
            _ = PluginServices.Instance!.Framework.RunOnTick(() =>
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
        await _connectGate.WaitAsync();
        try
        {
            if (_webSocket != null && _webSocket.State == WebSocketState.Open)
            {
                return;
            }

            if (!ApiHelpers.ValidateApiBaseUrl(_config))
            {
                return;
            }

            try
            {
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
            catch (WebSocketException ex)
            {
                var status = ex.Data.Contains("StatusCode") ? ex.Data["StatusCode"] : ex.WebSocketErrorCode;
                PluginServices.Instance!.Log.Error(ex, $"Failed to connect WebSocket. Status: {status}");
                _webSocket?.Dispose();
                _webSocket = null;
                StartPolling();
            }
            catch (Exception ex)
            {
                PluginServices.Instance!.Log.Error(ex, "Failed to connect WebSocket");
                _webSocket?.Dispose();
                _webSocket = null;
                StartPolling();
            }
        }
        finally
        {
            _connectGate.Release();
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
                    _ = PluginServices.Instance!.Framework.RunOnTick(() =>
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
        lock (_embedLock)
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
    }

    public async Task RefreshEmbeds()
    {
        if (!ApiHelpers.ValidateApiBaseUrl(_config)) return;

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
            _ = PluginServices.Instance!.Framework.RunOnTick(() =>
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
        if (!_channelsLoaded)
        {
            _ = FetchChannels();
        }
        if (_channels.Count > 0)
        {
            var channelNames = _channels.Select(c => c.Name).ToArray();
            if (ImGui.Combo("Channel", ref _selectedIndex, channelNames, channelNames.Length))
            {
                _channelId = _channels[_selectedIndex].Id;
                _config.EventChannelId = _channelId;
                SaveConfig();
                _ = RefreshEmbeds();
            }
        }
        else
        {
            ImGui.TextUnformatted(_channelFetchFailed ? _channelErrorMessage : "No channels available");
        }

        List<EventView> embeds;
        lock (_embedLock)
        {
            embeds = _embeds.Values
                .Where(v => string.IsNullOrEmpty(_channelId) || v.ChannelId == _channelId)
                .ToList();
        }

        ImGui.BeginChild("##eventScroll", new Vector2(0, 0), true);
        foreach (var view in embeds)
        {
            view.Draw();
        }

        ImGui.EndChild();
    }

    public void ResetChannels()
    {
        _channelsLoaded = false;
        _channels.Clear();
    }

    public Task RefreshChannels()
    {
        ResetChannels();
        return FetchChannels();
    }

    private void SaveConfig()
    {
        PluginServices.Instance!.PluginInterface.SavePluginConfig(_config);
    }

    private async Task FetchChannels()
    {
        if (!ApiHelpers.ValidateApiBaseUrl(_config))
        {
            PluginServices.Instance!.Log.Warning("Cannot fetch channels: API base URL is not configured.");
            _channelFetchFailed = true;
            _channelErrorMessage = "Invalid API URL";
            _channelsLoaded = true;
            return;
        }

        try
        {
            var request = new HttpRequestMessage(HttpMethod.Get, $"{_config.ApiBaseUrl.TrimEnd('/')}/api/channels");
            if (!string.IsNullOrEmpty(_config.AuthToken))
            {
                request.Headers.Add("X-Api-Key", _config.AuthToken);
            }
            var response = await _httpClient.SendAsync(request);
            if (!response.IsSuccessStatusCode)
            {
                var responseBody = await response.Content.ReadAsStringAsync();
                PluginServices.Instance!.Log.Warning($"Failed to fetch channels. Status: {response.StatusCode}. Response Body: {responseBody}");
                _channelFetchFailed = true;
                _channelErrorMessage = "Failed to load channels";
                _channelsLoaded = true;
                return;
            }
            var stream = await response.Content.ReadAsStreamAsync();
            var dto = await JsonSerializer.DeserializeAsync<ChannelListDto>(stream) ?? new ChannelListDto();
            ResolveChannelNames(dto.Event);
            dto.Event.RemoveAll(c => string.IsNullOrWhiteSpace(c.Name));
            _ = PluginServices.Instance!.Framework.RunOnTick(() =>
            {
                _channels.Clear();
                _channels.AddRange(dto.Event);
                if (!string.IsNullOrEmpty(_channelId))
                {
                    _selectedIndex = _channels.FindIndex(c => c.Id == _channelId);
                    if (_selectedIndex < 0) _selectedIndex = 0;
                }
                if (_channels.Count > 0)
                {
                    _channelId = _channels[_selectedIndex].Id;
                }
                _channelsLoaded = true;
                _channelFetchFailed = false;
                _channelErrorMessage = string.Empty;
            });
        }
        catch (Exception ex)
        {
            PluginServices.Instance!.Log.Error(ex, "Error fetching channels");
            _channelFetchFailed = true;
            _channelErrorMessage = "Failed to load channels";
            _channelsLoaded = true;
        }
    }

    private static void ResolveChannelNames(List<ChannelDto> channels)
    {
        foreach (var c in channels)
        {
            if (string.IsNullOrWhiteSpace(c.Name))
            {
                PluginServices.Instance!.Log.Warning($"Channel name missing for {c.Id}.");
            }
        }
    }

    private class ChannelListDto
    {
        [JsonPropertyName("event")] public List<ChannelDto> Event { get; set; } = new();
    }

    public async ValueTask DisposeAsync()
    {
        StopPolling();
        if (_webSocket != null)
        {
            try
            {
                if (_webSocket.State == WebSocketState.Open)
                {
                    await _webSocket.CloseAsync(WebSocketCloseStatus.NormalClosure, string.Empty, CancellationToken.None);
                }
            }
            catch
            {
                // ignored
            }
            _webSocket.Dispose();
            _webSocket = null;
        }

        List<EventView> views;
        lock (_embedLock)
        {
            views = _embeds.Values.ToList();
            _embeds.Clear();
        }

        foreach (var view in views)
        {
            view.Dispose();
        }
    }

    public void Dispose()
    {
        DisposeAsync().GetAwaiter().GetResult();
    }
}

