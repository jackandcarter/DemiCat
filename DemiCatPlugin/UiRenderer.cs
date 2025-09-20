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
using DemiCatPlugin.Emoji;

namespace DemiCatPlugin;

public class UiRenderer : IAsyncDisposable, IDisposable
{
    private readonly HttpClient _httpClient;
    private readonly Config _config;
    private readonly Dictionary<string, EventView> _embeds = new();
    private readonly object _embedLock = new();
    private readonly List<EmbedDto> _embedDtos = new();
    private readonly ChannelSelectionService _channelSelection;
    private readonly EmojiManager _emojiManager;
    private ClientWebSocket? _webSocket;
    private CancellationTokenSource? _pollCts;
    private readonly List<ChannelDto> _channels = new();
    private bool _channelsLoaded;
    private bool _channelFetchFailed;
    private string _channelErrorMessage = string.Empty;
    private int _selectedIndex;
    private bool _channelWarningShown;
    private bool _channelSelectionWarningShown;
    private bool _embedWarningShown;
    private readonly SemaphoreSlim _connectGate = new(1, 1);
    private DateTime _lastSync;
    private int _failureCount;
    private bool _networkingActive;
    private int _webSocketReconnectAttempt;
    private string? _lastWebSocketErrorSignature;
    private DateTime _lastWebSocketErrorLog;
    private DateTime _nextWebSocketAttempt = DateTime.MinValue;
    private static readonly TimeSpan WebSocketErrorLogThrottle = TimeSpan.FromSeconds(30);
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly Dictionary<string, PresenceDto> _presences = new();
    private bool _presenceLoadAttempted;
    private static readonly Regex MentionRegex = new("<@!?([0-9]+)>", RegexOptions.Compiled);
    private const string DefaultWebSocketPath = "/ws/embeds";

    public UiRenderer(Config config, HttpClient httpClient, ChannelSelectionService channelSelection, EmojiManager emojiManager)
    {
        _config = config;
        _httpClient = httpClient;
        _channelSelection = channelSelection;
        _emojiManager = emojiManager;
        _channelSelection.ChannelChanged += HandleChannelChanged;
    }

    private string ChannelId => _channelSelection.GetChannel(ChannelKind.Event, _config.GuildId);

    private void HandleChannelChanged(string kind, string guildId, string oldId, string newId)
    {
        if (kind != ChannelKind.Event) return;
        if (!string.Equals(ChannelKeyHelper.NormalizeGuildId(guildId), ChannelKeyHelper.NormalizeGuildId(_config.GuildId), StringComparison.Ordinal))
            return;
        PluginServices.Instance!.Framework.RunOnTick(() =>
        {
            if (_channels.Count > 0)
            {
                _selectedIndex = _channels.FindIndex(c => c.Id == newId);
            }
        });
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

        _networkingActive = true;
        _webSocketReconnectAttempt = 0;
        _nextWebSocketAttempt = DateTime.UtcNow;
        StartPolling();
        await RefreshChannels();
        await LoadPresences();
        StartWebSocketConnectTask();
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
        _webSocketReconnectAttempt = 0;
        _nextWebSocketAttempt = DateTime.MaxValue;
        _networkingActive = false;
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
                if ((_webSocket == null || _webSocket.State != WebSocketState.Open) && DateTime.UtcNow >= _nextWebSocketAttempt)
                {
                    StartWebSocketConnectTask();
                }
            }
        }
        catch (OperationCanceledException)
        {
            // ignored
        }
    }

    private void StartWebSocketConnectTask()
    {
        async Task RunAsync()
        {
            try
            {
                await ConnectWebSocket();
            }
            catch (Exception ex)
            {
                LogWebSocketException(ex, "connect.task");
            }
        }

        _ = RunAsync();
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
        if (_embedDtos.Count == 0) return;
        foreach (var dto in _embedDtos)
        {
            if (dto?.Fields == null) continue;
            foreach (var f in dto.Fields)
            {
                if (f == null || string.IsNullOrEmpty(f.Value)) continue;
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
        if (_embedDtos.Count == 0) return;
        lock (_embedLock)
        {
            AnnotateEmbedDtos();
            foreach (var dto in _embedDtos)
            {
                if (dto == null || string.IsNullOrEmpty(dto.Id))
                {
                    continue;
                }

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
            var channelId = ChannelId;
            if (!string.IsNullOrEmpty(channelId))
            {
                url += $"?channel_id={channelId}";
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
        if (DateTime.UtcNow < _nextWebSocketAttempt)
        {
            return;
        }

        if (!_networkingActive)
        {
            return;
        }

        await _connectGate.WaitAsync();
        try
        {
            if (_webSocket != null && _webSocket.State == WebSocketState.Open)
            {
                return;
            }

            if (!ApiHelpers.ValidateApiBaseUrl(_config) || TokenManager.Instance?.IsReady() != true || !_config.Enabled)
            {
                ScheduleNextWebSocketAttempt();
                return;
            }

            try
            {
                var pingService = PingService.Instance ?? new PingService(_httpClient, _config, TokenManager.Instance!);
                var pingResponse = await pingService.PingAsync(CancellationToken.None);
                if (pingResponse?.IsSuccessStatusCode != true)
                {
                    if (pingResponse?.StatusCode == HttpStatusCode.NotFound)
                    {
                        PluginServices.Instance!.Log.Error("Backend ping endpoints missing. Please update or restart the backend.");
                    }
                    ScheduleNextWebSocketAttempt();
                    return;
                }

                _webSocket?.Dispose();
                _webSocket = new ClientWebSocket();
                ApiHelpers.AddAuthHeader(_webSocket, TokenManager.Instance!);
                var wsUrl = BuildWebSocketUri();
                if (wsUrl == null)
                {
                    LogWebSocketException(new InvalidOperationException("Missing WebSocket URL"), "uri");
                    ScheduleNextWebSocketAttempt();
                    return;
                }

                PluginServices.Instance!.Log.Information("Connecting WebSocket to {Uri}", wsUrl);
                await _webSocket.ConnectAsync(wsUrl, CancellationToken.None);
                PluginServices.Instance!.Log.Information("WebSocket connected successfully");

                ResetWebSocketBackoff();
                StopPolling();
                await ReceiveLoop();
                ScheduleNextWebSocketAttempt();
            }
            catch (HttpRequestException ex)
            {
                LogWebSocketException(ex, "connect");
                ScheduleNextWebSocketAttempt();
            }
            catch (WebSocketException ex)
            {
                LogWebSocketException(ex, "connect");
                ScheduleNextWebSocketAttempt();
            }
            catch (IOException ex)
            {
                LogWebSocketException(ex, "connect");
                ScheduleNextWebSocketAttempt();
            }
            catch (Exception ex)
            {
                LogWebSocketException(ex, "connect");
                ScheduleNextWebSocketAttempt();
            }
            finally
            {
                if (_webSocket?.State != WebSocketState.Open)
                {
                    _webSocket?.Dispose();
                    _webSocket = null;
                }
            }
        }
        finally
        {
            _connectGate.Release();
        }
    }

    internal Uri? BuildWebSocketUri()
    {
        var baseUrl = (_config.ApiBaseUrl ?? string.Empty).TrimEnd('/');
        if (string.IsNullOrWhiteSpace(baseUrl))
        {
            return null;
        }

        var rawPath = string.IsNullOrWhiteSpace(_config.WebSocketPath)
            ? DefaultWebSocketPath
            : _config.WebSocketPath.Trim();
        var normalizedPath = "/" + rawPath.TrimStart('/');
        var urlString = ($"{baseUrl}{normalizedPath}")
            .Replace("http://", "ws://", StringComparison.OrdinalIgnoreCase)
            .Replace("https://", "wss://", StringComparison.OrdinalIgnoreCase);

        if (!Uri.TryCreate(urlString, UriKind.Absolute, out var wsUrl))
        {
            return null;
        }

        return IsValidWebSocketUri(wsUrl) ? wsUrl : null;
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
                using var ms = new MemoryStream();
                WebSocketReceiveResult result;
                do
                {
                    result = await _webSocket.ReceiveAsync(buffer, CancellationToken.None);
                    if (result.MessageType == WebSocketMessageType.Close)
                    {
                        await CloseWebSocketGracefully(_webSocket, CancellationToken.None);
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
                            var channelId = ChannelId;
                            if (string.IsNullOrEmpty(channelId) ||
                                embed.ChannelId?.ToString() == channelId)
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
        catch (OperationCanceledException ex) when (!ShouldRethrow(ex, CancellationToken.None))
        {
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (ObjectDisposedException)
        {
        }
        catch (WebSocketException ex)
        {
            LogWebSocketException(ex, "receive");
        }
        catch (IOException ex)
        {
            LogWebSocketException(ex, "receive");
        }
    }

    internal static async Task CloseWebSocketGracefully(WebSocket socket, CancellationToken token)
    {
        if (socket == null)
        {
            return;
        }

        var state = socket.State;
        if (state != WebSocketState.Open && state != WebSocketState.CloseReceived)
        {
            return;
        }

        try
        {
            await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, string.Empty, token);
        }
        catch (OperationCanceledException ex) when (!ShouldRethrow(ex, token))
        {
        }
        catch (WebSocketException)
        {
        }
        catch (ObjectDisposedException)
        {
        }
        catch (InvalidOperationException)
        {
        }
    }

    private void ScheduleNextWebSocketAttempt()
    {
        if (!_networkingActive)
        {
            _nextWebSocketAttempt = DateTime.MaxValue;
            return;
        }
        _webSocketReconnectAttempt++;
        var delay = GetWebSocketRetryDelay(_webSocketReconnectAttempt);
        _nextWebSocketAttempt = DateTime.UtcNow + delay;
        StartPolling();
    }

    private void ResetWebSocketBackoff()
    {
        _webSocketReconnectAttempt = 0;
        _nextWebSocketAttempt = DateTime.UtcNow;
    }

    private static TimeSpan GetWebSocketRetryDelay(int attempt)
    {
        var cappedAttempt = Math.Max(1, attempt);
        var baseDelay = Math.Min(15d, Math.Pow(2, cappedAttempt - 1));
        var min = Math.Max(0.5d, baseDelay / 2d);
        var max = Math.Max(min, baseDelay);
        var jitter = min + (max - min) * Random.Shared.NextDouble();
        return TimeSpan.FromSeconds(jitter);
    }

    private static bool ShouldRethrow(OperationCanceledException _, CancellationToken token)
        => token.CanBeCanceled && token.IsCancellationRequested;

    private static bool IsValidWebSocketUri(Uri? uri)
    {
        if (uri == null || !uri.IsAbsoluteUri)
            return false;

        if (string.IsNullOrWhiteSpace(uri.ToString()))
            return false;

        return string.Equals(uri.Scheme, "ws", StringComparison.OrdinalIgnoreCase)
            || string.Equals(uri.Scheme, "wss", StringComparison.OrdinalIgnoreCase);
    }

    private void LogWebSocketException(Exception ex, string stage)
    {
        var now = DateTime.UtcNow;
        var signature = $"{stage}:{ex.GetType().FullName}:{ex.Message}";
        if (_lastWebSocketErrorSignature == signature && (now - _lastWebSocketErrorLog) < WebSocketErrorLogThrottle)
        {
            return;
        }

        _lastWebSocketErrorSignature = signature;
        _lastWebSocketErrorLog = now;
        PluginServices.Instance?.Log.Error(ex, $"embeds.ws {stage} failed");
    }

    public void SetEmbeds(IEnumerable<EmbedDto>? embeds)
    {
        if (embeds == null)
        {
            return;
        }

        lock (_embedLock)
        {
            var ids = new HashSet<string>();
            foreach (var dto in embeds)
            {
                if (dto == null || string.IsNullOrEmpty(dto.Id))
                {
                    continue;
                }

                ids.Add(dto.Id);
                if (_embeds.TryGetValue(dto.Id, out var view))
                {
                    view.Update(dto, BuildMentionContent(dto));
                }
                else
                {
                    _embeds[dto.Id] = new EventView(dto, _config, _httpClient, RefreshEmbeds, _emojiManager, BuildMentionContent(dto));
                }
            }

            foreach (var key in _embeds.Keys.Where(k => !ids.Contains(k)).ToList())
            {
                _embeds[key].Dispose();
                _embeds.Remove(key);
            }
        }
    }

    private static string? BuildMentionContent(EmbedDto dto)
    {
        if (dto.Mentions == null || dto.Mentions.Count == 0)
        {
            return null;
        }

        return string.Join(" ", dto.Mentions.Select(id => $"<@&{id}>"));
    }

    public async Task RefreshEmbeds()
    {
        if (!TokenManager.Instance!.IsReady()) return;
        if (!ApiHelpers.ValidateApiBaseUrl(_config)) return;

        try
        {
            var url = $"{_config.ApiBaseUrl.TrimEnd('/')}/api/embeds";
            var channelId = ChannelId;
            if (!string.IsNullOrEmpty(channelId))
            {
                url += $"?channel_id={channelId}";
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
            ImGui.TextUnformatted("Loading channels...");
            return;
        }

        if (_channels.Count == 0)
        {
            ShowWarningOrToast(_channelFetchFailed ? _channelErrorMessage : "No channels available", ref _channelWarningShown);
            return;
        }

        var channelNames = _channels
            .Select(c => c?.Name ?? c?.Id ?? string.Empty)
            .ToArray();

        if (channelNames.Length == 0)
        {
            ShowWarningOrToast(_channelFetchFailed ? _channelErrorMessage : "No channels available", ref _channelWarningShown);
            return;
        }

        _channelWarningShown = false;

        var comboIndex = _selectedIndex;
        if (comboIndex < 0 || comboIndex >= channelNames.Length)
        {
            comboIndex = Math.Clamp(comboIndex, 0, channelNames.Length - 1);
        }

        if (ImGui.Combo("Channel", ref comboIndex, channelNames, channelNames.Length))
        {
            if (comboIndex >= 0 && comboIndex < _channels.Count)
            {
                var selectedChannel = _channels[comboIndex];
                if (!string.IsNullOrEmpty(selectedChannel?.Id))
                {
                    _selectedIndex = comboIndex;
                    _channelSelection.SetChannel(ChannelKind.Event, _config.GuildId, selectedChannel.Id);
                    _ = RefreshChannels();
                    _ = RefreshEmbeds();
                }
                else
                {
                    ShowWarningOrToast("Select a valid channel to view events.", ref _channelSelectionWarningShown);
                    return;
                }
            }
            else
            {
                ShowWarningOrToast("Select a valid channel to view events.", ref _channelSelectionWarningShown);
                return;
            }
        }
        else
        {
            _selectedIndex = comboIndex;
        }

        if (_selectedIndex < 0 || _selectedIndex >= _channels.Count)
        {
            ShowWarningOrToast("Select a channel to view events.", ref _channelSelectionWarningShown);
            return;
        }

        var selected = _channels[_selectedIndex];
        if (selected == null || string.IsNullOrEmpty(selected.Id))
        {
            ShowWarningOrToast("Select a channel to view events.", ref _channelSelectionWarningShown);
            return;
        }

        var channelId = ChannelId;
        var currentGuildId = ChannelKeyHelper.NormalizeGuildId(_config.GuildId);
        if (string.IsNullOrWhiteSpace(channelId))
        {
            ShowWarningOrToast("Select a channel to view events.", ref _channelSelectionWarningShown);
            return;
        }

        _channelSelectionWarningShown = false;

        List<EventView> embeds;
        lock (_embedLock)
        {
            embeds = _embeds.Values
                .Where(v => v != null
                    && v.ChannelId == channelId
                    && string.Equals(ChannelKeyHelper.NormalizeGuildId(v.GuildId), currentGuildId, StringComparison.Ordinal))
                .ToList();
        }

        if (_embedDtos.Count == 0 || embeds.Count == 0)
        {
            ShowWarningOrToast("No events to display.", ref _embedWarningShown);
            return;
        }

        _embedWarningShown = false;

        ImGui.BeginChild("##eventScroll", ImGui.GetContentRegionAvail(), true);
        foreach (var view in embeds)
        {
            view?.Draw();
        }

        ImGui.EndChild();
    }

    private void ShowWarningOrToast(string message, ref bool toastShown)
    {
        var text = string.IsNullOrWhiteSpace(message) ? "No data available" : message;
        ImGui.TextUnformatted(text);
        if (!toastShown)
        {
            PluginServices.Instance?.ToastGui.ShowError(text);
            toastShown = true;
        }
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
            if (response.StatusCode == HttpStatusCode.Unauthorized || response.StatusCode == HttpStatusCode.Forbidden)
            {
                var responseBody = await response.Content.ReadAsStringAsync();
                PluginServices.Instance!.Log.Warning($"Failed to fetch channels. Status: {response.StatusCode}. Response Body: {responseBody}");
                _ = PluginServices.Instance!.Framework.RunOnTick(() =>
                {
                    _channelFetchFailed = true;
                    _channelErrorMessage = response.StatusCode == HttpStatusCode.Unauthorized
                        ? "Authentication failed"
                        : "Forbidden \u2013 check API key/roles";
                    _channelsLoaded = true;
                });
                return;
            }
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
            var eventChannels = (dto.Event ?? new List<ChannelDto>())
                .Where(c => c != null)
                .ToList();
            foreach (var channel in eventChannels)
            {
                channel.EnsureKind(ChannelKind.Event);
            }
            if (await ChannelNameResolver.Resolve(eventChannels, _httpClient, _config, refreshed, () => FetchChannels(true))) return;
            _ = PluginServices.Instance!.Framework.RunOnTick(() =>
            {
                _channels.Clear();
                _channels.AddRange(eventChannels);

                var guildId = _config.GuildId;
                var selectedChannelId = _channelSelection.GetChannel(ChannelKind.Event, guildId, out var hasStoredSelection);
                var usedFallback = !hasStoredSelection && !string.IsNullOrEmpty(selectedChannelId);
                var targetIndex = -1;
                if (!string.IsNullOrEmpty(selectedChannelId))
                {
                    targetIndex = _channels.FindIndex(c => c != null && c.Id == selectedChannelId);
                }

                string? ensureChannelId = null;
                if (targetIndex >= 0)
                {
                    _selectedIndex = targetIndex;
                    if (usedFallback)
                    {
                        ensureChannelId = selectedChannelId;
                    }
                }
                else if (_channels.Count > 0)
                {
                    _selectedIndex = 0;
                    ensureChannelId = _channels[_selectedIndex]?.Id;
                }
                else
                {
                    _selectedIndex = -1;
                }

                if (_channels.Count > 0 && (_selectedIndex < 0 || _selectedIndex >= _channels.Count))
                {
                    _selectedIndex = Math.Clamp(_selectedIndex, 0, _channels.Count - 1);
                    ensureChannelId ??= _channels[_selectedIndex]?.Id;
                }

                if (!string.IsNullOrEmpty(ensureChannelId))
                {
                    _channelSelection.SetChannel(ChannelKind.Event, guildId, ensureChannelId);
                }
                _channelsLoaded = true;
                _channelFetchFailed = false;
                _channelErrorMessage = string.Empty;
            });
        }
        catch (HttpRequestException ex)
        {
            PluginServices.Instance!.Log.Warning($"Failed to fetch channels. Status: {ex.StatusCode}");
            _ = PluginServices.Instance!.Framework.RunOnTick(() =>
            {
                _channelFetchFailed = true;
                _channelErrorMessage = ex.StatusCode == HttpStatusCode.Unauthorized
                    ? "Authentication failed"
                    : ex.StatusCode == HttpStatusCode.Forbidden
                        ? "Forbidden \u2013 check API key/roles"
                        : "Failed to load channels";
                _channelsLoaded = true;
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
        _channelSelection.ChannelChanged -= HandleChannelChanged;
        DisposeAsync().GetAwaiter().GetResult();
    }
}

