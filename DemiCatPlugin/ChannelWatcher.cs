using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.WebSockets;
using System.Threading;
using System.Threading.Tasks;
using System.Text;
using System.Linq;

namespace DemiCatPlugin;

public class ChannelWatcher : IDisposable
{
    private readonly Config _config;
    private readonly HttpClient _httpClient;
    private readonly ChannelService _channelService;
    private readonly UiRenderer _ui;
    private readonly EventCreateWindow _eventCreateWindow;
    private readonly TemplatesWindow _templatesWindow;
    private readonly ChatWindow _chatWindow;
    private readonly OfficerChatWindow _officerChatWindow;
    private readonly TokenManager _tokenManager;
    private Task? _task;
    private CancellationTokenSource? _cts;
    private DateTime _lastRefresh = DateTime.MinValue;
    private readonly TimeSpan _refreshCooldown = TimeSpan.FromSeconds(2);
    private int _retryAttempt;
    private readonly SemaphoreSlim _refreshGate = new(1, 1);
    private readonly Dictionary<string, ChannelRefreshResult> _channelCache = new(StringComparer.OrdinalIgnoreCase);
    private DateTime _channelCacheTimestamp = DateTime.MinValue;
    private string _cachedGuildId = string.Empty;
    private string? _lastErrorSignature;
    private DateTime _lastErrorLog;
    private static readonly TimeSpan ErrorLogThrottle = TimeSpan.FromSeconds(30);
    private const string ForbiddenMessage = "Forbidden – check API key/roles";
    private bool _permissionWarningShown;

    internal static ChannelWatcher? Instance { get; private set; }

    internal bool IsRunning => _cts != null;

    public ChannelWatcher(Config config, UiRenderer ui, EventCreateWindow eventCreateWindow, TemplatesWindow templatesWindow, ChatWindow chatWindow, OfficerChatWindow officerChatWindow, TokenManager tokenManager, HttpClient httpClient, ChannelService channelService)
    {
        _config = config;
        _ui = ui;
        _eventCreateWindow = eventCreateWindow;
        _templatesWindow = templatesWindow;
        _chatWindow = chatWindow;
        _officerChatWindow = officerChatWindow;
        _tokenManager = tokenManager;
        _httpClient = httpClient;
        _channelService = channelService;

        Instance = this;
        _tokenManager.OnUnlinked += HandleTokenUnlinked;
    }

    public async Task Start()
    {
        if (!_config.Events && !_config.SyncedChat && !OfficerPermissions.HasAccess(_config))
        {
            return;
        }
        _cts?.Cancel();
        if (_task != null)
        {
            try { await _task; } catch { }
        }
        _cts = new CancellationTokenSource();
        _task = Run(_cts.Token);
    }

    public void Stop()
    {
        _cts?.Cancel();
        try { _task?.GetAwaiter().GetResult(); } catch { }
        _cts = null;
        _task = null;
    }

    private async Task Run(CancellationToken token)
    {
        var baseDelay = TimeSpan.FromSeconds(5);
        while (!token.IsCancellationRequested)
        {
            if (!ApiHelpers.ValidateApiBaseUrl(_config) || !_tokenManager.IsReady() || !_config.Enabled)
            {
                await DelayWithBackoff(baseDelay, token);
                _retryAttempt = 0;
                continue;
            }

            var hadTransportError = true;
            int? retryStatusCode = null;
            string? retryStatusDetail = null;
            WebSocketCloseStatus? closeStatus = null;
            string? closeStatusDescription = null;
            ClientWebSocket? ws = null;
            try
            {
                var pingService = PingService.Instance ?? new PingService(_httpClient, _config, _tokenManager);
                var pingResponse = await pingService.PingAsync(token);
                if (pingResponse?.IsSuccessStatusCode != true)
                {
                    var responseBody = pingResponse == null ? string.Empty : await pingResponse.Content.ReadAsStringAsync();
                    var status = pingResponse?.StatusCode;
                    PluginServices.Instance!.Log.Warning("Channel watcher ping failed. Status: {Status}. Response Body: {ResponseBody}", status?.ToString() ?? "unknown", responseBody ?? "");
                    if (status == HttpStatusCode.NotFound)
                    {
                        PluginServices.Instance!.Log.Error("Backend ping endpoints missing. Please update or restart the backend.");
                    }
                    if (status == HttpStatusCode.Unauthorized)
                    {
                        if (_tokenManager.IsReady())
                        {
                            PluginServices.Instance!.Log.Warning("Clearing stored token after channel watcher auth failure.");
                            _ = Task.Run(() => _tokenManager.Clear("Authentication failed"));
                        }
                    }
                    else if (status == HttpStatusCode.Forbidden)
                    {
                        ShowPermissionWarning();
                    }
                    await DelayWithBackoff(baseDelay, token);
                    _retryAttempt = 0;
                    continue;
                }

                ws = new ClientWebSocket();
                ApiHelpers.AddAuthHeader(ws, _tokenManager);

                Uri? uri;
                try
                {
                    uri = BuildWebSocketUri();
                }
                catch (Exception ex)
                {
                    LogConnectionException(ex, "uri");
                    await DelayWithBackoff(baseDelay, token);
                    _retryAttempt = 0;
                    continue;
                }

                if (!IsValidWebSocketUri(uri))
                {
                    LogConnectionException(new InvalidOperationException("Missing WebSocket URL"), "uri");
                    await DelayWithBackoff(baseDelay, token);
                    _retryAttempt = 0;
                    continue;
                }

                PluginServices.Instance!.Log.Information("Connecting to channel watcher at {Uri}", uri);
                await ws.ConnectAsync(uri!, token);
                PluginServices.Instance!.Log.Information("Channel watcher connected to {Uri}", uri);
                _retryAttempt = 0;
                hadTransportError = false;
                ResetPermissionWarning();

                var buffer = new byte[1024];
                while (ws != null && ws.State == WebSocketState.Open && !token.IsCancellationRequested)
                {
                    var (message, messageType) = await ReceiveMessageAsync(ws, buffer, token);
                    if (messageType == WebSocketMessageType.Close)
                    {
                        break;
                    }
                    if (message == "update" && _tokenManager.IsReady())
                    {
                        _ = PluginServices.Instance!.Framework.RunOnTick(() =>
                        {
                            RefreshChannelsIfNeeded();
                        });
                    }
                }

                if (ws?.CloseStatus == WebSocketCloseStatus.PolicyViolation)
                {
                    ShowPermissionWarning();
                    retryStatusCode = (int)WebSocketCloseStatus.PolicyViolation;
                    retryStatusDetail = WebSocketCloseStatus.PolicyViolation.ToString();
                    hadTransportError = true;
                }
            }
            catch (OperationCanceledException ex) when (!ShouldRethrow(ex, token))
            {
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (ObjectDisposedException)
            {
            }
            catch (HttpRequestException ex)
            {
                hadTransportError = true;
                retryStatusCode = ex.StatusCode.HasValue ? (int)ex.StatusCode.Value : null;
                retryStatusDetail = ex.StatusCode?.ToString() ?? ex.Message;
                if (ex.StatusCode == HttpStatusCode.Forbidden)
                {
                    ShowPermissionWarning();
                }
                LogConnectionException(ex, "connect");
            }
            catch (WebSocketException ex)
            {
                hadTransportError = true;
                if (ws?.CloseStatus == WebSocketCloseStatus.PolicyViolation || ex.Message?.Contains("403", StringComparison.Ordinal) == true)
                {
                    ShowPermissionWarning();
                }
                retryStatusCode = (int)ex.WebSocketErrorCode;
                retryStatusDetail = ex.WebSocketErrorCode.ToString();
                LogConnectionException(ex, "connect");
            }
            catch (IOException ex)
            {
                hadTransportError = true;
                retryStatusDetail = ex.Message;
                LogConnectionException(ex, "connect");
            }
            catch (Exception ex)
            {
                hadTransportError = true;
                retryStatusDetail = ex.Message;
                LogConnectionException(ex, "loop");
            }
            finally
            {
                closeStatus = ws?.CloseStatus;
                closeStatusDescription = ws?.CloseStatusDescription;
                if (closeStatus.HasValue)
                {
                    retryStatusCode ??= (int)closeStatus.Value;
                    retryStatusDetail ??= closeStatus.Value.ToString();
                }
                PluginServices.Instance!.Log.Information("Channel watcher disconnected. Status: {Status}, Description: {Description}",
                    closeStatus?.ToString() ?? "unknown", closeStatusDescription ?? "");
                ws?.Dispose();
                ws = null;
            }

            if (token.IsCancellationRequested)
                break;

            if (_tokenManager.IsReady())
            {
                _ = PluginServices.Instance!.Framework.RunOnTick(() =>
                {
                    RefreshChannelsIfNeeded();
                });
            }

            if (hadTransportError)
            {
                _retryAttempt++;
                var retryDelay = GetRetryDelay(_retryAttempt);
                var statusDetail = retryStatusDetail ?? "unknown";
                var statusCodeValue = retryStatusCode ?? 0;
                PluginServices.Instance!.Log.Warning(
                    "Channel watcher retry {Attempt}. Status {StatusCode} ({StatusDetail}). Next retry in {Delay}",
                    _retryAttempt,
                    statusCodeValue,
                    statusDetail,
                    $"{retryDelay.TotalSeconds:0.###}s"
                );
                await DelayWithBackoff(retryDelay, token);
            }
            else
            {
                _retryAttempt = 0;
                await DelayWithBackoff(baseDelay, token);
            }
        }
    }

    private void ShowPermissionWarning()
    {
        if (_permissionWarningShown)
        {
            return;
        }

        _permissionWarningShown = true;
        _ = PluginServices.Instance?.Framework.RunOnTick(() =>
        {
            PluginServices.Instance?.ToastGui.ShowError(ForbiddenMessage);
        });
    }

    private void ResetPermissionWarning()
    {
        _permissionWarningShown = false;
    }

    private async Task DelayWithBackoff(TimeSpan delay, CancellationToken token)
    {
        try
        {
            await Task.Delay(delay, token);
        }
        catch (OperationCanceledException ex) when (!ShouldRethrow(ex, token))
        {
        }
        catch (OperationCanceledException)
        {
            throw;
        }
    }

    private static TimeSpan GetRetryDelay(int attempt)
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

    private void LogConnectionException(Exception ex, string stage)
    {
        var now = DateTime.UtcNow;
        var signature = $"{stage}:{ex.GetType().FullName}:{ex.Message}";
        if (_lastErrorSignature == signature && (now - _lastErrorLog) < ErrorLogThrottle)
        {
            return;
        }

        _lastErrorSignature = signature;
        _lastErrorLog = now;
        PluginServices.Instance!.Log.Error(ex, $"channel.ws {stage} failed");
    }

    internal static async Task<(string message, WebSocketMessageType messageType)> ReceiveMessageAsync(WebSocket ws, byte[] buffer, CancellationToken token)
    {
        using var ms = new MemoryStream();
        WebSocketReceiveResult result;
        do
        {
            result = await ws.ReceiveAsync(new ArraySegment<byte>(buffer), token);
            if (result.MessageType == WebSocketMessageType.Close)
            {
                return (string.Empty, WebSocketMessageType.Close);
            }
            ms.Write(buffer, 0, result.Count);
            if (result.Count == buffer.Length)
            {
                Array.Resize(ref buffer, buffer.Length * 2);
            }
        } while (!result.EndOfMessage);

        var message = Encoding.UTF8.GetString(ms.ToArray());
        return (message, result.MessageType);
    }

    private static async Task SafeRefresh(Func<Task> refresh)
    {
        try
        {
            await refresh();
        }
        catch (Exception ex)
        {
            PluginServices.Instance!.Log.Error(ex, "Channel refresh failed");
        }
    }

    private void RefreshChannelsIfNeeded(bool force = false)
    {
        if (!force && DateTime.UtcNow - _lastRefresh < _refreshCooldown)
            return;

        _ = SafeRefresh(() => RefreshConsumersAsync(force));
    }

    public void TriggerRefresh(bool force = false)
    {
        RefreshChannelsIfNeeded(force);
    }

    public void InvalidateCache()
    {
        lock (_channelCache)
        {
            _channelCache.Clear();
            _channelCacheTimestamp = DateTime.MinValue;
            _cachedGuildId = string.Empty;
        }
    }

    private void HandleTokenUnlinked(string? _)
    {
        InvalidateCache();
    }

    private async Task RefreshConsumersAsync(bool force)
    {
        await _refreshGate.WaitAsync().ConfigureAwait(false);
        try
        {
            var results = await GetChannelDataAsync(force).ConfigureAwait(false);

            var eventResult = GetResult(results, ChannelKind.Event);
            var fcResult = GetResult(results, ChannelKind.FcChat);
            var officerResult = GetResult(results, ChannelKind.OfficerChat);

            var tasks = new List<Task>
            {
                SafeRefresh(() => _ui.ApplyChannelRefreshResult(eventResult)),
                SafeRefresh(() => _eventCreateWindow.ApplyChannelRefreshResult(eventResult)),
                SafeRefresh(() => _templatesWindow.ApplyChannelRefreshResult(eventResult)),
                SafeRefresh(() => _chatWindow.ApplyChannelRefreshResult(fcResult)),
                SafeRefresh(() => _officerChatWindow.ApplyChannelRefreshResult(officerResult))
            };

            await Task.WhenAll(tasks).ConfigureAwait(false);
            _lastRefresh = DateTime.UtcNow;
        }
        finally
        {
            _refreshGate.Release();
        }
    }

    private ChannelRefreshResult GetResult(Dictionary<string, ChannelRefreshResult> results, string kind)
    {
        if (results.TryGetValue(kind, out var result))
        {
            return result;
        }

        return ChannelRefreshResult.Failure(kind, ChannelRefreshError.Generic);
    }

    private async Task<Dictionary<string, ChannelRefreshResult>> GetChannelDataAsync(bool force)
    {
        var normalizedGuild = ChannelKeyHelper.NormalizeGuildId(_config.GuildId);
        var now = DateTime.UtcNow;

        lock (_channelCache)
        {
            if (!force
                && _channelCacheTimestamp != DateTime.MinValue
                && string.Equals(_cachedGuildId, normalizedGuild, StringComparison.Ordinal)
                && now - _channelCacheTimestamp < _refreshCooldown)
            {
                return new Dictionary<string, ChannelRefreshResult>(_channelCache, StringComparer.OrdinalIgnoreCase);
            }
        }

        var results = await FetchChannelDataAsync().ConfigureAwait(false);

        lock (_channelCache)
        {
            _channelCache.Clear();
            foreach (var kvp in results)
            {
                _channelCache[kvp.Key] = kvp.Value;
            }
            _channelCacheTimestamp = now;
            _cachedGuildId = normalizedGuild;
        }

        return results;
    }

    private async Task<Dictionary<string, ChannelRefreshResult>> FetchChannelDataAsync()
    {
        var results = new Dictionary<string, ChannelRefreshResult>(StringComparer.OrdinalIgnoreCase);

        if (!_tokenManager.IsReady())
        {
            results[ChannelKind.Event] = ChannelRefreshResult.Failure(ChannelKind.Event, ChannelRefreshError.TokenMissing);
            results[ChannelKind.FcChat] = ChannelRefreshResult.Failure(ChannelKind.FcChat, ChannelRefreshError.TokenMissing);
            results[ChannelKind.OfficerChat] = ChannelRefreshResult.Failure(ChannelKind.OfficerChat, ChannelRefreshError.TokenMissing);
            return results;
        }

        if (!ApiHelpers.ValidateApiBaseUrl(_config))
        {
            PluginServices.Instance!.Log.Warning("Cannot fetch channels: API base URL is not configured.");
            results[ChannelKind.Event] = ChannelRefreshResult.Failure(ChannelKind.Event, ChannelRefreshError.InvalidApiUrl);
            results[ChannelKind.FcChat] = ChannelRefreshResult.Failure(ChannelKind.FcChat, ChannelRefreshError.InvalidApiUrl);
            results[ChannelKind.OfficerChat] = ChannelRefreshResult.Failure(ChannelKind.OfficerChat, ChannelRefreshError.InvalidApiUrl);
            return results;
        }

        results[ChannelKind.Event] = await FetchKindAsync(ChannelKind.Event).ConfigureAwait(false);

        if (_config.SyncedChat && _config.EnableFcChat)
        {
            results[ChannelKind.FcChat] = await FetchKindAsync(ChannelKind.FcChat).ConfigureAwait(false);
        }
        else
        {
            results[ChannelKind.FcChat] = ChannelRefreshResult.FeatureDisabled(ChannelKind.FcChat);
        }

        if (OfficerPermissions.HasAccess(_config))
        {
            results[ChannelKind.OfficerChat] = await FetchKindAsync(ChannelKind.OfficerChat).ConfigureAwait(false);
        }
        else
        {
            results[ChannelKind.OfficerChat] = ChannelRefreshResult.FeatureDisabled(ChannelKind.OfficerChat);
        }

        return results;
    }

    private async Task<ChannelRefreshResult> FetchKindAsync(string kind)
    {
        try
        {
            var channels = await FetchChannelsForKindAsync(kind, refreshed: false).ConfigureAwait(false);
            return ChannelRefreshResult.Success(kind, channels);
        }
        catch (HttpRequestException ex) when (ex.StatusCode == HttpStatusCode.Unauthorized)
        {
            var status = ex.StatusCode?.ToString() ?? "Unknown";
            PluginServices.Instance!.Log.Warning(ex, "Failed to fetch channels for {Kind}. Status: {Status}", kind, status);
            _ = Task.Run(() => _tokenManager.Clear("Invalid API key"));
            return ChannelRefreshResult.Failure(kind, ChannelRefreshError.Unauthorized);
        }
        catch (HttpRequestException ex) when (ex.StatusCode == HttpStatusCode.Forbidden)
        {
            var status = ex.StatusCode?.ToString() ?? "Unknown";
            PluginServices.Instance!.Log.Warning(ex, "Failed to fetch channels for {Kind}. Status: {Status}", kind, status);
            return ChannelRefreshResult.Failure(kind, ChannelRefreshError.Forbidden);
        }
        catch (HttpRequestException ex)
        {
            var status = ex.StatusCode?.ToString() ?? "Unknown";
            PluginServices.Instance!.Log.Warning(ex, "Failed to fetch channels for {Kind}. Status: {Status}", kind, status);
            return ChannelRefreshResult.Failure(kind, ChannelRefreshError.Generic);
        }
        catch (Exception ex)
        {
            PluginServices.Instance!.Log.Error(ex, "Error fetching channels for {Kind}", kind);
            return ChannelRefreshResult.Failure(kind, ChannelRefreshError.Generic);
        }
    }

    private async Task<IReadOnlyList<ChannelDto>> FetchChannelsForKindAsync(string kind, bool refreshed, CancellationToken ct = default)
    {
        var channels = (await _channelService.FetchAsync(kind, ct).ConfigureAwait(false)).ToList();
        if (await ChannelNameResolver.Resolve(channels, _httpClient, _config, refreshed, () => Task.CompletedTask).ConfigureAwait(false))
        {
            return await FetchChannelsForKindAsync(kind, true, ct).ConfigureAwait(false);
        }

        return channels;
    }

    private Uri BuildWebSocketUri()
    {
        var baseUri = _config.ApiBaseUrl.TrimEnd('/') + "/ws/channels";
        var builder = new UriBuilder(baseUri);
        if (builder.Scheme == "https") builder.Scheme = "wss";
        else if (builder.Scheme == "http") builder.Scheme = "ws";
        return builder.Uri;
    }

    public void Dispose()
    {
        _cts?.Cancel();
        try { _task?.GetAwaiter().GetResult(); } catch { }
        _cts?.Dispose();
        _cts = null;
        _task = null;
        _tokenManager.OnUnlinked -= HandleTokenUnlinked;
        if (Instance == this) Instance = null;
    }
}
