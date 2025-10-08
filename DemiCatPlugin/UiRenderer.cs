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
using ImGuiNET;
using DemiCatPlugin.Emoji;

namespace DemiCatPlugin;

public class UiRenderer : IAsyncDisposable, IDisposable
{
    private readonly HttpClient _httpClient;
    private readonly TokenManager _tokenManager;
    private readonly Config _config;
    private readonly Dictionary<string, EventView> _embeds = new();
    private readonly object _embedLock = new();
    private readonly List<EmbedDto> _embedDtos = new();
    private readonly ChannelSelectionService _channelSelection;
    private readonly EmojiManager _emojiManager;
    private ClientWebSocket? _webSocket;
    private CancellationTokenSource? _pollCts;
    private readonly List<ChannelDto> _channels = new();
    private string[] _channelDisplayNames = Array.Empty<string>();
    private bool _channelsLoaded;
    private bool _channelsFetchInFlight;
    private bool _channelFetchFailed;
    private string _channelErrorMessage = string.Empty;
    private int _selectedIndex;
    private bool _channelWarningShown;
    private bool _channelSelectionWarningShown;
    private bool _embedWarningShown;
    private bool _permissionWarningShown;
    private readonly SemaphoreSlim _connectGate = new(1, 1);
    private DateTime _lastSync;
    private int _failureCount;
    private bool _networkingActive;
    private int _webSocketReconnectAttempt;
    private string? _lastWebSocketErrorSignature;
    private DateTime _lastWebSocketErrorLog;
    private DateTime _nextWebSocketAttempt = DateTime.MinValue;
    private static readonly TimeSpan WebSocketErrorLogThrottle = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan WebSocketCloseTimeout = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan WebSocketCloseWaitTimeout = TimeSpan.FromSeconds(3);
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly Dictionary<string, PresenceDto> _presences = new();
    private bool _presenceLoadAttempted;
    private static readonly Regex MentionRegex = new("<@!?([0-9]+)>", RegexOptions.Compiled);
    private const string DefaultWebSocketPath = "/ws/embeds";
    private const string ForbiddenMessage = "Forbidden – check API key/roles";

    public UiRenderer(
        Config config,
        HttpClient httpClient,
        ChannelSelectionService channelSelection,
        EmojiManager emojiManager,
        TokenManager tokenManager)
    {
        _config = config;
        _httpClient = httpClient;
        _channelSelection = channelSelection;
        _emojiManager = emojiManager;
        _tokenManager = tokenManager;
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
        _networkingActive = false;
        StopPolling();

        if (_webSocket != null)
        {
            var socket = _webSocket;
            _webSocket = null;
            try
            {
                await CloseClientWebSocketWithTimeoutAsync(socket, CancellationToken.None);
            }
            catch
            {
                socket.Abort();
            }
            socket.Dispose();
        }

        if (!_tokenManager.IsReady() || !_config.Enabled || !_config.Events)
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
        _networkingActive = false;
        StopPolling();
        var socket = _webSocket;
        _webSocket = null;
        if (socket != null)
        {
            try
            {
                var closeTask = CloseClientWebSocketWithTimeoutAsync(socket, CancellationToken.None);
                var completedTask = Task.WhenAny(closeTask, Task.Delay(WebSocketCloseWaitTimeout)).GetAwaiter().GetResult();
                if (completedTask != closeTask)
                {
                    socket.Abort();
                }
                else
                {
                    closeTask.GetAwaiter().GetResult();
                }
            }
            catch (OperationCanceledException)
            {
                socket.Abort();
            }
            catch (WebSocketException)
            {
                socket.Abort();
            }
            catch (ObjectDisposedException)
            {
            }
            catch (InvalidOperationException)
            {
            }
            catch (Exception)
            {
                socket.Abort();
            }
            finally
            {
                socket.Dispose();
            }
        }
        _webSocketReconnectAttempt = 0;
        _nextWebSocketAttempt = DateTime.MaxValue;
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
                if (!_tokenManager.IsReady() || !_config.Enabled || !_config.Events)
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
        if (!_tokenManager.IsReady()) return;
        if (_presenceLoadAttempted) return;
        _presenceLoadAttempted = true;
        if (!ApiHelpers.ValidateApiBaseUrl(_config)) return;
        try
        {
            var request = new HttpRequestMessage(HttpMethod.Get, $"{_config.ApiBaseUrl.TrimEnd('/')}/api/users");
            ApiHelpers.AddAuthHeader(request, _tokenManager);
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
        if (!ApiHelpers.ValidateApiBaseUrl(_config) || !_tokenManager.IsReady()) return false;

        try
        {
            var url = $"{_config.ApiBaseUrl.TrimEnd('/')}/api/embeds";
            var channelId = ChannelId;
            if (!string.IsNullOrEmpty(channelId))
            {
                url += $"?channel_id={channelId}";
            }
            var request = new HttpRequestMessage(HttpMethod.Get, url);
            ApiHelpers.AddAuthHeader(request, _tokenManager);
            var response = await _httpClient.SendAsync(request);
            if (response.StatusCode == HttpStatusCode.Unauthorized)
            {
                PluginServices.Instance?.Log.Warning("Sync unauthorized; clearing token");
                _tokenManager.Clear("Invalid API key");
                return false;
            }
            if (response.StatusCode == HttpStatusCode.Forbidden)
            {
                PluginServices.Instance?.Log.Warning("Sync forbidden; check API key/roles");
                ShowPermissionToast();
                return false;
            }
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
            ResetPermissionToast();
            PluginServices.Instance?.Log.Info($"Sync at {_lastSync:O}");
            return true;
        }
        catch (HttpRequestException ex) when (ex.StatusCode == HttpStatusCode.Unauthorized)
        {
            PluginServices.Instance?.Log.Warning(ex, "Sync unauthorized; clearing token");
            _tokenManager.Clear("Invalid API key");
            return false;
        }
        catch (HttpRequestException ex) when (ex.StatusCode == HttpStatusCode.Forbidden)
        {
            PluginServices.Instance?.Log.Warning(ex, "Sync forbidden; check API key/roles");
            ShowPermissionToast();
            return false;
        }
        catch (OperationCanceledException ex) when (IsPollingShutdown(ex))
        {
            return false;
        }
        catch (ObjectDisposedException ex) when (IsPollingShutdown(ex))
        {
            return false;
        }
        catch (HttpRequestException ex) when (IsPollingShutdown(ex))
        {
            return false;
        }
        catch (Exception ex) when (IsPollingShutdown(ex))
        {
            return false;
        }
        catch (Exception ex)
        {
            _failureCount++;
            PluginServices.Instance?.Log.Warning(ex, $"Sync exception ({_failureCount})");
            PluginServices.Instance?.ToastGui.ShowError($"Sync failed ({_failureCount})");
            return false;
        }
    }

    private bool IsPollingShutdown(Exception? exception = null)
    {
        if (!_networkingActive)
        {
            return true;
        }

        var pollCts = _pollCts;
        if (pollCts == null || pollCts.IsCancellationRequested)
        {
            return true;
        }

        if (exception is OperationCanceledException oce)
        {
            var token = oce.CancellationToken;
            if (token.CanBeCanceled && token.IsCancellationRequested)
            {
                return true;
            }
        }

        return false;
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

            if (!ApiHelpers.ValidateApiBaseUrl(_config) || !_tokenManager.IsReady() || !_config.Enabled)
            {
                ScheduleNextWebSocketAttempt();
                return;
            }

            try
            {
                var pingService = PingService.Instance ?? new PingService(_httpClient, _config, _tokenManager);
                var pingResponse = await pingService.PingAsync(CancellationToken.None);
                if (pingResponse?.StatusCode == HttpStatusCode.Unauthorized)
                {
                    PluginServices.Instance?.Log.Warning("WebSocket ping unauthorized; clearing token");
                    _tokenManager.Clear("Invalid API key");
                    return;
                }
                if (pingResponse?.StatusCode == HttpStatusCode.Forbidden)
                {
                    PluginServices.Instance?.Log.Warning("WebSocket ping forbidden; check API key/roles");
                    ShowPermissionToast();
                    return;
                }

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
                ApiHelpers.AddAuthHeader(_webSocket, _tokenManager);
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
                ResetPermissionToast();

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

    private static async Task CloseClientWebSocketWithTimeoutAsync(ClientWebSocket socket, CancellationToken cancellationToken)
    {
        if (socket == null)
        {
            return;
        }

        WebSocketState state;
        try
        {
            state = socket.State;
        }
        catch (ObjectDisposedException)
        {
            return;
        }

        if (state != WebSocketState.Open && state != WebSocketState.CloseReceived && state != WebSocketState.CloseSent)
        {
            return;
        }

        using var timeoutCts = new CancellationTokenSource(WebSocketCloseTimeout);
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);
        try
        {
            await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, string.Empty, linkedCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            socket.Abort();
        }
        catch (WebSocketException)
        {
            socket.Abort();
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
                    _embeds[dto.Id] = new EventView(
                        dto,
                        _config,
                        _httpClient,
                        RefreshEmbeds,
                        _emojiManager,
                        _tokenManager,
                        BuildMentionContent(dto));
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
        if (!_tokenManager.IsReady()) return;
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
            ApiHelpers.AddAuthHeader(request, _tokenManager);
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
        if (ImGui.GetCurrentContext() == IntPtr.Zero)
            return;

        try
        {
            if (!_tokenManager.IsReady())
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
                if (!_channelsFetchInFlight)
                {
                    _channelsFetchInFlight = true;
                    _ = FetchChannels();
                }
                ImGui.TextUnformatted("Loading channels...");
                return;
            }

            if (_channels.Count == 0)
            {
                ShowWarningOrToast(_channelFetchFailed ? _channelErrorMessage : "No channels available", ref _channelWarningShown);
                return;
            }

            var channelNames = _channelDisplayNames;

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

            {
                using var emojiFontScope = _emojiManager.PushEmojiFont();
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

            ImGui.BeginChild("##eventScroll", ImGui.GetContentRegionAvail(), ImGuiChildFlags.Border, ImGuiWindowFlags.None);
            foreach (var view in embeds)
            {
                view?.Draw();
            }

            ImGui.EndChild();
        }
        catch (Exception ex)
        {
            try
            {
                PluginServices.Instance?.Log.Error(ex, "UiRenderer.Draw()");
            }
            catch
            {
                // ignored
            }
        }
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

    private void ShowPermissionToast()
    {
        if (_permissionWarningShown)
        {
            return;
        }

        _permissionWarningShown = true;
        PluginServices.Instance?.ToastGui.ShowError(ForbiddenMessage);
    }

    private void ResetPermissionToast()
    {
        _permissionWarningShown = false;
    }

    public void ResetChannels()
    {
        _channelsLoaded = false;
        _channels.Clear();
        UpdateChannelDisplayNames();
    }

    public Task RefreshChannels()
    {
        ResetChannels();
        return FetchChannels();
    }

    private void UpdateChannelDisplayNames()
    {
        if (_channels.Count == 0)
        {
            _channelDisplayNames = Array.Empty<string>();
            return;
        }

        _channelDisplayNames = _channels
            .Select(c => c?.Name ?? c?.Id ?? string.Empty)
            .ToArray();
    }

    private void ApplyEventChannels(IReadOnlyList<ChannelDto> channels)
    {
        _channels.Clear();
        foreach (var channel in channels)
        {
            if (channel != null)
            {
                _channels.Add(channel);
            }
        }
        UpdateChannelDisplayNames();

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
    }

    internal Task ApplyChannelRefreshResult(ChannelRefreshResult result)
    {
        switch (result.Error)
        {
            case ChannelRefreshError.None:
            case ChannelRefreshError.FeatureDisabled:
            {
                ResetPermissionToast();
                var channels = result.Channels ?? Array.Empty<ChannelDto>();
                return PluginServices.Instance!.Framework.RunOnTick(() =>
                {
                    ApplyEventChannels(channels);
                });
            }
            case ChannelRefreshError.TokenMissing:
            {
                ResetPermissionToast();
                return PluginServices.Instance!.Framework.RunOnTick(() =>
                {
                    ResetChannels();
                    _channelFetchFailed = false;
                    _channelErrorMessage = string.Empty;
                });
            }
            case ChannelRefreshError.InvalidApiUrl:
            {
                ResetPermissionToast();
                return PluginServices.Instance!.Framework.RunOnTick(() =>
                {
                    _channelFetchFailed = true;
                    _channelErrorMessage = "Invalid API URL";
                    _channelsLoaded = true;
                });
            }
            case ChannelRefreshError.Unauthorized:
            {
                ResetPermissionToast();
                return PluginServices.Instance!.Framework.RunOnTick(() =>
                {
                    _channelFetchFailed = true;
                    _channelErrorMessage = "Authentication failed";
                    _channelsLoaded = true;
                });
            }
            case ChannelRefreshError.Forbidden:
            {
                ShowPermissionToast();
                return PluginServices.Instance!.Framework.RunOnTick(() =>
                {
                    _channelFetchFailed = true;
                    _channelErrorMessage = ForbiddenMessage;
                    _channelsLoaded = true;
                });
            }
            case ChannelRefreshError.Generic:
            {
                ResetPermissionToast();
                return PluginServices.Instance!.Framework.RunOnTick(() =>
                {
                    _channelFetchFailed = true;
                    _channelErrorMessage = "Failed to load channels";
                    _channelsLoaded = true;
                });
            }
            default:
                return Task.CompletedTask;
        }
    }

    private void SaveConfig()
    {
        PluginServices.Instance!.PluginInterface.SavePluginConfig(_config);
    }

    private async Task FetchChannels(bool refreshed = false)
    {
        var resetFetchFlag = false;
        try
        {
            _channelsFetchInFlight = true;
            resetFetchFlag = true;

            if (!_tokenManager.IsReady()) return;
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
                ApiHelpers.AddAuthHeader(request, _tokenManager);
                var response = await _httpClient.SendAsync(request);
                if (response.StatusCode == HttpStatusCode.Unauthorized)
                {
                    var responseBody = await response.Content.ReadAsStringAsync();
                    PluginServices.Instance!.Log.Warning(
                        $"Failed to fetch channels. Status: {response.StatusCode}. Response Body: {responseBody}. Clearing token");
                    _tokenManager.Clear("Invalid API key");
                    return;
                }
                if (response.StatusCode == HttpStatusCode.Forbidden)
                {
                    var responseBody = await response.Content.ReadAsStringAsync();
                    PluginServices.Instance!.Log.Warning(
                        $"Failed to fetch channels. Status: {response.StatusCode}. Response Body: {responseBody}.");
                    ShowPermissionToast();
                    _ = PluginServices.Instance!.Framework.RunOnTick(() =>
                    {
                        _channelFetchFailed = true;
                        _channelErrorMessage = ForbiddenMessage;
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
                if (await ChannelNameResolver.Resolve(
                        eventChannels,
                        _httpClient,
                        _config,
                        refreshed,
                        () => FetchChannels(true),
                        _tokenManager))
                {
                    return;
                }
                ResetPermissionToast();
                _ = PluginServices.Instance!.Framework.RunOnTick(() =>
                {
                    ApplyEventChannels(eventChannels);
                });
            }
            catch (HttpRequestException ex) when (ex.StatusCode == HttpStatusCode.Unauthorized)
            {
                PluginServices.Instance!.Log.Warning(
                    ex,
                    "Failed to fetch channels due to authorization failure; clearing token");
                _tokenManager.Clear("Invalid API key");
                return;
            }
            catch (HttpRequestException ex) when (ex.StatusCode == HttpStatusCode.Forbidden)
            {
                PluginServices.Instance!.Log.Warning(
                    ex,
                    "Failed to fetch channels due to forbidden response");
                ShowPermissionToast();
                _ = PluginServices.Instance!.Framework.RunOnTick(() =>
                {
                    _channelFetchFailed = true;
                    _channelErrorMessage = ForbiddenMessage;
                    _channelsLoaded = true;
                });
            }
            catch (HttpRequestException ex)
            {
                PluginServices.Instance!.Log.Warning($"Failed to fetch channels. Status: {ex.StatusCode}");
                _ = PluginServices.Instance!.Framework.RunOnTick(() =>
                {
                    _channelFetchFailed = true;
                    _channelErrorMessage = "Failed to load channels";
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
        finally
        {
            if (resetFetchFlag)
            {
                _channelsFetchInFlight = false;
            }
        }
    }

    private class ChannelListDto
    {
        [JsonPropertyName(ChannelKind.Event)] public List<ChannelDto> Event { get; set; } = new();
    }

    public async ValueTask DisposeAsync()
    {
        _networkingActive = false;
        StopPolling();
        if (_webSocket != null)
        {
            var socket = _webSocket;
            _webSocket = null;
            try
            {
                await CloseClientWebSocketWithTimeoutAsync(socket, CancellationToken.None);
            }
            catch
            {
                socket.Abort();
            }
            socket.Dispose();
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

