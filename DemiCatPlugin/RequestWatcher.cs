using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.WebSockets;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace DemiCatPlugin;

public class RequestWatcher : IDisposable
{
    private readonly Config _config;
    private readonly HttpClient _httpClient;
    private readonly TokenManager _tokenManager;
    private Task? _task;
    private CancellationTokenSource? _cts;
    private int _retryAttempt;
    private string? _lastErrorSignature;
    private DateTime _lastErrorLog;
    private static readonly TimeSpan ErrorLogThrottle = TimeSpan.FromSeconds(30);
    private const string ForbiddenMessage = "Forbidden – check API key/roles";
    private bool _permissionWarningShown;

    public RequestWatcher(Config config, HttpClient httpClient, TokenManager tokenManager)
    {
        _config = config;
        _httpClient = httpClient;
        _tokenManager = tokenManager;
    }

    internal bool IsRunning => _cts != null;

    public void Start()
    {
        if (!_config.Requests)
        {
            return;
        }
        _cts?.Cancel();
        _cts = new CancellationTokenSource();
        _task = Run(_cts.Token);
    }

    public async Task Stop()
    {
        var cts = _cts;
        _cts = null;

        cts?.Cancel();
        if (_task != null)
        {
            try
            {
                await _task.ConfigureAwait(false);
            }
            catch
            {
            }
        }

        _task = null;
        cts?.Dispose();
    }

    private async Task Run(CancellationToken token)
    {
        var baseDelay = TimeSpan.FromSeconds(5);
        while (!token.IsCancellationRequested)
        {
            if (!ApiHelpers.ValidateApiBaseUrl(_config) || !_tokenManager.IsReady() || !_config.Enabled || !_config.Requests)
            {
                await DelayWithBackoff(baseDelay, token);
                _retryAttempt = 0;
                continue;
            }

            var hadTransportError = true;
            ClientWebSocket? ws = null;
            try
            {
                var pingService = PingService.Instance ?? new PingService(_httpClient, _config, _tokenManager);
                var pingResponse = await pingService.PingAsync(token);
                if (pingResponse?.IsSuccessStatusCode != true)
                {
                    if (pingResponse?.StatusCode == HttpStatusCode.NotFound)
                    {
                        PluginServices.Instance!.Log.Error("Backend ping endpoints missing. Please update or restart the backend.");
                    }
                    if (pingResponse?.StatusCode == HttpStatusCode.Forbidden)
                    {
                        ShowPermissionWarning();
                    }
                    await DelayWithBackoff(baseDelay, token);
                    _retryAttempt = 0;
                    continue;
                }

                try { await RequestStateService.RefreshAll(_httpClient, _config); } catch { }

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

                await ws.ConnectAsync(uri!, token);
                _retryAttempt = 0;
                hadTransportError = false;
                ResetPermissionWarning();

                var buffer = new byte[1024];
                while (ws != null && ws.State == WebSocketState.Open && !token.IsCancellationRequested)
                {
                    var (message, type) = await ChannelWatcher.ReceiveMessageAsync(ws, buffer, token);
                    if (type == WebSocketMessageType.Close)
                        break;
                    HandleMessage(message);
                }

                if (ws?.CloseStatus == WebSocketCloseStatus.PolicyViolation)
                {
                    ShowPermissionWarning();
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
                if (ex.StatusCode == HttpStatusCode.Unauthorized)
                {
                    if (_tokenManager.IsReady())
                    {
                        _tokenManager.Clear("Authentication failed");
                    }
                }
                else if (ex.StatusCode == HttpStatusCode.Forbidden)
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
                LogConnectionException(ex, "connect");
            }
            catch (IOException ex)
            {
                hadTransportError = true;
                LogConnectionException(ex, "connect");
            }
            catch (Exception ex)
            {
                hadTransportError = true;
                PluginServices.Instance?.Log.Error(ex, "Request watcher loop failed");
            }
            finally
            {
                ws?.Dispose();
                ws = null;
            }

            if (token.IsCancellationRequested)
                break;

            if (_tokenManager.IsReady())
            {
                try { await RequestStateService.RefreshAll(_httpClient, _config); } catch { }
            }

            if (hadTransportError)
            {
                _retryAttempt++;
                var retryDelay = GetRetryDelay(_retryAttempt);
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

    private void HandleMessage(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("payload", out var payload))
                return;

            var id = payload.TryGetProperty("id", out var idEl) ? idEl.GetString() : null;
            var deleted = payload.TryGetProperty("deleted", out var delEl) && delEl.GetBoolean();
            if (deleted)
            {
                if (id != null) RequestStateService.Remove(id);
                return;
            }

            var title = payload.TryGetProperty("title", out var titleEl) ? titleEl.GetString() ?? "Request" : "Request";
            var statusString = payload.TryGetProperty("status", out var statusEl) ? statusEl.GetString() : null;
            var version = payload.TryGetProperty("version", out var verEl) ? verEl.GetInt32() : 0;
            var itemId = payload.TryGetProperty("item_id", out var itemEl) ? itemEl.GetUInt32() : (uint?)null;
            var dutyId = payload.TryGetProperty("duty_id", out var dutyEl) ? dutyEl.GetUInt32() : (uint?)null;
            var hq = payload.TryGetProperty("hq", out var hqEl) && hqEl.GetBoolean();
            var quantity = payload.TryGetProperty("quantity", out var qtyEl) ? qtyEl.GetInt32() : 0;
            var assigneeId = payload.TryGetProperty("assignee_id", out var aEl) ? aEl.GetUInt32() : (uint?)null;

            if (id == null || statusString == null)
                return;

            var status = statusString switch
            {
                "open" => RequestStatus.Open,
                "claimed" => RequestStatus.Claimed,
                "in_progress" => RequestStatus.InProgress,
                "awaiting_confirm" => RequestStatus.AwaitingConfirm,
                "completed" => RequestStatus.Completed,
                "cancelled" => RequestStatus.Cancelled,
                "approved" => RequestStatus.Approved,
                "denied" => RequestStatus.Denied,
                _ => RequestStatus.Open
            };

            RequestStateService.Upsert(new RequestState
            {
                Id = id,
                Title = title,
                Status = status,
                Version = version,
                ItemId = itemId,
                DutyId = dutyId,
                Hq = hq,
                Quantity = quantity,
                AssigneeId = assigneeId
            });

            if (status == RequestStatus.Claimed)
            {
                PluginServices.Instance?.ToastGui.ShowNormal("Request claimed");
            }
            else if (status == RequestStatus.Completed)
            {
                PluginServices.Instance?.ToastGui.ShowNormal("Request completed");
            }
            else if (status == RequestStatus.Cancelled)
            {
                PluginServices.Instance?.ToastGui.ShowNormal("Request cancelled");
            }
            else if (status == RequestStatus.Approved)
            {
                PluginServices.Instance?.ToastGui.ShowNormal("Request approved (legacy)");
            }
            else if (status == RequestStatus.Denied)
            {
                PluginServices.Instance?.ToastGui.ShowNormal("Request denied (legacy)");
            }
        }
        catch (Exception ex)
        {
            PluginServices.Instance?.Log.Error(ex, "Failed to handle request notification");
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
        PluginServices.Instance?.Log.Error(ex, $"request.ws {stage} failed");
    }

    private Uri BuildWebSocketUri()
    {
        var baseUri = _config.ApiBaseUrl.TrimEnd('/') + "/ws/requests";
        var builder = new UriBuilder(baseUri);
        if (builder.Scheme == "https") builder.Scheme = "wss";
        else if (builder.Scheme == "http") builder.Scheme = "ws";
        return builder.Uri;
    }

    public void Dispose()
    {
        _ = Stop();
        _cts?.Dispose();
        _cts = null;
        _task = null;
    }
}
