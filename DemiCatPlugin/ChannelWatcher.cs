using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.WebSockets;
using System.Threading;
using System.Threading.Tasks;
using System.Text;
using System.IO;

namespace DemiCatPlugin;

public class ChannelWatcher : IDisposable
{
    private readonly Config _config;
    private readonly HttpClient _httpClient;
    private readonly UiRenderer _ui;
    private readonly EventCreateWindow _eventCreateWindow;
    private readonly TemplatesWindow _templatesWindow;
    private readonly ChatWindow _chatWindow;
    private readonly OfficerChatWindow _officerChatWindow;
    private readonly TokenManager _tokenManager;
    private ClientWebSocket? _ws;
    private Task? _task;
    private CancellationTokenSource? _cts;
    private DateTime _lastRefresh = DateTime.MinValue;
    private readonly TimeSpan _refreshCooldown = TimeSpan.FromSeconds(2);
    private int _retryAttempt;
    private string? _lastErrorSignature;
    private DateTime _lastErrorLog;
    private static readonly TimeSpan ErrorLogThrottle = TimeSpan.FromSeconds(30);

    internal static ChannelWatcher? Instance { get; private set; }

    public ChannelWatcher(Config config, UiRenderer ui, EventCreateWindow eventCreateWindow, TemplatesWindow templatesWindow, ChatWindow chatWindow, OfficerChatWindow officerChatWindow, TokenManager tokenManager, HttpClient httpClient)
    {
        _config = config;
        _ui = ui;
        _eventCreateWindow = eventCreateWindow;
        _templatesWindow = templatesWindow;
        _chatWindow = chatWindow;
        _officerChatWindow = officerChatWindow;
        _tokenManager = tokenManager;
        _httpClient = httpClient;

        Instance = this;
    }

    public async Task Start()
    {
        if (!_config.Events && !_config.SyncedChat && !_config.Roles.Contains("officer"))
        {
            return;
        }
        _cts?.Cancel();
        if (_task != null)
        {
            try { await _task; } catch { }
        }
        _ws?.Dispose();
        _cts = new CancellationTokenSource();
        _task = Run(_cts.Token);
    }

    public void Stop()
    {
        _cts?.Cancel();
        try { _task?.GetAwaiter().GetResult(); } catch { }
        _ws?.Dispose();
        _ws = null;
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
                    if (status == HttpStatusCode.Unauthorized || status == HttpStatusCode.Forbidden)
                    {
                        PluginServices.Instance?.ToastGui.ShowError("Channel watcher auth failed");
                        PluginServices.Instance!.Log.Warning("Clearing stored token after channel watcher auth failure.");
                        _ = Task.Run(() => _tokenManager.Clear("Authentication failed"));
                    }
                    await DelayWithBackoff(baseDelay, token);
                    _retryAttempt = 0;
                    continue;
                }

                _ws?.Dispose();
                _ws = new ClientWebSocket();
                ApiHelpers.AddAuthHeader(_ws, _tokenManager);

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
                await _ws.ConnectAsync(uri!, token);
                PluginServices.Instance!.Log.Information("Channel watcher connected to {Uri}", uri);
                _retryAttempt = 0;
                hadTransportError = false;

                var buffer = new byte[1024];
                while (_ws.State == WebSocketState.Open && !token.IsCancellationRequested)
                {
                    var (message, messageType) = await ReceiveMessageAsync(_ws, buffer, token);
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

                if (_ws.CloseStatus == WebSocketCloseStatus.PolicyViolation)
                {
                    PluginServices.Instance?.ToastGui.ShowError("Channel watcher auth failed");
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
                LogConnectionException(ex, "connect");
            }
            catch (WebSocketException ex)
            {
                hadTransportError = true;
                if (_ws?.CloseStatus == WebSocketCloseStatus.PolicyViolation || ex.Message?.Contains("403", StringComparison.Ordinal) == true)
                {
                    PluginServices.Instance?.ToastGui.ShowError("Channel watcher auth failed");
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
                closeStatus = _ws?.CloseStatus;
                closeStatusDescription = _ws?.CloseStatusDescription;
                if (closeStatus.HasValue)
                {
                    retryStatusCode ??= (int)closeStatus.Value;
                    retryStatusDetail ??= closeStatus.Value.ToString();
                }
                PluginServices.Instance!.Log.Information("Channel watcher disconnected. Status: {Status}, Description: {Description}",
                    closeStatus?.ToString() ?? "unknown", closeStatusDescription ?? "");
                _ws?.Dispose();
                _ws = null;
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

        _ = SafeRefresh(_ui.RefreshChannels);
        _ = SafeRefresh(_eventCreateWindow.RefreshChannels);
        _ = SafeRefresh(_templatesWindow.RefreshChannels);
        if (_config.SyncedChat && _config.EnableFcChat)
            _ = SafeRefresh(_chatWindow.RefreshChannels);
        if (_config.Roles.Contains("officer"))
            _ = SafeRefresh(_officerChatWindow.RefreshChannels);

        _lastRefresh = DateTime.UtcNow;
    }

    public void TriggerRefresh(bool force = false)
    {
        RefreshChannelsIfNeeded(force);
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
        _ws?.Dispose();
        _cts?.Dispose();
        if (Instance == this) Instance = null;
    }
}
