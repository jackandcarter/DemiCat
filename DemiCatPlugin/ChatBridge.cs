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
    private long _disconnectCount;
    private DateTime _lastDisconnectLog;
    private string? _lastErrorSignature;
    private DateTime _lastErrorLog;
    private static readonly TimeSpan DisconnectLogThrottle = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan ErrorLogThrottle = TimeSpan.FromSeconds(30);
    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNameCaseInsensitive = true };

    public event Action<string>? MessageReceived;
    public event Action<DiscordUserDto>? TypingReceived;
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
        try { _task?.GetAwaiter().GetResult(); }
        catch (OperationCanceledException) { }
        catch { }
        _ws?.Dispose();
        _ws = null;
        _tokenValid = false;
        _task = null;
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

    public void Unsubscribe(string channel)
    {
        if (string.IsNullOrEmpty(channel)) return;
        if (_subs.Remove(channel))
        {
            PluginServices.Instance?.Log.Info($"chat.ws unsubscribe channel={channel}");
            _ = SendSubscriptions();
        }
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
                _tokenManager.Clear("Invalid API key");
                Unlinked?.Invoke();
                StatusChanged?.Invoke("Authentication failed");
                await DelayWithJitter(5, token);
                continue;
            }

            var forbidden = false;
            Exception? transportError = null;
            var failureStage = "connect";
            var connected = false;
            var attemptedConnect = false;
            try
            {
                StatusChanged?.Invoke("Connecting...");
                Uri? uri;
                try
                {
                    uri = _uriBuilder();
                }
                catch (Exception ex)
                {
                    LogConnectionException(ex, "uri");
                    StatusChanged?.Invoke("Invalid API URL");
                    await DelayWithJitter(5, token);
                    continue;
                }

                if (!IsValidWebSocketUri(uri))
                {
                    LogConnectionException(new InvalidOperationException("Missing WebSocket URL"), "uri");
                    StatusChanged?.Invoke("Invalid API URL");
                    await DelayWithJitter(5, token);
                    continue;
                }

                _ws?.Dispose();
                _ws = new ClientWebSocket();
                _ws.Options.DangerousDeflateOptions = new WebSocketDeflateOptions();
                _ws.Options.SetRequestHeader("Sec-WebSocket-Extensions", "permessage-deflate");
                ApiHelpers.AddAuthHeader(_ws, _tokenManager);
                attemptedConnect = true;
                await _ws.ConnectAsync(uri!, token);
                _connectedSince = DateTime.UtcNow;
                _reconnectAttempt = 0;
                _tokenValid = true;
                connected = true;
                failureStage = "receive";
                Linked?.Invoke();
                StatusChanged?.Invoke(string.Empty);
                _connectCount++;
                if (_connectCount > 1) _reconnectCount++;
                PluginServices.Instance?.Log.Info($"chat.ws connect count={_connectCount} reconnects={_reconnectCount}");

                await SendSubscriptions();

                await ReceiveLoop(token);
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
                forbidden = ex.StatusCode == HttpStatusCode.Forbidden;
                transportError = ex;
                failureStage = connected ? "receive" : "connect";
            }
            catch (WebSocketException ex)
            {
                forbidden = ex.Message?.Contains("403", StringComparison.Ordinal) == true
                    || (ex.InnerException as HttpRequestException)?.StatusCode == HttpStatusCode.Forbidden;
                transportError = ex;
                failureStage = connected ? "receive" : "connect";
            }
            catch (IOException ex)
            {
                transportError = ex;
                failureStage = connected ? "receive" : "connect";
            }
            catch (Exception ex)
            {
                forbidden |= (ex.InnerException as HttpRequestException)?.StatusCode == HttpStatusCode.Forbidden;
                transportError = ex;
                failureStage = connected ? "receive" : "connect";
            }
            finally
            {
                var status = _ws?.CloseStatus;
                var description = _ws?.CloseStatusDescription;
                _ws?.Dispose();
                _ws = null;
                if (attemptedConnect)
                {
                    LogDisconnect(status, description);
                }
            }

            if (token.IsCancellationRequested)
            {
                break;
            }

            if (transportError != null)
            {
                LogConnectionException(transportError, failureStage);
            }

            if (forbidden)
            {
                _tokenManager.Clear("Invalid API key");
                Unlinked?.Invoke();
                _tokenValid = false;
            }

            if ((DateTime.UtcNow - _connectedSince) > TimeSpan.FromSeconds(60))
            {
                _reconnectAttempt = 0;
            }
            _reconnectAttempt++;
            var backoff = GetReconnectDelay(_reconnectAttempt);
            StatusChanged?.Invoke(forbidden
                ? "Forbidden – check API key/roles"
                : $"Reconnecting in {backoff.TotalSeconds:0.#}s...");
            await DelayWithBackoff(backoff, token);
        }
    }

    private async Task ReceiveLoop(CancellationToken token)
    {
        if (_ws == null) return;
        var buffer = new byte[8192];
        try
        {
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
        catch (WebSocketException)
        {
            throw;
        }
        catch (IOException)
        {
            throw;
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
                        else if (mOp == "ty")
                        {
                            var payload = msg.GetProperty("d").GetRawText();
                            var author = JsonSerializer.Deserialize<DiscordUserDto>(payload, JsonOpts);
                            if (author != null)
                            {
                                TypingReceived?.Invoke(author);
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
        try
        {
            var sw = Stopwatch.StartNew();
            await _ws.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, CancellationToken.None);
            var ms = sw.Elapsed.TotalMilliseconds;
            _sendCount++;
            _sendLatencyTotal += ms;
            var avg = _sendLatencyTotal / _sendCount;
            PluginServices.Instance?.Log.Info($"chat.ws send latency_ms={ms:F1} avg_latency_ms={avg:F1} count={_sendCount}");
        }
        catch (OperationCanceledException ex) when (!ShouldRethrow(ex, CancellationToken.None))
        {
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (ObjectDisposedException)
        {
        }
        catch (WebSocketException ex)
        {
            LogConnectionException(ex, "send");
        }
        catch (IOException ex)
        {
            LogConnectionException(ex, "send");
        }
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
        var pingService = PingService.Instance ?? new PingService(_httpClient, _config, _tokenManager);
        var response = await pingService.PingAsync(token);
        if (response?.StatusCode == HttpStatusCode.NotFound)
        {
            PluginServices.Instance?.Log.Error("Backend ping endpoints missing. Please update or restart the backend.");
        }
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
        catch (OperationCanceledException ex) when (!ShouldRethrow(ex, token))
        {
        }
        catch (OperationCanceledException)
        {
            throw;
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

    private TimeSpan GetReconnectDelay(int attempt)
    {
        var cappedAttempt = Math.Max(1, attempt);
        var baseDelay = Math.Min(15d, Math.Pow(2, cappedAttempt - 1));
        var min = Math.Max(0.5d, baseDelay / 2d);
        var max = Math.Max(min, baseDelay);
        var jitter = min + (max - min) * _random.NextDouble();
        return TimeSpan.FromSeconds(jitter);
    }

    private static bool ShouldRethrow(OperationCanceledException _, CancellationToken token)
        => token.CanBeCanceled && token.IsCancellationRequested;

    private static bool IsValidWebSocketUri(Uri? uri)
    {
        if (uri == null)
            return false;

        if (!uri.IsAbsoluteUri)
            return false;

        if (string.IsNullOrWhiteSpace(uri.ToString()))
            return false;

        return string.Equals(uri.Scheme, "ws", StringComparison.OrdinalIgnoreCase)
            || string.Equals(uri.Scheme, "wss", StringComparison.OrdinalIgnoreCase);
    }

    private void LogDisconnect(WebSocketCloseStatus? status, string? description)
    {
        _disconnectCount++;
        var now = DateTime.UtcNow;
        if (_disconnectCount > 1 && (now - _lastDisconnectLog) < DisconnectLogThrottle)
        {
            return;
        }

        _lastDisconnectLog = now;
        var statusText = status.HasValue ? $"{(int)status} {status}" : "n/a";
        var desc = string.IsNullOrWhiteSpace(description) ? string.Empty : $" desc={description}";
        PluginServices.Instance?.Log.Info($"chat.ws disconnect count={_disconnectCount} status={statusText}{desc}");
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
        PluginServices.Instance?.Log.Error(ex, $"chat.ws {stage} failed");
    }
}
