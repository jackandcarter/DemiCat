using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.IO;

namespace DemiCatPlugin;

/// <summary>
/// Service responsible for tracking Discord user presences. It handles
/// refreshing the current list of users via HTTP and receiving incremental
/// updates over a WebSocket connection. The resulting collection exposes each
/// user's roles and online status for any interested UI components.
/// </summary>
public class DiscordPresenceService : IDisposable
{
    private readonly Config _config;
    private readonly HttpClient _httpClient;
    private readonly List<PresenceDto> _presences = new();
    private readonly SemaphoreSlim _resetGate = new(1, 1);
    private readonly object _refreshSync = new();
    private ClientWebSocket? _ws;
    private Task? _wsTask;
    private CancellationTokenSource? _wsCts;
    private bool _loaded;
    private volatile bool _presenceReady = true;
    private string _statusMessage = string.Empty;
    private int _retryAttempt;
    private string? _lastErrorSignature;
    private DateTime _lastErrorLog;
    private Task<RefreshOutcome>? _refreshTask;
    private DateTime _nextRefreshAllowed = DateTime.MinValue;
    private static readonly TimeSpan ErrorLogThrottle = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan RefreshSuccessCooldown = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan RefreshFailureCooldown = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan RefreshRateLimitFallback = TimeSpan.FromSeconds(20);
    private const string InvalidApiStatus = "Invalid API URL";
    private const string ApiKeyMissingStatus = "API key not configured";
    private const string PluginDisabledStatus = "Plugin disabled";

    public IReadOnlyList<PresenceDto> Presences => _presences;
    public string StatusMessage => _statusMessage;
    public bool Loaded => _loaded;
    public bool IsPresenceReady => _presenceReady;

    public DiscordPresenceService(Config config, HttpClient httpClient)
    {
        _config = config;
        _httpClient = httpClient;
    }

    public void SetPresenceReady(bool ready)
        => _presenceReady = ready;

    /// <summary>
    /// Clears any cached presence information. Used when reconnecting to the
    /// presence stream so that a full refresh can be performed.
    /// </summary>
    public void Reload()
    {
        _loaded = false;
        _nextRefreshAllowed = DateTime.MinValue;
        DisposeAllPresences();
    }

    /// <summary>
    /// Starts the background WebSocket listener which will also trigger an
    /// initial refresh of the presence list.
    /// </summary>
    public void Reset()
    {
        _ = ResetAsync();
    }

    private async Task ResetAsync()
    {
        try
        {
            await _resetGate.WaitAsync().ConfigureAwait(false);
            try
            {
                _loaded = false;
                _retryAttempt = 0;

                var previousTask = Interlocked.Exchange(ref _wsTask, null);
                var previousCts = Interlocked.Exchange(ref _wsCts, null);
                var previousSocket = Interlocked.Exchange(ref _ws, null);

                previousCts?.Cancel();

                if (previousTask != null)
                {
                    try
                    {
                        await previousTask.ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                    }
                    catch (ObjectDisposedException)
                    {
                    }
                    catch (Exception ex)
                    {
                        PluginServices.Instance?.Log.Debug("presence reset wait", ex);
                    }
                }

                previousSocket?.Dispose();
                previousCts?.Dispose();

                var cts = new CancellationTokenSource();
                _wsCts = cts;
                var runTask = RunWebSocket(cts.Token);
                _wsTask = runTask;
            }
            finally
            {
                _resetGate.Release();
            }
        }
        catch (Exception ex)
        {
            PluginServices.Instance?.Log.Error(ex, "Failed to reset presence service.");
        }
    }

    public void Stop()
    {
        var cts = Interlocked.Exchange(ref _wsCts, null);
        cts?.Cancel();
        cts?.Dispose();

        var socket = Interlocked.Exchange(ref _ws, null);
        socket?.Dispose();

        DisposeAllPresences();
    }

    public void Dispose()
    {
        var cts = Interlocked.Exchange(ref _wsCts, null);
        cts?.Cancel();
        cts?.Dispose();

        var socket = Interlocked.Exchange(ref _ws, null);
        socket?.Dispose();

        DisposeAllPresences();
    }

    public Task Refresh(bool force = false)
    {
        lock (_refreshSync)
        {
            if (_refreshTask != null)
            {
                return _refreshTask;
            }

            if (!force)
            {
                if (DateTime.UtcNow < _nextRefreshAllowed)
                {
                    return Task.CompletedTask;
                }
            }

            var task = RefreshInternalAsync();
            _refreshTask = task;

            if (task.IsCompleted)
            {
                FinalizeRefreshTask(task);
            }
            else
            {
                task.ContinueWith(FinalizeRefreshTask, TaskScheduler.Default);
            }

            return task;
        }
    }

    private async Task<RefreshOutcome> RefreshInternalAsync()
    {
        if (!ApiHelpers.ValidateApiBaseUrl(_config))
        {
            PluginServices.Instance!.Log.Warning("Cannot refresh presences: API base URL is not configured.");
            UpdateStatusMessage(InvalidApiStatus);
            return RefreshOutcome.Failure(RefreshFailureCooldown);
        }

        if (TokenManager.Instance?.IsReady() != true)
        {
            PluginServices.Instance!.Log.Warning("Cannot refresh presences: API key is not configured.");
            UpdateStatusMessage(ApiKeyMissingStatus);
            return RefreshOutcome.Failure(RefreshFailureCooldown);
        }

        try
        {
            var request = new HttpRequestMessage(HttpMethod.Get, $"{_config.ApiBaseUrl.TrimEnd('/')}/api/users");
            ApiHelpers.AddAuthHeader(request, TokenManager.Instance!);
            using var response = await _httpClient.SendAsync(request).ConfigureAwait(false);
            if (response.StatusCode == (HttpStatusCode)429)
            {
                var delay = ParseRetryAfter(response) ?? RefreshRateLimitFallback;
                UpdateStatusMessage($"Rate limited. Retrying in {delay.TotalSeconds:0.#}s...");
                return RefreshOutcome.Failure(delay);
            }

            if (!response.IsSuccessStatusCode)
            {
                UpdateStatusMessage($"Presence refresh failed ({(int)response.StatusCode})");
                return RefreshOutcome.Failure(RefreshFailureCooldown);
            }

            var stream = await response.Content.ReadAsStreamAsync().ConfigureAwait(false);
            var list = await JsonSerializer.DeserializeAsync<List<PresenceDto>>(stream).ConfigureAwait(false) ?? new List<PresenceDto>();
            _ = PluginServices.Instance!.Framework.RunOnTick(() =>
            {
                DisposePresencesUnlocked(_presences);
                _presences.Clear();
                _presences.AddRange(list);
            });
            _loaded = true;
            UpdateStatusMessage(string.Empty);
            return RefreshOutcome.Success(RefreshSuccessCooldown);
        }
        catch (Exception ex)
        {
            PluginServices.Instance?.Log.Debug("presence.refresh", ex);
            UpdateStatusMessage("Presence refresh failed");
            return RefreshOutcome.Failure(RefreshFailureCooldown);
        }
    }

    private void FinalizeRefreshTask(Task<RefreshOutcome> task)
    {
        RefreshOutcome outcome;

        if (task.IsFaulted)
        {
            outcome = RefreshOutcome.Failure(RefreshFailureCooldown);
        }
        else if (task.IsCanceled)
        {
            outcome = RefreshOutcome.Failure(RefreshFailureCooldown);
        }
        else
        {
            outcome = task.Result;
        }

        lock (_refreshSync)
        {
            if (ReferenceEquals(_refreshTask, task))
            {
                _refreshTask = null;
                var delay = outcome.Delay;
                _nextRefreshAllowed = DateTime.UtcNow + delay;
            }
        }
    }

    private static TimeSpan? ParseRetryAfter(HttpResponseMessage response)
    {
        if (response.Headers.TryGetValues("Retry-After", out var values))
        {
            foreach (var value in values)
            {
                if (double.TryParse(value, out var seconds) && seconds > 0)
                {
                    return TimeSpan.FromSeconds(seconds);
                }
                if (DateTimeOffset.TryParse(value, out var retryTime))
                {
                    var delta = retryTime - DateTimeOffset.UtcNow;
                    if (delta > TimeSpan.Zero)
                    {
                        return delta;
                    }
                }
            }
        }

        return null;
    }

    private async Task RunWebSocket(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            if (!ApiHelpers.ValidateApiBaseUrl(_config))
            {
                UpdateStatusMessage(InvalidApiStatus);
                await DelayWithBackoff(TimeSpan.FromSeconds(5), token).ConfigureAwait(false);
                _retryAttempt = 0;
                continue;
            }

            if (TokenManager.Instance?.IsReady() != true)
            {
                UpdateStatusMessage(ApiKeyMissingStatus);
                await DelayWithBackoff(TimeSpan.FromSeconds(5), token).ConfigureAwait(false);
                _retryAttempt = 0;
                continue;
            }

            if (!_config.Enabled)
            {
                UpdateStatusMessage(PluginDisabledStatus);
                await DelayWithBackoff(TimeSpan.FromSeconds(5), token).ConfigureAwait(false);
                _retryAttempt = 0;
                continue;
            }

            var hadTransportError = true;
            ClientWebSocket? socket = null;
            IDisposable? connectionScope = null;

            try
            {
                var pingService = PingService.Instance ?? new PingService(_httpClient, _config, TokenManager.Instance!);
                var pingResponse = await pingService.PingAsync(token).ConfigureAwait(false);
                if (pingResponse?.IsSuccessStatusCode != true)
                {
                    if (pingResponse?.StatusCode == HttpStatusCode.NotFound)
                    {
                        PluginServices.Instance!.Log.Error("Backend ping endpoints missing. Please update or restart the backend.");
                    }
                    await DelayWithBackoff(TimeSpan.FromSeconds(5), token).ConfigureAwait(false);
                    _retryAttempt = 0;
                    continue;
                }

                socket = CreateClientWebSocket();
                ApiHelpers.AddAuthHeader(socket, TokenManager.Instance!);
                Uri? uri;
                try
                {
                    uri = BuildWebSocketUri();
                }
                catch (Exception ex)
                {
                    LogConnectionException(ex, "uri");
                    await DelayWithBackoff(TimeSpan.FromSeconds(5), token).ConfigureAwait(false);
                    _retryAttempt = 0;
                    continue;
                }

                if (!IsValidWebSocketUri(uri))
                {
                    LogConnectionException(new InvalidOperationException("Missing WebSocket URL"), "uri");
                    await DelayWithBackoff(TimeSpan.FromSeconds(5), token).ConfigureAwait(false);
                    _retryAttempt = 0;
                    continue;
                }

                connectionScope = EnterConnectionScope();
                await ConnectAsync(socket, uri!, token).ConfigureAwait(false);

                var previousSocket = Interlocked.Exchange(ref _ws, socket);
                previousSocket?.Dispose();

                _retryAttempt = 0;
                hadTransportError = false;
                _loaded = false;
                await Refresh(force: true).ConfigureAwait(false);
                UpdateStatusMessage(string.Empty);

                await ReceiveLoopAsync(socket, token).ConfigureAwait(false);
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
                hadTransportError = true;
                HandleConnectionException(ex);
            }
            catch (WebSocketException ex)
            {
                hadTransportError = true;
                HandleConnectionException(ex);
            }
            catch (IOException ex)
            {
                hadTransportError = true;
                HandleConnectionException(ex);
            }
            catch (Exception ex)
            {
                hadTransportError = true;
                HandleConnectionException(ex);
            }
            finally
            {
                connectionScope?.Dispose();

                if (socket != null)
                {
                    if (Interlocked.CompareExchange(ref _ws, null, socket) == socket)
                    {
                        socket.Dispose();
                    }
                    else
                    {
                        socket.Dispose();
                    }
                }
                else
                {
                    var leftover = Interlocked.Exchange(ref _ws, null);
                    leftover?.Dispose();
                }
            }

            if (token.IsCancellationRequested)
                break;

            if (hadTransportError)
            {
                _retryAttempt++;
                var delay = GetRetryDelay(_retryAttempt);
                UpdateStatusMessage($"Reconnecting in {delay.TotalSeconds:0.#}s...");
                await DelayWithBackoff(delay, token).ConfigureAwait(false);
            }
            else
            {
                _retryAttempt = 0;
                UpdateStatusMessage("Reconnecting...");
                await DelayWithBackoff(TimeSpan.FromSeconds(5), token).ConfigureAwait(false);
            }
        }
    }

    protected virtual ClientWebSocket CreateClientWebSocket() => new();

    protected virtual Task ConnectAsync(ClientWebSocket socket, Uri uri, CancellationToken token)
        => socket.ConnectAsync(uri, token);

    protected virtual Task ReceiveLoopAsync(ClientWebSocket socket, CancellationToken token)
        => ReceiveLoopCoreAsync(socket, token);

    protected virtual IDisposable? EnterConnectionScope() => null;

    private async Task ReceiveLoopCoreAsync(ClientWebSocket socket, CancellationToken token)
    {
        var buffer = new byte[1024];
        while (socket.State == WebSocketState.Open && !token.IsCancellationRequested)
        {
            using var ms = new MemoryStream();
            WebSocketReceiveResult result;
            do
            {
                result = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), token).ConfigureAwait(false);
                if (result.MessageType == WebSocketMessageType.Close)
                {
                    break;
                }

                ms.Write(buffer, 0, result.Count);

                if (result.Count == buffer.Length)
                {
                    Array.Resize(ref buffer, buffer.Length * 2);
                }

            } while (!result.EndOfMessage);

            if (result.MessageType == WebSocketMessageType.Close)
            {
                break;
            }

            var json = Encoding.UTF8.GetString(ms.ToArray());
            PresenceDto? dto = null;
            try
            {
                dto = JsonSerializer.Deserialize<PresenceDto>(json);
            }
            catch
            {
                // ignore
            }
            if (dto != null)
            {
                _ = PluginServices.Instance!.Framework.RunOnTick(() =>
                    ApplyPresenceUpdate(dto));
            }
        }
    }

    internal void ApplyPresenceUpdate(PresenceDto dto)
    {
        var idx = _presences.FindIndex(p => p.Id == dto.Id);
        if (idx >= 0)
        {
            var existing = _presences[idx];
            dto.AvatarUrl ??= existing.AvatarUrl;
            dto.AvatarTexture ??= existing.AvatarTexture;
            var existingBannerUrl = existing.BannerUrl;
            if (string.IsNullOrWhiteSpace(dto.BannerUrl) && !string.IsNullOrWhiteSpace(existingBannerUrl))
            {
                dto.BannerUrl = existingBannerUrl;
            }
            else if (!string.IsNullOrWhiteSpace(dto.BannerUrl) && !string.IsNullOrWhiteSpace(existingBannerUrl) &&
                     !string.Equals(dto.BannerUrl, existingBannerUrl, StringComparison.Ordinal))
            {
                dto.BannerTexture = null;
            }
            if (dto.BannerTexture == null && string.Equals(dto.BannerUrl, existingBannerUrl, StringComparison.Ordinal))
            {
                dto.BannerTexture = existing.BannerTexture;
            }
            if (!dto.AccentColorValue.HasValue && existing.AccentColorValue.HasValue)
                dto.AccentColorValue = existing.AccentColorValue;
            if (dto.Roles.Count == 0)
                dto.Roles = existing.Roles;
            if (dto.RoleDetails.Count == 0 && existing.RoleDetails.Count > 0)
                dto.RoleDetails = existing.RoleDetails;
            if (string.IsNullOrWhiteSpace(dto.StatusText) && !string.IsNullOrWhiteSpace(existing.StatusText))
                dto.StatusText = existing.StatusText;
            _presences[idx] = dto;
        }
        else
        {
            _presences.Add(dto);
        }
    }

    private static void DisposePresenceTextures(PresenceDto presence)
    {
        try
        {
            if (presence.AvatarTexture?.GetWrapOrEmpty() is IDisposable avatar)
            {
                avatar.Dispose();
            }
        }
        catch (ObjectDisposedException)
        {
        }
        finally
        {
            presence.AvatarTexture = null;
        }

        try
        {
            if (presence.BannerTexture?.GetWrapOrEmpty() is IDisposable banner)
            {
                banner.Dispose();
            }
        }
        catch (ObjectDisposedException)
        {
        }
        finally
        {
            presence.BannerTexture = null;
        }
    }

    private void DisposePresencesUnlocked(IEnumerable<PresenceDto> presences)
    {
        foreach (var presence in presences)
        {
            if (presence != null)
            {
                DisposePresenceTextures(presence);
            }
        }
    }

    private void DisposeAllPresences()
    {
        var framework = PluginServices.Instance?.Framework;
        if (framework == null)
        {
            DisposePresencesUnlocked(_presences);
            _presences.Clear();
            return;
        }

        framework.RunOnTick(() =>
        {
            DisposePresencesUnlocked(_presences);
            _presences.Clear();
        });
    }

    private readonly struct RefreshOutcome
    {
        public RefreshOutcome(bool success, TimeSpan delay)
        {
            Succeeded = success;
            Delay = delay > TimeSpan.Zero ? delay : TimeSpan.Zero;
        }

        public bool Succeeded { get; }
        public TimeSpan Delay { get; }

        public static RefreshOutcome Success(TimeSpan delay) => new(true, delay);
        public static RefreshOutcome Failure(TimeSpan delay) => new(false, delay);
    }

    private void HandleConnectionException(Exception ex)
    {
        LogConnectionException(ex, "connect");
        UpdateStatusMessage($"Connection failed: {ex.Message}");
    }

    private void UpdateStatusMessage(string message)
        => _ = PluginServices.Instance!.Framework.RunOnTick(() => _statusMessage = message);

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

    private static TimeSpan GetRetryDelay(int attempt)
    {
        var cappedAttempt = Math.Max(1, attempt);
        var baseDelay = Math.Min(15d, Math.Pow(2, cappedAttempt - 1));
        var min = Math.Max(0.5d, baseDelay / 2d);
        var max = Math.Max(min, baseDelay);
        var jitter = min + (max - min) * Random.Shared.NextDouble();
        return TimeSpan.FromSeconds(jitter);
    }

    private static bool ShouldRethrow(OperationCanceledException _, CancellationToken token)
        => token.CanBeCanceled && token.IsCancellationRequested;

    private static bool IsValidWebSocketUri(Uri? uri)
    {
        if (uri == null || !uri.IsAbsoluteUri)
            return false;

        if (string.IsNullOrWhiteSpace(uri.ToString()))
            return false;

        return string.Equals(uri.Scheme, "ws", StringComparison.OrdinalIgnoreCase)
            || string.Equals(uri.Scheme, "wss", StringComparison.OrdinalIgnoreCase);
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
        PluginServices.Instance!.Log.Error(ex, $"presence.ws {stage} failed");
    }

    private Uri BuildWebSocketUri()
    {
        var baseUri = _config.ApiBaseUrl.TrimEnd('/') + "/ws/presences";
        var builder = new UriBuilder(baseUri);
        if (builder.Scheme == "https") builder.Scheme = "wss";
        else if (builder.Scheme == "http") builder.Scheme = "ws";
        return builder.Uri;
    }
}

