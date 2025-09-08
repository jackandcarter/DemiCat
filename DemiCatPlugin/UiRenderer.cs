using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Numerics;
using System.Threading.Tasks;
using System.IO;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Text.RegularExpressions;
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
    private DateTime _lastSync;
    private int _failureCount;
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly Dictionary<string, PresenceDto> _presences = new();
    private bool _presenceLoadAttempted;
    private static readonly Regex MentionRegex = new("<@!?([0-9]+)>", RegexOptions.Compiled);

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

        if (!TokenManager.Instance!.IsReady() || !_config.Enabled || !_config.Events)
        {
            return;
        }

        StartPolling();
        await RefreshChannels();
        await LoadPresences();
        await ConnectWebSocket();
    }

    public void StopNetworking()
    {
        StopPolling();
        if (_webSocket != null)
        {
            try
            {
                if (_webSocket.State == WebSocketState.Open)
                {
                    _webSocket.CloseAsync(WebSocketCloseStatus.NormalClosure, string.Empty, CancellationToken.None).GetAwaiter().GetResult();
                }
            }
            catch { }
            _webSocket.Dispose();
            _webSocket = null;
        }
    }

    private async Task PollLoop(CancellationToken token)
    {
        var baseInterval = TimeSpan.FromSeconds(_config.PollIntervalSeconds);
        var delay = baseInterval;
        try
        {
            while (!token.IsCancellationRequested)
            {
                await Task.Delay(delay, token);
                if (!TokenManager.Instance!.IsReady() || !_config.Enabled || !_config.Events)
                {
                    continue;
                }
                var success = await PollEmbeds();
                delay = success
                    ? baseInterval
                    : TimeSpan.FromSeconds(
                        Math.Min(baseInterval.TotalSeconds * Math.Pow(2, _failureCount), 300));
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

    private async Task LoadPresences()
    {
        if (!TokenManager.Instance!.IsReady()) return;
        if (_presenceLoadAttempted) return;
        _presenceLoadAttempted = true;
        if (!ApiHelpers.ValidateApiBaseUrl(_config)) return;
        try
        {
            var request = new HttpRequestMessage(HttpMethod.Get, $"{_config.ApiBaseUrl.TrimEnd('/')}/api/users");
            ApiHelpers.AddAuthHeader(request, TokenManager.Instance!);
            var response = await _httpClient.SendAsync(request);
            if (!response.IsSuccessStatusCode) return;
            var stream = await response.Content.ReadAsStreamAsync();
            var list = await JsonSerializer.DeserializeAsync<List<PresenceDto>>(stream, JsonOpts) ?? new List<PresenceDto>();
            _ = PluginServices.Instance!.Framework.RunOnTick(() =>
            {
                _presences.Clear();
                foreach (var p in list)
                {
                    _presences[p.Id] = p;
                }
                AnnotateEmbedsWithPresence();
            });
        }
        catch
        {
            // ignore
        }
    }

    private void AnnotateEmbedDtos()
    {
        if (_presences.Count == 0) return;
        foreach (var dto in _embedDtos)
        {
            if (dto.Fields == null) continue;
            foreach (var f in dto.Fields)
            {
                f.Value = MentionRegex.Replace(f.Value, match =>
                {
                    var id = match.Groups[1].Value;
                    if (_presences.TryGetValue(id, out var p))
                    {
                        return $"{p.Name} ({p.Status})";
                    }
                    return match.Value;
                });
            }
        }
    }

    private void AnnotateEmbedsWithPresence()
    {
        if (_presences.Count == 0) return;
        lock (_embedLock)
        {
            AnnotateEmbedDtos();
            foreach (var dto in _embedDtos)
            {
                if (_embeds.TryGetValue(dto.Id, out var view))
                {
                    view.Update(dto);
                }
            }
        }
    }

    private async Task<bool> PollEmbeds()
    {
        if (!ApiHelpers.ValidateApiBaseUrl(_config) || TokenManager.Instance?.IsReady() != true) return false;

        try
        {
            var url = $"{_config.ApiBaseUrl.TrimEnd('/')}/api/embeds";
            if (!string.IsNullOrEmpty(_channelId))
            {
                url += $"?channel_id={_channelId}";
            }
            var request = new HttpRequestMessage(HttpMethod.Get, url);
            ApiHelpers.AddAuthHeader(request, TokenManager.Instance!);
            var response = await _httpClient.SendAsync(request);
            if (!response.IsSuccessStatusCode)
            {
                _failureCount++;
                PluginServices.Instance?.Log.Warning(
                    $"Sync failed ({_failureCount}) status={response.StatusCode}");
                PluginServices.Instance?.ToastGui.ShowError($"Sync failed ({_failureCount})");
                return false;
            }
            var stream = await response.Content.ReadAsStreamAsync();
            var embeds = await JsonSerializer.DeserializeAsync<List<EmbedDto>>(stream, JsonOpts) ?? new List<EmbedDto>();
            _ = PluginServices.Instance!.Framework.RunOnTick(() =>
            {
                _embedDtos.Clear();
                _embedDtos.AddRange(embeds);
                AnnotateEmbedDtos();
                SetEmbeds(_embedDtos);
            });
            _failureCount = 0;
            _lastSync = DateTime.UtcNow;
            PluginServices.Instance?.Log.Info($"Sync at {_lastSync:O}");
            return true;
        }
        catch (Exception ex)
        {
            _failureCount++;
            PluginServices.Instance?.Log.Warning(ex, $"Sync exception ({_failureCount})");
            PluginServices.Instance?.ToastGui.ShowError($"Sync failed ({_failureCount})");
            return false;
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

            if (!ApiHelpers.ValidateApiBaseUrl(_config) || TokenManager.Instance?.IsReady() != true || !_config.Enabled)
            {
                return;
            }

            try
            {
                var pingResponse = await ApiHelpers.PingAsync(_httpClient, _config, TokenManager.Instance!, CancellationToken.None);
                if (pingResponse?.IsSuccessStatusCode != true)
                {
                    if (pingResponse?.StatusCode == HttpStatusCode.NotFound)
                    {
                        PluginServices.Instance!.Log.Error("Backend ping endpoints missing. Please update or restart the backend.");
                    }
                    StartPolling();
                    return;
                }
                _webSocket = new ClientWebSocket();
                ApiHelpers.AddAuthHeader(_webSocket, TokenManager.Instance!);
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
                try
                {
                    using var document = JsonDocument.Parse(json);
                    if (document.RootElement.TryGetProperty("deletedId", out var delProp))
                    {
                        var id = delProp.GetString();
                        if (!string.IsNullOrEmpty(id))
                        {
                            _ = PluginServices.Instance!.Framework.RunOnTick(() =>
                            {
                                var index = _embedDtos.FindIndex(e => e.Id == id);
                                if (index >= 0)
                                {
                                    _embedDtos.RemoveAt(index);
                                    AnnotateEmbedDtos();
                                    SetEmbeds(_embedDtos);
                                }
                            });
                        }
                    }
                    else
                    {
                        var embed = document.RootElement.Deserialize<EmbedDto>(JsonOpts);
                        if (embed != null)
                        {
                            if (string.IsNullOrEmpty(_channelId) ||
                                embed.ChannelId?.ToString() == _channelId)
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
                                    AnnotateEmbedDtos();
                                    SetEmbeds(_embedDtos);
                                });
                            }
                        }
                    }
                }
                catch
                {
                    // ignored
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
        if (!TokenManager.Instance!.IsReady()) return;
        if (!ApiHelpers.ValidateApiBaseUrl(_config)) return;

        try
        {
            var url = $"{_config.ApiBaseUrl.TrimEnd('/')}/api/embeds";
            if (!string.IsNullOrEmpty(_channelId))
            {
                url += $"?channel_id={_channelId}";
            }
            var request = new HttpRequestMessage(HttpMethod.Get, url);
            ApiHelpers.AddAuthHeader(request, TokenManager.Instance!);
            var response = await _httpClient.SendAsync(request);
            if (!response.IsSuccessStatusCode)
            {
                return;
            }
            var stream = await response.Content.ReadAsStreamAsync();
            var embeds = await JsonSerializer.DeserializeAsync<List<EmbedDto>>(stream, JsonOpts) ?? new List<EmbedDto>();
            _ = PluginServices.Instance!.Framework.RunOnTick(() =>
            {
                _embedDtos.Clear();
                _embedDtos.AddRange(embeds);
                AnnotateEmbedDtos();
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
        if (!TokenManager.Instance!.IsReady())
        {
            ImGui.TextUnformatted("Link DemiCat to view events");
            return;
        }

        if (!_config.Events)
        {
            ImGui.TextUnformatted("Feature disabled");
            return;
        }

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
                _ = RefreshChannels();
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

        ImGui.BeginChild("##eventScroll", ImGui.GetContentRegionAvail(), true);
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

    private async Task FetchChannels(bool refreshed = false)
    {
        if (!TokenManager.Instance!.IsReady()) return;
        if (!ApiHelpers.ValidateApiBaseUrl(_config))
        {
            PluginServices.Instance!.Log.Warning("Cannot fetch channels: API base URL is not configured.");
            _ = PluginServices.Instance!.Framework.RunOnTick(() =>
            {
                _channelFetchFailed = true;
                _channelErrorMessage = "Invalid API URL";
                _channelsLoaded = true;
            });
            return;
        }

        try
        {
            var request = new HttpRequestMessage(HttpMethod.Get, $"{_config.ApiBaseUrl.TrimEnd('/')}/api/channels");
            ApiHelpers.AddAuthHeader(request, TokenManager.Instance!);
            var response = await _httpClient.SendAsync(request);
            if (!response.IsSuccessStatusCode)
            {
                var responseBody = await response.Content.ReadAsStringAsync();
                PluginServices.Instance!.Log.Warning($"Failed to fetch channels. Status: {response.StatusCode}. Response Body: {responseBody}");
                _ = PluginServices.Instance!.Framework.RunOnTick(() =>
                {
                    _channelFetchFailed = true;
                    _channelErrorMessage = "Failed to load channels";
                    _channelsLoaded = true;
                });
                return;
            }
            var stream = await response.Content.ReadAsStreamAsync();
            var dto = await JsonSerializer.DeserializeAsync<ChannelListDto>(stream) ?? new ChannelListDto();
            if (await ChannelNameResolver.Resolve(dto.Event, _httpClient, _config, refreshed, () => FetchChannels(true))) return;
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
            _ = PluginServices.Instance!.Framework.RunOnTick(() =>
            {
                _channelFetchFailed = true;
                _channelErrorMessage = "Failed to load channels";
                _channelsLoaded = true;
            });
        }
    }

    private class ChannelListDto
    {
        [JsonPropertyName(ChannelKind.Event)] public List<ChannelDto> Event { get; set; } = new();
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

