using System;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace DemiCatPlugin;

public class RequestWatcher : IDisposable
{
    private readonly Config _config;
    private ClientWebSocket? _ws;
    private Task? _task;
    private CancellationTokenSource? _cts;

    public RequestWatcher(Config config)
    {
        _config = config;
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
        while (!token.IsCancellationRequested)
        {
            if (!ApiHelpers.ValidateApiBaseUrl(_config) || string.IsNullOrEmpty(_config.AuthToken) || !_config.Enabled)
            {
                try { await Task.Delay(TimeSpan.FromSeconds(5), token); } catch { }
                continue;
            }
            try
            {
                _ws?.Dispose();
                _ws = new ClientWebSocket();
                _ws.Options.SetRequestHeader("X-Api-Key", _config.AuthToken);
                var uri = BuildWebSocketUri();
                await _ws.ConnectAsync(uri, token);
                var buffer = new byte[1024];
                while (_ws.State == WebSocketState.Open && !token.IsCancellationRequested)
                {
                    var (message, type) = await ChannelWatcher.ReceiveMessageAsync(_ws, buffer, token);
                    if (type == WebSocketMessageType.Close)
                        break;
                    if (message == "ping")
                    {
                        await _ws.SendAsync(new ArraySegment<byte>(Encoding.UTF8.GetBytes("pong")), WebSocketMessageType.Text, true, token);
                        continue;
                    }
                    HandleMessage(message);
                }
            }
            catch
            {
                // ignore errors and retry
            }
            finally
            {
                _ws?.Dispose();
                _ws = null;
            }
            try { await Task.Delay(TimeSpan.FromSeconds(5), token); } catch { }
        }
    }

    private void HandleMessage(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("payload", out var payload))
                return;
            if (payload.TryGetProperty("title", out var titleEl))
            {
                var title = titleEl.GetString() ?? "Request";
                PluginServices.Instance?.ToastGui.ShowNormal($"Request created: {title}");
            }
            else if (payload.TryGetProperty("status", out var statusEl))
            {
                var status = statusEl.GetString();
                if (status == "accepted")
                {
                    PluginServices.Instance?.ToastGui.ShowNormal("Request accepted");
                }
                else if (status == "completed")
                {
                    PluginServices.Instance?.ToastGui.ShowNormal("Request completed");
                }
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
