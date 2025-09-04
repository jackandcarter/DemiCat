using System;
using System.Net.WebSockets;
using System.Threading;
using System.Threading.Tasks;
using System.Text;

namespace DemiCatPlugin;

public class ChannelWatcher : IDisposable
{
    private readonly Config _config;
    private readonly UiRenderer _ui;
    private readonly EventCreateWindow _eventCreateWindow;
    private readonly ChatWindow _chatWindow;
    private readonly OfficerChatWindow _officerChatWindow;
    private ClientWebSocket? _ws;
    private Task? _task;
    private CancellationTokenSource? _cts;

    public ChannelWatcher(Config config, UiRenderer ui, EventCreateWindow eventCreateWindow, ChatWindow chatWindow, OfficerChatWindow officerChatWindow)
    {
        _config = config;
        _ui = ui;
        _eventCreateWindow = eventCreateWindow;
        _chatWindow = chatWindow;
        _officerChatWindow = officerChatWindow;
    }

    public async Task Start()
    {
        _cts?.Cancel();
        if (_task != null)
        {
            try { await _task; } catch { }
        }
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
                    var (message, messageType) = await ReceiveMessageAsync(_ws, buffer, token);
                    if (messageType == WebSocketMessageType.Close)
                    {
                        break;
                    }
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
                    if (message == "update")
                    {
                        _ = PluginServices.Instance!.Framework.RunOnTick(() =>
                        {
                            _ = SafeRefresh(_ui.RefreshChannels);
                            _ = SafeRefresh(_eventCreateWindow.RefreshChannels);
                            if (_config.EnableFcChat)
                                _ = SafeRefresh(_chatWindow.RefreshChannels);
                            _ = SafeRefresh(_officerChatWindow.RefreshChannels);
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
                _ws?.Dispose();
                _ws = null;
            }
            if (token.IsCancellationRequested)
                break;

            _ = PluginServices.Instance!.Framework.RunOnTick(() =>
            {
                _ = SafeRefresh(_ui.RefreshChannels);
                _ = SafeRefresh(_eventCreateWindow.RefreshChannels);
                if (_config.EnableFcChat)
                    _ = SafeRefresh(_chatWindow.RefreshChannels);
                _ = SafeRefresh(_officerChatWindow.RefreshChannels);
            });
            try { await Task.Delay(delay, token); } catch { }
            delay = TimeSpan.FromSeconds(Math.Min(delay.TotalSeconds * 2, 60));
        }
    }

    internal static async Task<(string message, WebSocketMessageType messageType)> ReceiveMessageAsync(WebSocket ws, byte[] buffer, CancellationToken token)
    {
        var sb = new StringBuilder();
        WebSocketReceiveResult result;
        do
        {
            result = await ws.ReceiveAsync(new ArraySegment<byte>(buffer), token);
            if (result.MessageType == WebSocketMessageType.Close)
            {
                return (string.Empty, WebSocketMessageType.Close);
            }
            sb.Append(Encoding.UTF8.GetString(buffer, 0, result.Count));
        } while (!result.EndOfMessage);

        return (sb.ToString(), result.MessageType);
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
