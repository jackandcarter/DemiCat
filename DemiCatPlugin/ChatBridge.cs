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

public class ChatBridge : IChatBridge
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
    private readonly object _stateLock = new();
    private readonly ChannelSelectionService _channelSelection;
    private readonly Func<ClientWebSocket> _webSocketFactory;
    private readonly Func<ClientWebSocket, Uri, CancellationToken, Task> _connectAsync;
    private readonly TimeSpan _connectTimeout;
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
    private static readonly TimeSpan WebSocketCloseTimeout = TimeSpan.FromSeconds(1);
    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNameCaseInsensitive = true };
    private static readonly TimeSpan DefaultConnectTimeout = TimeSpan.FromSeconds(10);
    private const string ForbiddenMessage = "Forbidden – check API key/roles";
    private bool _permissionWarningShown;
    private static void OnFramework(Action action)
        => PluginServices.Instance?.Framework.RunOnTick(action);

    private const bool SpreadAcrossFrames = true;

    // Dispatch list items to a handler in slices, scheduling each slice over successive framework ticks.
    private static void DispatchInTicks<T>(IReadOnlyList<T> items, int slice, Action<T> handler)
    {
        if (items == null || items.Count == 0 || slice <= 0 || handler == null) return;

        var idx = 0;
        void Step()
        {
            var end = Math.Min(idx + slice, items.Count);
            for (; idx < end; idx++)
            {
                handler(items[idx]);
            }

            if (idx < items.Count)
            {
                OnFramework(Step); // schedule next frame
            }
        }

        OnFramework(Step);
    }
#if TEST
    internal bool? ForceWebSocketOpen { get; set; }
    internal Action<string>? SendRawInterceptor { get; set; }
#endif

    public event Action<string>? MessageReceived;
    public event Action<DiscordUserDto>? TypingReceived;
    public event Action? Linked;
    public event Action? Unlinked;
    public event Action<string>? StatusChanged;
    public event Action<string, long>? ResyncRequested;
    public event Action? TemplatesUpdated;

    public ChatBridge(Config config, HttpClient httpClient, TokenManager tokenManager, Func<Uri> uriBuilder, ChannelSelectionService channelSelection, Func<ClientWebSocket>? webSocketFactory = null, TimeSpan? connectTimeout = null, Func<ClientWebSocket, Uri, CancellationToken, Task>? connectAsync = null)
    {
        _config = config;
        _httpClient = httpClient;
        _tokenManager = tokenManager;
        _uriBuilder = uriBuilder;
        _channelSelection = channelSelection;
        _webSocketFactory = webSocketFactory ?? (() => new ClientWebSocket());
        _connectTimeout = connectTimeout ?? DefaultConnectTimeout;
        _connectAsync = connectAsync ?? ((socket, uri, ct) => socket.ConnectAsync(uri, ct));
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
        if (_ws != null)
        {
            using var closeCts = new CancellationTokenSource(WebSocketCloseTimeout);
            var closeTask = CloseWebSocketGracefully(closeCts.Token);
            try
            {
                closeTask.GetAwaiter().GetResult();
            }
            catch (OperationCanceledException)
            {
            }
        }
        _ws?.Dispose();
        _ws = null;
        _cts?.Dispose();
        _cts = null;
        _tokenValid = false;
        _task = null;
        ResetState();
    }

    public void Dispose() => Stop();

    private void ResetState()
    {
        lock (_stateLock)
        {
            _subs.Clear();
            _channelMetadata.Clear();
            _cursors.Clear();
            _acked.Clear();
            _lastDisconnectLog = default;
            _lastErrorSignature = null;
            _lastErrorLog = default;
            _lastSubscribeMismatchSignature = null;
            _lastSubscribeMismatchLog = default;
            _lastBatchDropSignature = null;
            _lastBatchDropLog = default;
        }
        _reconnectAttempt = 0;
        _connectCount = 0;
        _reconnectCount = 0;
        _resyncCount = 0;
        _backfillTotal = 0;
        _backfillBatches = 0;
        _ackFrameCount = 0;
        _sendCount = 0;
        _sendLatencyTotal = 0;
        _disconnectCount = 0;
        _connectedSince = default;
    }

    private void ShowPermissionWarning()
    {
        if (_permissionWarningShown)
        {
            return;
        }

        _permissionWarningShown = true;
        OnFramework(() =>
        {
            PluginServices.Instance?.ToastGui.ShowError(ForbiddenMessage);
        });
    }

    private void ResetPermissionWarning()
    {
        _permissionWarningShown = false;
    }

    public bool IsReady() => _tokenValid && _ws?.State == WebSocketState.Open;

    public void Subscribe(string channel, string? guildId, string? kind)
    {
        if (string.IsNullOrEmpty(channel)) return;
        string key;
        lock (_stateLock)
        {
            key = RegisterChannelMetadataUnsafe(channel, guildId, kind);
            if (!string.IsNullOrEmpty(key))
            {
                _subs.Add(key);
            }
        }
        ValidateSubscription(channel, guildId, kind);
        PluginServices.Instance?.Log.Info($"chat.ws subscribe channel={channel}");
        _ = SendSubscriptions();
    }

    public void Unsubscribe(string channel)
    {
        if (string.IsNullOrEmpty(channel)) return;
        string key;
        var removed = false;
        lock (_stateLock)
        {
            key = KeyForChannelUnsafe(channel);
            _channelMetadata.Remove(channel);
            if (!string.IsNullOrEmpty(key))
            {
                removed = _subs.Remove(key);
            }
        }
        if (removed)
        {
            PluginServices.Instance?.Log.Info($"chat.ws unsubscribe channel={channel}");
            _ = SendSubscriptions();
        }
    }

    public void Ack(string channel, string? guildId, string? kind)
    {
        if (!string.IsNullOrEmpty(channel))
        {
            lock (_stateLock)
            {
                if (_channelMetadata.ContainsKey(channel))
                {
                    UpdateChannelMetadataUnsafe(channel, guildId, kind);
                }
                else
                {
                    RegisterChannelMetadataUnsafe(channel, guildId, kind);
                }
            }
        }
        _ = AckAsync(channel);
    }

    public Task Send(string channel, object payload)
    {
        var json = JsonSerializer.Serialize(new { op = "send", channel, d = payload });
        return SendRaw(json);
    }

    public void Resync(string channel)
    {
        var json = JsonSerializer.Serialize(new { op = "resync", channel });
        PluginServices.Instance?.Log.Info($"chat.ws resync channel={channel}");
        _resyncCount++;
        _ = SendRaw(json);
    }

    private static string Key(string? guildId, string? kind, string channelId)
        => ChannelKeyHelper.BuildCursorKey(guildId, kind, channelId);

    private string KeyForChannel(string channel)
    {
        lock (_stateLock)
        {
            return KeyForChannelUnsafe(channel);
        }
    }

    private string KeyForChannelUnsafe(string channel)
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
        lock (_stateLock)
        {
            return RegisterChannelMetadataUnsafe(channel, guildId, kind);
        }
    }

    private string RegisterChannelMetadataUnsafe(string channel, string? guildId, string? kind)
    {
        if (string.IsNullOrEmpty(channel)) return string.Empty;
        var normalizedGuild = ChannelKeyHelper.NormalizeGuildId(guildId);
        var normalizedKind = ChannelKeyHelper.NormalizeKind(kind);
        return StoreChannelMetadataUnsafe(channel, normalizedGuild, normalizedKind);
    }

    private string StoreChannelMetadataUnsafe(string channel, string normalizedGuild, string normalizedKind)
    {
        if (_channelMetadata.TryGetValue(channel, out var existing))
        {
            var defaultGuild = ChannelKeyHelper.NormalizeGuildId(null);
            var effectiveGuild = normalizedGuild;
            if (string.Equals(normalizedGuild, defaultGuild, StringComparison.Ordinal) &&
                !string.Equals(existing.GuildId, defaultGuild, StringComparison.Ordinal))
            {
                effectiveGuild = existing.GuildId;
            }

            var effectiveKind = normalizedKind;
            if (string.IsNullOrEmpty(normalizedKind) && !string.IsNullOrEmpty(existing.Kind))
            {
                effectiveKind = existing.Kind;
            }

            var oldKey = Key(existing.GuildId, existing.Kind, channel);
            var newKey = Key(effectiveGuild, effectiveKind, channel);
            _channelMetadata[channel] = (effectiveGuild, effectiveKind);
            if (!string.Equals(oldKey, newKey, StringComparison.Ordinal))
            {
                UpdateSubscriptionKeyUnsafe(oldKey, newKey);
            }
            return newKey;
        }
        else
        {
            var newKey = Key(normalizedGuild, normalizedKind, channel);
            _channelMetadata[channel] = (normalizedGuild, normalizedKind);
            return newKey;
        }
    }

    private void UpdateSubscriptionKeyUnsafe(string oldKey, string newKey)
    {
        if (_subs.Remove(oldKey))
        {
            _subs.Add(newKey);
        }

        if (_cursors.TryGetValue(oldKey, out var cursor))
        {
            _cursors.Remove(oldKey);
            _cursors[newKey] = cursor;
        }

        if (_acked.TryGetValue(oldKey, out var acked))
        {
            _acked.Remove(oldKey);
            _acked[newKey] = acked;
        }

        if (_config.ChatCursors.TryGetValue(oldKey, out var stored))
        {
            _config.ChatCursors.Remove(oldKey);
            _config.ChatCursors[newKey] = stored;
        }
    }

    private void UpdateChannelMetadata(string channel, string? guildId, string? kind)
    {
        lock (_stateLock)
        {
            UpdateChannelMetadataUnsafe(channel, guildId, kind);
        }
    }

    private void UpdateChannelMetadataUnsafe(string channel, string? guildId, string? kind)
    {
        if (string.IsNullOrEmpty(channel)) return;

        var hasGuild = !string.IsNullOrWhiteSpace(guildId);
        var hasKind = !string.IsNullOrWhiteSpace(kind);
        if (!hasGuild && !hasKind) return;

        if (_channelMetadata.TryGetValue(channel, out var existing))
        {
            var normalizedGuild = hasGuild ? ChannelKeyHelper.NormalizeGuildId(guildId) : existing.GuildId;
            var normalizedKind = hasKind ? ChannelKeyHelper.NormalizeKind(kind) : existing.Kind;
            StoreChannelMetadataUnsafe(channel, normalizedGuild, normalizedKind);
        }
        else if (hasGuild && hasKind)
        {
            RegisterChannelMetadataUnsafe(channel, guildId, kind);
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
        var shouldLog = false;
        lock (_stateLock)
        {
            if (_lastSubscribeMismatchSignature == signature && (now - _lastSubscribeMismatchLog) < SubscribeMismatchThrottle)
            {
                return;
            }

            _lastSubscribeMismatchSignature = signature;
            _lastSubscribeMismatchLog = now;
            shouldLog = true;
        }

        if (shouldLog)
        {
            PluginServices.Instance?.Log.Warning($"chat.ws subscribe mismatch channel={channel} guild={guildId} kind={kind} selected={selected}");
        }
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

        lock (_stateLock)
        {
            if (_channelMetadata.TryGetValue(channel, out var meta))
            {
                var defaultGuildSentinel = ChannelKeyHelper.NormalizeGuildId(null);
                var hasStoredGuild = !string.IsNullOrEmpty(meta.GuildId) &&
                    !string.Equals(meta.GuildId, defaultGuildSentinel, StringComparison.Ordinal);
                var hasStoredKind = !string.IsNullOrEmpty(meta.Kind);
                var guildMismatch = hasStoredGuild && !string.Equals(meta.GuildId, normalizedGuild, StringComparison.Ordinal);
                var kindMismatch = hasStoredKind && !string.Equals(meta.Kind, normalizedKind, StringComparison.Ordinal);
                if (guildMismatch || kindMismatch)
                {
                    LogBatchDrop(op, channel, normalizedGuild, normalizedKind, "metadata_mismatch", meta.GuildId, meta.Kind, null);
                    return false;
                }
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
        var shouldLog = false;
        lock (_stateLock)
        {
            if (_lastBatchDropSignature == signature && (now - _lastBatchDropLog) < BatchDropThrottle)
            {
                return;
            }

            _lastBatchDropSignature = signature;
            _lastBatchDropLog = now;
            shouldLog = true;
        }

        if (shouldLog)
        {
            PluginServices.Instance?.Log.Warning(message);
        }
    }

    private async Task Run(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            if (!ApiHelpers.ValidateApiBaseUrl(_config))
            {
                OnFramework(() => StatusChanged?.Invoke("Invalid API URL"));
                await DelayWithJitter(5, token);
                continue;
            }

            if (!_tokenManager.IsReady())
            {
                await DelayWithJitter(5, token);
                continue;
            }

            var tokenStatus = await ValidateToken(token);
            if (tokenStatus != TokenValidationResult.Success)
            {
                if (tokenStatus == TokenValidationResult.Unauthorized)
                {
                    _tokenManager.Clear("Invalid API key");
                    ResetState();
                    OnFramework(() => Unlinked?.Invoke());
                    _tokenValid = false;
                    OnFramework(() => StatusChanged?.Invoke("Authentication failed"));
                }
                else if (tokenStatus == TokenValidationResult.Forbidden)
                {
                    PluginServices.Instance?.Log.Warning("Chat bridge forbidden during token validation; check API key/roles.");
                    ShowPermissionWarning();
                    ResetState();
                    OnFramework(() => Unlinked?.Invoke());
                    _tokenValid = false;
                    OnFramework(() => StatusChanged?.Invoke(ForbiddenMessage));
                }
                else
                {
                    OnFramework(() => StatusChanged?.Invoke("Authentication failed"));
                }

                await DelayWithJitter(5, token);
                continue;
            }

            var forbidden = false;
            Exception? transportError = null;
            var failureStage = "connect";
            var connected = false;
            var attemptedConnect = false;
            var connectTimedOut = false;
            CancellationTokenSource? connectCts = null;
            try
            {
                OnFramework(() => StatusChanged?.Invoke("Connecting..."));
                Uri? uri;
                try
                {
                    uri = _uriBuilder();
                }
                catch (Exception ex)
                {
                    LogConnectionException(ex, "uri");
                    OnFramework(() => StatusChanged?.Invoke("Invalid API URL"));
                    await DelayWithJitter(5, token);
                    continue;
                }

                if (!IsValidWebSocketUri(uri))
                {
                    LogConnectionException(new InvalidOperationException("Missing WebSocket URL"), "uri");
                    OnFramework(() => StatusChanged?.Invoke("Invalid API URL"));
                    await DelayWithJitter(5, token);
                    continue;
                }

                _ws?.Dispose();
                _ws = _webSocketFactory();
                _ws.Options.DangerousDeflateOptions = new WebSocketDeflateOptions();
                _ws.Options.SetRequestHeader("Sec-WebSocket-Extensions", "permessage-deflate");
                ApiHelpers.AddAuthHeader(_ws, _tokenManager);
                attemptedConnect = true;
                connectCts = CancellationTokenSource.CreateLinkedTokenSource(token);
                connectCts.CancelAfter(_connectTimeout);
                await _connectAsync(_ws, uri!, connectCts.Token);
                _connectedSince = DateTime.UtcNow;
                _reconnectAttempt = 0;
                _tokenValid = true;
                connected = true;
                failureStage = "receive";
                OnFramework(() => Linked?.Invoke());
                OnFramework(() => StatusChanged?.Invoke(string.Empty));
                ResetPermissionWarning();
                _connectCount++;
                if (_connectCount > 1) _reconnectCount++;
                PluginServices.Instance?.Log.Info($"chat.ws connect count={_connectCount} reconnects={_reconnectCount}");

                await SendSubscriptions();

                await ReceiveLoop(token);
            }
            catch (OperationCanceledException ex) when (!ShouldRethrow(ex, token))
            {
                if (!token.IsCancellationRequested && connectCts?.IsCancellationRequested == true)
                {
                    connectTimedOut = true;
                    PluginServices.Instance?.Log.Warning($"chat.ws connect timed out after {_connectTimeout.TotalSeconds:0.#}s");
                }
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
                connectCts?.Dispose();
                var status = _ws?.CloseStatus;
                var description = _ws?.CloseStatusDescription;
                try
                {
                    await CloseWebSocketGracefully(token).ConfigureAwait(false);
                }
                catch (OperationCanceledException ex) when (!ShouldRethrow(ex, token))
                {
                }
                catch (OperationCanceledException)
                {
                }
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
                ShowPermissionWarning();
                ResetState();
                OnFramework(() => Unlinked?.Invoke());
                _tokenValid = false;
            }

            if ((DateTime.UtcNow - _connectedSince) > TimeSpan.FromSeconds(60))
            {
                _reconnectAttempt = 0;
            }
            _reconnectAttempt++;
            var backoff = GetReconnectDelay(_reconnectAttempt);
            var statusMessage = forbidden
                ? ForbiddenMessage
                : connectTimedOut
                    ? "Connection timed out; retrying..."
                    : $"Reconnecting in {backoff.TotalSeconds:0.#}s...";
            OnFramework(() => StatusChanged?.Invoke(statusMessage));
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
                    OnFramework(() => TemplatesUpdated?.Invoke());
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
                    string key;
                    lock (_stateLock)
                    {
                        key = RegisterChannelMetadataUnsafe(channel, guildId, kind);
                        if (string.IsNullOrEmpty(key))
                        {
                            key = KeyForChannelUnsafe(channel);
                        }
                    }

                    if (string.IsNullOrEmpty(key))
                    {
                        break;
                    }

                    var msgs = root.GetProperty("messages");
                    var deliveries = new List<string>(msgs.GetArrayLength());
                    var deleted = new List<string>();
                    var typings = new List<DiscordUserDto>();
                    var count = 0;
                    foreach (var msg in msgs.EnumerateArray())
                    {
                        count++;
                        var cursor = msg.GetProperty("cursor").GetInt64();
                        lock (_stateLock)
                        {
                            _cursors[key] = cursor;
                        }

                        var mOp = msg.GetProperty("op").GetString();
                        if (mOp == "mc" || mOp == "mu")
                        {
                            deliveries.Add(msg.GetProperty("d").GetRawText());
                        }
                        else if (mOp == "md")
                        {
                            var id = msg.GetProperty("d").GetProperty("id").GetString();
                            if (!string.IsNullOrEmpty(id))
                            {
                                deleted.Add(id!);
                            }
                        }
                        else if (mOp == "ty")
                        {
                            var payload = msg.GetProperty("d").GetRawText();
                            var author = JsonSerializer.Deserialize<DiscordUserDto>(payload, JsonOpts);
                            if (author != null)
                            {
                                typings.Add(author);
                            }
                        }
                    }
                    _backfillTotal += count;
                    _backfillBatches++;
                    var avg = _backfillBatches == 0 ? 0 : (double)_backfillTotal / _backfillBatches;
                    PluginServices.Instance?.Log.Info($"chat.ws batch channel={channel} size={count} avg_backfill={avg:F1}");

                    const int SliceSize = 100;
                    if (SpreadAcrossFrames)
                    {
                        DispatchInTicks(deliveries, SliceSize, p =>
                        {
                            var handler = MessageReceived;
                            handler?.Invoke(p);
                        });
                        DispatchInTicks(deleted, SliceSize, id =>
                        {
                            var handler = MessageReceived;
                            handler?.Invoke($"{\"deletedId\":\"{id}\"}");
                        });
                        DispatchInTicks(typings, SliceSize, a =>
                        {
                            var handler = TypingReceived;
                            handler?.Invoke(a);
                        });
                    }
                    else
                    {
                        for (var i = 0; i < deliveries.Count; i += SliceSize)
                        {
                            var slice = deliveries.GetRange(i, Math.Min(SliceSize, deliveries.Count - i));
                            OnFramework(() =>
                            {
                                foreach (var payload in slice)
                                {
                                    MessageReceived?.Invoke(payload);
                                }
                            });
                        }

                        for (var i = 0; i < deleted.Count; i += SliceSize)
                        {
                            var slice = deleted.GetRange(i, Math.Min(SliceSize, deleted.Count - i));
                            OnFramework(() =>
                            {
                                foreach (var id in slice)
                                {
                                    MessageReceived?.Invoke($"{{\"deletedId\":\"{id}\"}}");
                                }
                            });
                        }

                        for (var i = 0; i < typings.Count; i += SliceSize)
                        {
                            var slice = typings.GetRange(i, Math.Min(SliceSize, typings.Count - i));
                            OnFramework(() =>
                            {
                                foreach (var author in slice)
                                {
                                    TypingReceived?.Invoke(author);
                                }
                            });
                        }
                    }
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
                    var channelToResync = ch;
                    var cursorToResync = cur;
                    OnFramework(() => ResyncRequested?.Invoke(channelToResync, cursorToResync));
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

    private bool IsWebSocketOpen()
    {
#if TEST
        if (ForceWebSocketOpen.HasValue)
        {
            return ForceWebSocketOpen.Value;
        }
#endif
        return _ws != null && _ws.State == WebSocketState.Open;
    }

    private async Task SendRaw(string json)
    {
        var socketOpen = IsWebSocketOpen();
#if TEST
        SendRawInterceptor?.Invoke(json);
        if (ForceWebSocketOpen == true && _ws == null)
        {
            return;
        }
#endif
        if (!socketOpen)
            return;
        var socket = _ws;
        if (socket == null)
            return;
        var bytes = Encoding.UTF8.GetBytes(json);
        try
        {
            var sw = Stopwatch.StartNew();
            await socket.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, CancellationToken.None);
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

        long cursor;
        string key;
        lock (_stateLock)
        {
            key = KeyForChannelUnsafe(channel);
            if (string.IsNullOrEmpty(key)) return Task.CompletedTask;
            if (!_cursors.TryGetValue(key, out cursor)) return Task.CompletedTask;
            if (_acked.TryGetValue(key, out var acked) && acked == cursor) return Task.CompletedTask;
            _acked[key] = cursor;
            _config.ChatCursors[key] = cursor;
        }

        var json = JsonSerializer.Serialize(new { op = "ack", channel, cur = cursor });
        return SendRaw(json);
    }

    private Task SendSubscriptions()
    {
        if (!IsWebSocketOpen())
            return Task.CompletedTask;

        List<KeyValuePair<string, (string GuildId, string Kind)>> metaSnapshot;
        HashSet<string> subsSnapshot;
        Dictionary<string, long> cursorsSnapshot;

        lock (_stateLock)
        {
            metaSnapshot = _channelMetadata.ToList();
            subsSnapshot = new HashSet<string>(_subs, StringComparer.Ordinal);
            cursorsSnapshot = new Dictionary<string, long>(_config.ChatCursors);
        }

        var channels = new List<Dictionary<string, object?>>(metaSnapshot.Count);
        foreach (var kvp in metaSnapshot)
        {
            var channelId = kvp.Key;
            var meta = kvp.Value;
            var key = Key(meta.GuildId, meta.Kind, channelId);
            if (!subsSnapshot.Contains(key)) continue;

            cursorsSnapshot.TryGetValue(key, out var since);

            var channel = new Dictionary<string, object?>
            {
                ["id"] = channelId,
                ["since"] = since,
            };

            var normalizedGuild = ChannelKeyHelper.NormalizeGuildId(meta.GuildId);
            var defaultGuildSentinel = ChannelKeyHelper.NormalizeGuildId(null);
            if (!string.IsNullOrEmpty(normalizedGuild) &&
                !string.Equals(normalizedGuild, defaultGuildSentinel, StringComparison.Ordinal))
            {
                channel["guildId"] = normalizedGuild;
            }

            var normalizedKind = ChannelKeyHelper.NormalizeKind(meta.Kind);
            if (!string.IsNullOrEmpty(normalizedKind))
            {
                channel["kind"] = normalizedKind.ToLowerInvariant();
            }

            channels.Add(channel);
        }

        var json = JsonSerializer.Serialize(new { op = "sub", channels });
        PluginServices.Instance?.Log.Info($"chat.ws subscribe count={channels.Count}");
        return SendRaw(json);
    }

    private enum TokenValidationResult
    {
        Success,
        Unauthorized,
        Forbidden,
        Error
    }

    private async Task<TokenValidationResult> ValidateToken(CancellationToken token)
    {
        var pingService = PingService.Instance ?? new PingService(_httpClient, _config, _tokenManager);
        var response = await pingService.PingAsync(token);
        if (response == null)
        {
            return TokenValidationResult.Error;
        }

        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            PluginServices.Instance?.Log.Error("Backend ping endpoints missing. Please update or restart the backend.");
        }

        if (response.StatusCode == HttpStatusCode.Unauthorized)
        {
            return TokenValidationResult.Unauthorized;
        }

        if (response.StatusCode == HttpStatusCode.Forbidden)
        {
            return TokenValidationResult.Forbidden;
        }

        if (!response.IsSuccessStatusCode)
        {
            return TokenValidationResult.Error;
        }

        return TokenValidationResult.Success;
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
        var now = DateTime.UtcNow;
        long disconnectCount;
        var shouldLog = false;
        lock (_stateLock)
        {
            _disconnectCount++;
            disconnectCount = _disconnectCount;
            if (_disconnectCount > 1 && (now - _lastDisconnectLog) < DisconnectLogThrottle)
            {
                return;
            }

            _lastDisconnectLog = now;
            shouldLog = true;
        }

        if (!shouldLog)
        {
            return;
        }

        var statusText = status.HasValue ? $"{(int)status} {status}" : "n/a";
        var desc = string.IsNullOrWhiteSpace(description) ? string.Empty : $" desc={description}";
        PluginServices.Instance?.Log.Info($"chat.ws disconnect count={disconnectCount} status={statusText}{desc}");
    }

    private void LogConnectionException(Exception ex, string stage)
    {
        var now = DateTime.UtcNow;
        var signature = $"{stage}:{ex.GetType().FullName}:{ex.Message}";
        var shouldLog = false;
        lock (_stateLock)
        {
            if (_lastErrorSignature == signature && (now - _lastErrorLog) < ErrorLogThrottle)
            {
                return;
            }

            _lastErrorSignature = signature;
            _lastErrorLog = now;
            shouldLog = true;
        }

        if (shouldLog)
        {
            PluginServices.Instance?.Log.Error(ex, $"chat.ws {stage} failed");
        }
    }

    private async Task CloseWebSocketGracefully(CancellationToken token)
    {
        var socket = _ws;
        if (socket == null)
        {
            return;
        }

        WebSocketState state;
        try
        {
            state = socket.State;
        }
        catch (ObjectDisposedException)
        {
            return;
        }

        if (state != WebSocketState.Open && state != WebSocketState.CloseReceived && state != WebSocketState.CloseSent)
        {
            return;
        }

        if (state == WebSocketState.Open || state == WebSocketState.CloseReceived)
        {
            try
            {
                await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, string.Empty, token).ConfigureAwait(false);
            }
            catch (OperationCanceledException ex) when (!ShouldRethrow(ex, token))
            {
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (WebSocketException)
            {
            }
            catch (ObjectDisposedException)
            {
            }
            catch (InvalidOperationException)
            {
            }

            try
            {
                state = socket.State;
            }
            catch (ObjectDisposedException)
            {
                return;
            }
        }

        if (state != WebSocketState.CloseSent && state != WebSocketState.CloseReceived)
        {
            return;
        }

        try
        {
            await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, string.Empty, token).ConfigureAwait(false);
        }
        catch (OperationCanceledException ex) when (!ShouldRethrow(ex, token))
        {
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (WebSocketException)
        {
        }
        catch (ObjectDisposedException)
        {
        }
        catch (InvalidOperationException)
        {
        }
    }
}
