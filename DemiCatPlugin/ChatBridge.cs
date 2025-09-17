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
    private readonly Dictionary<string, (string GuildId, string Kind)> _channelMetadata = new();
    private readonly HashSet<string> _subs = new();
    private readonly ChannelSelectionService _channelSelection;
    private int _connectCount;
    private int _reconnectCount;
    private int _resyncCount;
    private long _backfillTotal;
    private long _backfillBatches;
    private int _ackFrameCount;
    private int _sendCount;
    private double _sendLatencyTotal;
    private long _disconnectCount;
    private DateTime _lastDisconnectLog;
    private string? _lastErrorSignature;
    private DateTime _lastErrorLog;
    private string? _lastSubscribeMismatchSignature;
    private DateTime _lastSubscribeMismatchLog;
    private string? _lastBatchDropSignature;
    private DateTime _lastBatchDropLog;
    private static readonly TimeSpan DisconnectLogThrottle = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan ErrorLogThrottle = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan SubscribeMismatchThrottle = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan BatchDropThrottle = TimeSpan.FromSeconds(30);
    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNameCaseInsensitive = true };

    public event Action<string>? MessageReceived;
    public event Action<DiscordUserDto>? TypingReceived;
    public event Action? Linked;
    public event Action? Unlinked;
    public event Action<string>? StatusChanged;
    public event Action<string, long>? ResyncRequested;
    public event Action? TemplatesUpdated;

    public ChatBridge(Config config, HttpClient httpClient, TokenManager tokenManager, Func<Uri> uriBuilder, ChannelSelectionService channelSelection)
    {
        _config = config;
        _httpClient = httpClient;
        _tokenManager = tokenManager;
        _uriBuilder = uriBuilder;
        _channelSelection = channelSelection;
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

    public void Subscribe(string channel, string? guildId, string? kind)
    {
        if (string.IsNullOrEmpty(channel)) return;
        var key = RegisterChannelMetadata(channel, guildId, kind);
        ValidateSubscription(channel, guildId, kind);
        if (!string.IsNullOrEmpty(key))
        {
            _subs.Add(key);
        }
        PluginServices.Instance?.Log.Info($"chat.ws subscribe channel={channel}");
        _ = SendSubscriptions();
    }

    public void Unsubscribe(string channel)
    {
        if (string.IsNullOrEmpty(channel)) return;
        var key = KeyForChannel(channel);
        _channelMetadata.Remove(channel);
        if (!string.IsNullOrEmpty(key) && _subs.Remove(key))
        {
            PluginServices.Instance?.Log.Info($"chat.ws unsubscribe channel={channel}");
            _ = SendSubscriptions();
        }
    }

    public void Ack(string channel, string? guildId, string? kind)
    {
        if (!string.IsNullOrEmpty(channel))
        {
            RegisterChannelMetadata(channel, guildId, kind);
        }
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

    private static string Key(string? guildId, string? kind, string channelId)
        => ChannelKeyHelper.BuildCursorKey(guildId, kind, channelId);

    private string KeyForChannel(string channel)
    {
        if (string.IsNullOrEmpty(channel)) return string.Empty;
        if (_channelMetadata.TryGetValue(channel, out var meta))
        {
            return Key(meta.GuildId, meta.Kind, channel);
        }
        return Key(_config.GuildId, null, channel);
    }

    private string RegisterChannelMetadata(string channel, string? guildId, string? kind)
    {
        if (string.IsNullOrEmpty(channel)) return string.Empty;
        var normalizedGuild = ChannelKeyHelper.NormalizeGuildId(guildId);
        var normalizedKind = ChannelKeyHelper.NormalizeKind(kind);
        _channelMetadata[channel] = (normalizedGuild, normalizedKind);
        return Key(normalizedGuild, normalizedKind, channel);
    }

    private void UpdateChannelMetadata(string channel, string? guildId, string? kind)
    {
        if (string.IsNullOrEmpty(channel)) return;

        var hasGuild = !string.IsNullOrWhiteSpace(guildId);
        var hasKind = !string.IsNullOrWhiteSpace(kind);
        if (!hasGuild && !hasKind) return;

        if (_channelMetadata.TryGetValue(channel, out var existing))
        {
            var normalizedGuild = hasGuild ? ChannelKeyHelper.NormalizeGuildId(guildId) : existing.GuildId;
            var normalizedKind = hasKind ? ChannelKeyHelper.NormalizeKind(kind) : existing.Kind;
            _channelMetadata[channel] = (normalizedGuild, normalizedKind);
        }
        else if (hasGuild && hasKind)
        {
            RegisterChannelMetadata(channel, guildId, kind);
        }
    }

    private void ValidateSubscription(string channel, string? guildId, string? kind)
    {
        if (string.IsNullOrEmpty(channel) || string.IsNullOrEmpty(kind)) return;
        var normalizedGuild = ChannelKeyHelper.NormalizeGuildId(guildId);
        var normalizedKind = ChannelKeyHelper.NormalizeKind(kind);
        if (string.IsNullOrEmpty(normalizedKind)) return;
        var selected = _channelSelection.GetChannel(normalizedKind, normalizedGuild);
        if (string.IsNullOrEmpty(selected)) return;
        if (string.Equals(selected, channel, StringComparison.Ordinal)) return;
        LogSubscribeMismatch(channel, normalizedGuild, normalizedKind, selected);
    }

    private void LogSubscribeMismatch(string channel, string guildId, string kind, string selected)
    {
        var now = DateTime.UtcNow;
        var signature = $"{guildId}:{kind}:{selected}";
        if (_lastSubscribeMismatchSignature == signature && (now - _lastSubscribeMismatchLog) < SubscribeMismatchThrottle)
        {
            return;
        }
        _lastSubscribeMismatchSignature = signature;
        _lastSubscribeMismatchLog = now;
        PluginServices.Instance?.Log.Warning($"chat.ws subscribe mismatch channel={channel} guild={guildId} kind={kind} selected={selected}");
    }

    private bool ShouldAcceptFrame(string op, string channel, string? guildId, string? kind)
    {
        var normalizedGuild = ChannelKeyHelper.NormalizeGuildId(guildId);
        var normalizedKind = ChannelKeyHelper.NormalizeKind(kind);

        if (string.IsNullOrEmpty(channel))
        {
            LogBatchDrop(op, channel, normalizedGuild, normalizedKind, "missing_channel", null, null, null);
            return false;
        }

        if (_channelMetadata.TryGetValue(channel, out var meta))
        {
            if (!string.Equals(meta.GuildId, normalizedGuild, StringComparison.Ordinal) ||
                !string.Equals(meta.Kind, normalizedKind, StringComparison.Ordinal))
            {
                LogBatchDrop(op, channel, normalizedGuild, normalizedKind, "metadata_mismatch", meta.GuildId, meta.Kind, null);
                return false;
            }
        }

        if (string.IsNullOrEmpty(normalizedKind))
        {
            return true;
        }

        var selected = _channelSelection.GetChannel(normalizedKind, normalizedGuild);
        if (!string.IsNullOrEmpty(selected) && !string.Equals(selected, channel, StringComparison.Ordinal))
        {
            LogBatchDrop(op, channel, normalizedGuild, normalizedKind, "selection_mismatch", normalizedGuild, normalizedKind, selected);
            return false;
        }

        return true;
    }

    private void LogBatchDrop(string op, string channel, string guildId, string kind, string reason, string? expectedGuild, string? expectedKind, string? expectedChannel)
    {
        var message = $"chat.ws drop batch op={op} channel={channel} guild={guildId} kind={kind} reason={reason}";
        if (!string.IsNullOrEmpty(expectedGuild))
        {
            message += $" expected_guild={expectedGuild}";
        }
        if (!string.IsNullOrEmpty(expectedKind))
        {
            message += $" expected_kind={expectedKind}";
        }
        if (!string.IsNullOrEmpty(expectedChannel))
        {
            message += $" expected_channel={expectedChannel}";
        }
        var now = DateTime.UtcNow;
        var signature = $"{channel}:{guildId}:{kind}:{reason}:{expectedGuild ?? string.Empty}:{expectedKind ?? string.Empty}:{expectedChannel ?? string.Empty}";
        if (_lastBatchDropSignature == signature && (now - _lastBatchDropLog) < BatchDropThrottle)
        {
            return;
        }
        _lastBatchDropSignature = signature;
        _lastBatchDropLog = now;
        PluginServices.Instance?.Log.Warning(message);
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
                    root.TryGetProperty("guildId", out var guildEl);
                    root.TryGetProperty("kind", out var kindEl);
                    var guildId = guildEl.ValueKind == JsonValueKind.String ? guildEl.GetString() : null;
                    var kind = kindEl.ValueKind == JsonValueKind.String ? kindEl.GetString() : null;
                    if (!ShouldAcceptFrame("batch", channel, guildId, kind))
                    {
                        break;
                    }
                    var key = RegisterChannelMetadata(channel, guildId, kind);
                    if (string.IsNullOrEmpty(key))
                    {
                        key = KeyForChannel(channel);
                    }
                    if (string.IsNullOrEmpty(key))
                    {
                        break;
                    }
                    var msgs = root.GetProperty("messages");
                    var count = 0;
                    foreach (var msg in msgs.EnumerateArray())
                    {
                        count++;
                        var cursor = msg.GetProperty("cursor").GetInt64();
                        _cursors[key] = cursor;
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
                    root.TryGetProperty("guildId", out var resGuildEl);
                    root.TryGetProperty("kind", out var resKindEl);
                    var resGuildId = resGuildEl.ValueKind == JsonValueKind.String ? resGuildEl.GetString() : null;
                    var resKind = resKindEl.ValueKind == JsonValueKind.String ? resKindEl.GetString() : null;
                    if (!ShouldAcceptFrame("resync", ch, resGuildId, resKind))
                    {
                        break;
                    }
                    RegisterChannelMetadata(ch, resGuildId, resKind);
                    var cur = root.GetProperty("cursor").GetInt64();
                    _resyncCount++;
                    PluginServices.Instance?.Log.Info($"chat.ws resync channel={ch} count={_resyncCount}");
                    ResyncRequested?.Invoke(ch, cur);
                    break;
                case "ack":
                    var ackChannel = root.TryGetProperty("channel", out var ackChannelEl) && ackChannelEl.ValueKind == JsonValueKind.String
                        ? ackChannelEl.GetString() ?? string.Empty
                        : string.Empty;
                    root.TryGetProperty("guildId", out var ackGuildEl);
                    root.TryGetProperty("kind", out var ackKindEl);
                    var ackGuildId = ackGuildEl.ValueKind == JsonValueKind.String ? ackGuildEl.GetString() : null;
                    var ackKind = ackKindEl.ValueKind == JsonValueKind.String ? ackKindEl.GetString() : null;
                    UpdateChannelMetadata(ackChannel, ackGuildId, ackKind);
                    _ackFrameCount++;
                    var ackGuildLog = ackGuildId ?? string.Empty;
                    var ackKindLog = ackKind ?? string.Empty;
                    PluginServices.Instance?.Log.Info($"chat.ws ack channel={ackChannel} guild={ackGuildLog} kind={ackKindLog} count={_ackFrameCount}");
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
        if (string.IsNullOrEmpty(channel)) return Task.CompletedTask;
        var key = KeyForChannel(channel);
        if (string.IsNullOrEmpty(key)) return Task.CompletedTask;
        if (!_cursors.TryGetValue(key, out var cursor)) return Task.CompletedTask;
        if (_acked.TryGetValue(key, out var acked) && acked == cursor) return Task.CompletedTask;
        _acked[key] = cursor;
        _config.ChatCursors[key] = cursor;
        var json = JsonSerializer.Serialize(new { op = "ack", ch = channel, cur = cursor });
        return SendRaw(json);
    }

    private Task SendSubscriptions()
    {
        if (_ws == null || _ws.State != WebSocketState.Open || _subs.Count == 0)
            return Task.CompletedTask;
        var chans = new List<Dictionary<string, object?>>();
        foreach (var kvp in _channelMetadata)
        {
            var channelId = kvp.Key;
            var meta = kvp.Value;
            var key = Key(meta.GuildId, meta.Kind, channelId);
            if (!_subs.Contains(key)) continue;

            _config.ChatCursors.TryGetValue(key, out var since);

            var channel = new Dictionary<string, object?>
            {
                ["id"] = channelId,
                ["since"] = since,
            };

            var normalizedGuild = ChannelKeyHelper.NormalizeGuildId(meta.GuildId);
            if (!string.IsNullOrEmpty(normalizedGuild))
            {
                channel["guildId"] = normalizedGuild;
            }

            var normalizedKind = ChannelKeyHelper.NormalizeKind(meta.Kind);
            if (!string.IsNullOrEmpty(normalizedKind))
            {
                channel["kind"] = normalizedKind;
            }

            chans.Add(channel);
        }
        if (chans.Count == 0) return Task.CompletedTask;
        var json = JsonSerializer.Serialize(new { op = "sub", channels = chans });
        PluginServices.Instance?.Log.Info($"chat.ws subscribe count={chans.Count}");
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
