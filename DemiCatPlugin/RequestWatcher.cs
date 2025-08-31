using System;
using System.Net.Http;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace DemiCatPlugin;

public class RequestWatcher : IDisposable
{
    private readonly Config _config;
    private readonly HttpClient _httpClient;
    private ClientWebSocket? _ws;
    private Task? _task;
    private CancellationTokenSource? _cts;

    public RequestWatcher(Config config, HttpClient httpClient)
    {
        _config = config;
        _httpClient = httpClient;
    }

    public void Start()
    {
        _cts?.Cancel();
        _ws?.Dispose();
        _cts = new CancellationTokenSource();
        _task = Run(_cts.Token);
    }

    private async Task Run(CancellationToken token)
    {
        var delay = TimeSpan.FromSeconds(5);
        while (!token.IsCancellationRequested)
        {
            if (!ApiHelpers.ValidateApiBaseUrl(_config) || string.IsNullOrEmpty(_config.AuthToken) || !_config.Enabled)
            {
                try { await Task.Delay(delay, token); } catch { }
                delay = TimeSpan.FromSeconds(5);
                continue;
            }
            try
            {
                _ws?.Dispose();
                _ws = new ClientWebSocket();
                ApiHelpers.AddAuthHeader(_ws, _config);
                var uri = BuildWebSocketUri();
                await _ws.ConnectAsync(uri, token);
                delay = TimeSpan.FromSeconds(5);
                var buffer = new byte[1024];
                while (_ws.State == WebSocketState.Open && !token.IsCancellationRequested)
                {
                    var (message, type) = await ChannelWatcher.ReceiveMessageAsync(_ws, buffer, token);
                    if (type == WebSocketMessageType.Close)
                        break;
                    if (message == "ping")
                    {
                        await _ws.SendAsync(
                            new ArraySegment<byte>(Encoding.UTF8.GetBytes("pong")),
                            WebSocketMessageType.Text,
                            true,
                            token
                        );
                        continue;
                    }
                    HandleMessage(message);
                }
                if (_ws.CloseStatus == WebSocketCloseStatus.PolicyViolation)
                {
                    PluginServices.Instance?.ToastGui.ShowError("Request watcher auth failed");
                }
            }
            catch (Exception ex)
            {
                if (_ws?.CloseStatus == WebSocketCloseStatus.PolicyViolation || ex.Message.Contains("403"))
                {
                    PluginServices.Instance?.ToastGui.ShowError("Request watcher auth failed");
                }
                PluginServices.Instance?.Log.Error(ex, "Request watcher loop failed");
            }
            finally
            {
                _ws?.Dispose();
                _ws = null;
            }
            try { await RequestStateService.RefreshAll(_httpClient, _config); } catch { }
            try { await Task.Delay(delay, token); } catch { }
            delay = TimeSpan.FromSeconds(Math.Min(delay.TotalSeconds * 2, 60));
        }
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
        }
        catch (Exception ex)
        {
            PluginServices.Instance?.Log.Error(ex, "Failed to handle request notification");
        }
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
        _cts?.Cancel();
        _ws?.Dispose();
    }
}
