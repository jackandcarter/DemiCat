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

    public event Action<string>? MessageReceived;
    public event Action? Linked;
    public event Action? Unlinked;
    public event Action<string>? StatusChanged;
    public event Action<string, long>? ResyncRequested;

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
                _ws.Options.SetRequestHeader("Sec-WebSocket-Extensions", "permessage-deflate");
                ApiHelpers.AddAuthHeader(_ws, _tokenManager);
                var uri = _uriBuilder();
                await _ws.ConnectAsync(uri, token);
                _connectedSince = DateTime.UtcNow;
                _reconnectAttempt = 0;
                _tokenValid = true;
                Linked?.Invoke();
                StatusChanged?.Invoke(string.Empty);

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
            var op = root.GetProperty("op").GetString();
            switch (op)
            {
                case "batch":
                    var channel = root.GetProperty("channel").GetString() ?? string.Empty;
                    foreach (var msg in root.GetProperty("messages").EnumerateArray())
                    {
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
                    break;
                case "resync":
                    var ch = root.GetProperty("channel").GetString() ?? string.Empty;
                    var cur = root.GetProperty("cursor").GetInt64();
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

    private Task SendRaw(string json)
    {
        if (_ws == null || _ws.State != WebSocketState.Open)
            return Task.CompletedTask;
        var bytes = Encoding.UTF8.GetBytes(json);
        return _ws.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, CancellationToken.None);
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
        return SendRaw(json);
    }

    private async Task<bool> ValidateToken(CancellationToken token)
    {
        try
        {
            var request = new HttpRequestMessage(HttpMethod.Head, $"{_config.ApiBaseUrl.TrimEnd('/')}/api/ping");
            ApiHelpers.AddAuthHeader(request, _tokenManager);
            var response = await _httpClient.SendAsync(request, token);
            return response.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
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
