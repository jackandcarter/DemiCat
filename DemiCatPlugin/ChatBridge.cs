using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Net.WebSockets;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Diagnostics;

namespace DemiCatPlugin;

public class ChatBridge : IDisposable
{
    private readonly Config _config;
    private readonly HttpClient _httpClient;
    private readonly TokenManager _tokenManager;
    private readonly Func<Uri> _uriBuilder;
    private ClientWebSocket? _ws;
    private CancellationTokenSource? _cts;
    private Task? _task;
    private readonly Random _random = new();
    private int _reconnectAttempt;
    private DateTime _connectedSince;
    private bool _tokenValid;
    private readonly Dictionary<string, long> _cursors = new();
    private readonly Dictionary<string, long> _acked = new();
    private readonly HashSet<string> _subs = new();
    private int _connectCount;
    private int _reconnectCount;
    private int _resyncCount;
    private long _backfillTotal;
    private long _backfillBatches;
    private int _sendCount;
    private double _sendLatencyTotal;

    public event Action<string>? MessageReceived;
    public event Action? Linked;
    public event Action? Unlinked;
    public event Action<string>? StatusChanged;
    public event Action<string, long>? ResyncRequested;
    public event Action? TemplatesUpdated;

    public ChatBridge(Config config, HttpClient httpClient, TokenManager tokenManager, Func<Uri> uriBuilder)
    {
        _config = config;
        _httpClient = httpClient;
        _tokenManager = tokenManager;
        _uriBuilder = uriBuilder;
    }

    public void Start()
    {
        Stop();
        _cts = new CancellationTokenSource();
        _task = Run(_cts.Token);
    }

    public void Stop()
    {
        _cts?.Cancel();
        _ws?.Dispose();
        _ws = null;
        _tokenValid = false;
    }

    public void Dispose() => Stop();

    public bool IsReady() => _tokenValid && _ws?.State == WebSocketState.Open;

    public void Subscribe(string channel)
    {
        if (string.IsNullOrEmpty(channel)) return;
        _subs.Add(channel);
        PluginServices.Instance?.Log.Info($"chat.ws subscribe channel={channel}");
        _ = SendSubscriptions();
    }

    public void Ack(string channel)
    {
        _ = AckAsync(channel);
    }

    public Task Send(string channel, object payload)
    {
        var json = JsonSerializer.Serialize(new { op = "send", ch = channel, d = payload });
        return SendRaw(json);
    }

    public void Resync(string channel)
    {
        var json = JsonSerializer.Serialize(new { op = "resync", ch = channel });
        PluginServices.Instance?.Log.Info($"chat.ws resync channel={channel}");
        _resyncCount++;
        _ = SendRaw(json);
    }

    private async Task Run(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            if (!ApiHelpers.ValidateApiBaseUrl(_config))
            {
                StatusChanged?.Invoke("Invalid API URL");
                await DelayWithJitter(5, token);
                continue;
            }

            if (!_tokenManager.IsReady())
            {
                await DelayWithJitter(5, token);
                continue;
            }

            if (!await ValidateToken(token))
            {
                _tokenManager.Clear();
                Unlinked?.Invoke();
                StatusChanged?.Invoke("Authentication failed");
                await DelayWithJitter(5, token);
                continue;
            }

            var forbidden = false;
            try
            {
                StatusChanged?.Invoke("Connecting...");
                _ws?.Dispose();
                _ws = new ClientWebSocket();
                _ws.Options.DangerousDeflateOptions = new WebSocketDeflateOptions();
                _ws.Options.SetRequestHeader("Sec-WebSocket-Extensions", "permessage-deflate");
                ApiHelpers.AddAuthHeader(_ws, _tokenManager);
                var uri = _uriBuilder();
                await _ws.ConnectAsync(uri, token);
                _connectedSince = DateTime.UtcNow;
                _reconnectAttempt = 0;
                _tokenValid = true;
                Linked?.Invoke();
                StatusChanged?.Invoke(string.Empty);
                _connectCount++;
                if (_connectCount > 1) _reconnectCount++;
                PluginServices.Instance?.Log.Info($"chat.ws connect count={_connectCount} reconnects={_reconnectCount}");

                await SendSubscriptions();

                await ReceiveLoop(token);
            }
            catch (Exception ex)
            {
                forbidden = ex is HttpRequestException hre && hre.StatusCode == HttpStatusCode.Forbidden
                    || (ex as WebSocketException)?.Message.Contains("403") == true
                    || (ex.InnerException as HttpRequestException)?.StatusCode == HttpStatusCode.Forbidden;
            }
            finally
            {
                _ws?.Dispose();
                _ws = null;
                PluginServices.Instance?.Log.Info("chat.ws disconnect");
            }

            if (token.IsCancellationRequested)
            {
                break;
            }

            if (forbidden)
            {
                _tokenManager.Clear();
                Unlinked?.Invoke();
                _tokenValid = false;
            }

            var delay = Math.Min((int)Math.Pow(2, _reconnectAttempt), 60);
            if ((DateTime.UtcNow - _connectedSince) > TimeSpan.FromSeconds(60))
            {
                _reconnectAttempt = 0;
                delay = 1;
            }
            else
            {
                _reconnectAttempt++;
            }
            StatusChanged?.Invoke(forbidden ? "Forbidden – check API key/roles" : $"Reconnecting in {delay}s...");
            await DelayWithJitter(delay, token);
        }
    }

    private async Task ReceiveLoop(CancellationToken token)
    {
        if (_ws == null) return;
        var buffer = new byte[8192];
        while (_ws.State == WebSocketState.Open && !token.IsCancellationRequested)
        {
            using var ms = new MemoryStream();
            WebSocketReceiveResult result;
            do
            {
                result = await _ws.ReceiveAsync(new ArraySegment<byte>(buffer), token);
                if (result.MessageType == WebSocketMessageType.Close)
                {
                    return;
                }
                ms.Write(buffer, 0, result.Count);
                if (result.Count == buffer.Length)
                {
                    Array.Resize(ref buffer, buffer.Length * 2);
                }
            } while (!result.EndOfMessage);

            var json = Encoding.UTF8.GetString(ms.ToArray());
            HandleMessage(json);
        }
    }

    private void HandleMessage(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.TryGetProperty("topic", out var topicEl))
            {
                var topic = topicEl.GetString();
                if (topic == "templates.updated")
                {
                    TemplatesUpdated?.Invoke();
                }
                return;
            }
            var op = root.GetProperty("op").GetString();
            switch (op)
            {
                case "batch":
                    var channel = root.GetProperty("channel").GetString() ?? string.Empty;
                    var msgs = root.GetProperty("messages");
                    var count = 0;
                    foreach (var msg in msgs.EnumerateArray())
                    {
                        count++;
                        var cursor = msg.GetProperty("cursor").GetInt64();
                        _cursors[channel] = cursor;
                        var mOp = msg.GetProperty("op").GetString();
                        if (mOp == "mc" || mOp == "mu")
                        {
                            var payload = msg.GetProperty("d").GetRawText();
                            MessageReceived?.Invoke(payload);
                        }
                        else if (mOp == "md")
                        {
                            var id = msg.GetProperty("d").GetProperty("id").GetString();
                            if (id != null)
                            {
                                MessageReceived?.Invoke($"{{\"deletedId\":\"{id}\"}}");
                            }
                        }
                    }
                    _backfillTotal += count;
                    _backfillBatches++;
                    var avg = _backfillBatches == 0 ? 0 : (double)_backfillTotal / _backfillBatches;
                    PluginServices.Instance?.Log.Info($"chat.ws batch channel={channel} size={count} avg_backfill={avg:F1}");
                    break;
                case "resync":
                    var ch = root.GetProperty("channel").GetString() ?? string.Empty;
                    var cur = root.GetProperty("cursor").GetInt64();
                    _resyncCount++;
                    PluginServices.Instance?.Log.Info($"chat.ws resync channel={ch} count={_resyncCount}");
                    ResyncRequested?.Invoke(ch, cur);
                    break;
                case "ping":
                    _ = SendRaw("{\"op\":\"ping\"}");
                    break;
            }
        }
        catch
        {
            // ignore malformed
        }
    }

    private async Task SendRaw(string json)
    {
        if (_ws == null || _ws.State != WebSocketState.Open)
            return;
        var bytes = Encoding.UTF8.GetBytes(json);
        var sw = Stopwatch.StartNew();
        await _ws.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, CancellationToken.None);
        var ms = sw.Elapsed.TotalMilliseconds;
        _sendCount++;
        _sendLatencyTotal += ms;
        var avg = _sendLatencyTotal / _sendCount;
        PluginServices.Instance?.Log.Info($"chat.ws send latency_ms={ms:F1} avg_latency_ms={avg:F1} count={_sendCount}");
    }

    private Task AckAsync(string channel)
    {
        if (!_cursors.TryGetValue(channel, out var cursor)) return Task.CompletedTask;
        if (_acked.TryGetValue(channel, out var acked) && acked == cursor) return Task.CompletedTask;
        _acked[channel] = cursor;
        _config.ChatCursors[channel] = cursor;
        var json = JsonSerializer.Serialize(new { op = "ack", ch = channel, cur = cursor });
        return SendRaw(json);
    }

    private Task SendSubscriptions()
    {
        if (_ws == null || _ws.State != WebSocketState.Open || _subs.Count == 0)
            return Task.CompletedTask;
        var chans = new List<object>();
        foreach (var ch in _subs)
        {
            _config.ChatCursors.TryGetValue(ch, out var since);
            chans.Add(new { id = ch, since });
        }
        var json = JsonSerializer.Serialize(new { op = "sub", channels = chans });
        PluginServices.Instance?.Log.Info($"chat.ws subscribe count={_subs.Count}");
        return SendRaw(json);
    }

    private async Task<bool> ValidateToken(CancellationToken token)
    {
        var response = await ApiHelpers.PingAsync(_httpClient, _config, _tokenManager, token);
        return response?.IsSuccessStatusCode ?? false;
    }

    private async Task DelayWithJitter(int seconds, CancellationToken token)
    {
        var jitter = _random.NextDouble();
        var ms = (int)(seconds * 1000 + jitter * 1000);
        try
        {
            await Task.Delay(ms, token);
        }
        catch
        {
            // ignore
        }
    }
}
