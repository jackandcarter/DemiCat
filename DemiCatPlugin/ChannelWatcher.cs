using System;
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
    private readonly ChatWindow _chatWindow;
    private readonly OfficerChatWindow _officerChatWindow;
    private readonly TokenManager _tokenManager;
    private ClientWebSocket? _ws;
    private Task? _task;
    private CancellationTokenSource? _cts;
    private DateTime _lastRefresh = DateTime.MinValue;
    private readonly TimeSpan _refreshCooldown = TimeSpan.FromSeconds(2);

    public ChannelWatcher(Config config, UiRenderer ui, EventCreateWindow eventCreateWindow, ChatWindow chatWindow, OfficerChatWindow officerChatWindow, TokenManager tokenManager, HttpClient httpClient)
    {
        _config = config;
        _ui = ui;
        _eventCreateWindow = eventCreateWindow;
        _chatWindow = chatWindow;
        _officerChatWindow = officerChatWindow;
        _tokenManager = tokenManager;
        _httpClient = httpClient;
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
        var delay = TimeSpan.FromSeconds(5);
        while (!token.IsCancellationRequested)
        {
            if (!ApiHelpers.ValidateApiBaseUrl(_config) || !_tokenManager.IsReady() || !_config.Enabled)
            {
                try { await Task.Delay(delay, token); } catch { }
                delay = TimeSpan.FromSeconds(Math.Min(delay.TotalSeconds * 2, 60));
                continue;
            }

            try
            {
                var pingResponse = await ApiHelpers.PingAsync(_httpClient, _config, _tokenManager, token);
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
                    }
                    try { await Task.Delay(delay, token); } catch { }
                    delay = TimeSpan.FromSeconds(Math.Min(delay.TotalSeconds * 2, 60));
                    continue;
                }

                _ws?.Dispose();
                _ws = new ClientWebSocket();
                ApiHelpers.AddAuthHeader(_ws, _tokenManager);
                var uri = BuildWebSocketUri();
                PluginServices.Instance!.Log.Information("Connecting to channel watcher at {Uri}", uri);
                await _ws.ConnectAsync(uri, token);
                PluginServices.Instance!.Log.Information("Channel watcher connected to {Uri}", uri);
                delay = TimeSpan.FromSeconds(5);

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
                }
            }
            catch (OperationCanceledException)
            {
                // Swallow cancellation during shutdown
            }
            catch (Exception ex)
            {
                if (_ws?.CloseStatus == WebSocketCloseStatus.PolicyViolation || ex.Message.Contains("403"))
                {
                    PluginServices.Instance?.ToastGui.ShowError("Channel watcher auth failed");
                }
                PluginServices.Instance!.Log.Error(ex, "Channel watcher loop failed");
            }
            finally
            {
                var status = _ws?.CloseStatus;
                var description = _ws?.CloseStatusDescription;
                PluginServices.Instance!.Log.Information("Channel watcher disconnected. Status: {Status}, Description: {Description}",
                    status?.ToString() ?? "unknown", description ?? "");
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

            try { await Task.Delay(delay, token); } catch { }
            delay = TimeSpan.FromSeconds(Math.Min(delay.TotalSeconds * 2, 60));
        }
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

    private void RefreshChannelsIfNeeded()
    {
        if (DateTime.UtcNow - _lastRefresh < _refreshCooldown)
            return;

        _ = SafeRefresh(_ui.RefreshChannels);
        _ = SafeRefresh(_eventCreateWindow.RefreshChannels);
        if (_config.SyncedChat && _config.EnableFcChat)
            _ = SafeRefresh(_chatWindow.RefreshChannels);
        if (_config.Roles.Contains("officer"))
            _ = SafeRefresh(_officerChatWindow.RefreshChannels);

        _lastRefresh = DateTime.UtcNow;
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
    }
}
