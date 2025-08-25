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
    private readonly ChatWindow _chatWindow;
    private readonly OfficerChatWindow _officerChatWindow;
    private ClientWebSocket? _ws;
    private Task? _task;
    private CancellationTokenSource? _cts;

    public ChannelWatcher(Config config, UiRenderer ui, ChatWindow chatWindow, OfficerChatWindow officerChatWindow)
    {
        _config = config;
        _ui = ui;
        _chatWindow = chatWindow;
        _officerChatWindow = officerChatWindow;
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
                var buffer = new byte[16];
                while (_ws.State == WebSocketState.Open && !token.IsCancellationRequested)
                {
                    var result = await _ws.ReceiveAsync(new ArraySegment<byte>(buffer), token);
                    if (result.MessageType == WebSocketMessageType.Close)
                    {
                        break;
                    }
                    var message = Encoding.UTF8.GetString(buffer, 0, result.Count);
                    if (message == "ping")
                    {
                        await _ws.SendAsync(new ArraySegment<byte>(Encoding.UTF8.GetBytes("pong")), WebSocketMessageType.Text, true, token);
                        continue;
                    }
                    _ = PluginServices.Instance!.Framework.RunOnTick(() =>
                    {
                        _ = SafeRefresh(_ui.RefreshChannels);
                        _ = SafeRefresh(_chatWindow.RefreshChannels);
                        _ = SafeRefresh(_officerChatWindow.RefreshChannels);
                    });
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
        _ws?.Dispose();
    }
}
