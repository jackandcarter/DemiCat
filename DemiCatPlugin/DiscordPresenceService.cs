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
    private ClientWebSocket? _ws;
    private Task? _wsTask;
    private CancellationTokenSource? _wsCts;
    private bool _loaded;
    private string _statusMessage = string.Empty;
    private int _retryAttempt;
    private string? _lastErrorSignature;
    private DateTime _lastErrorLog;
    private static readonly TimeSpan ErrorLogThrottle = TimeSpan.FromSeconds(30);

    public IReadOnlyList<PresenceDto> Presences => _presences;
    public string StatusMessage => _statusMessage;
    public bool Loaded => _loaded;

    public DiscordPresenceService(Config config, HttpClient httpClient)
    {
        _config = config;
        _httpClient = httpClient;
    }

    /// <summary>
    /// Clears any cached presence information. Used when reconnecting to the
    /// presence stream so that a full refresh can be performed.
    /// </summary>
    public void Reload()
    {
        _loaded = false;
        _presences.Clear();
    }

    /// <summary>
    /// Starts the background WebSocket listener which will also trigger an
    /// initial refresh of the presence list.
    /// </summary>
    public void Reset()
    {
        _loaded = false;
        _retryAttempt = 0;
        _wsCts?.Cancel();
        _ws?.Dispose();
        _ws = null;
        _wsCts = new CancellationTokenSource();
        _wsTask = RunWebSocket(_wsCts.Token);
    }

    public void Stop()
    {
        _wsCts?.Cancel();
        _ws?.Dispose();
        _ws = null;
    }

    public void Dispose()
    {
        _wsCts?.Cancel();
        _ws?.Dispose();
    }

    public async Task Refresh()
    {
        if (!ApiHelpers.ValidateApiBaseUrl(_config) || TokenManager.Instance?.IsReady() != true)
        {
            PluginServices.Instance!.Log.Warning("Cannot refresh presences: API base URL is not configured or token missing.");
            _ = PluginServices.Instance!.Framework.RunOnTick(() => _statusMessage = "Invalid API URL");
            return;
        }

        try
        {
            var request = new HttpRequestMessage(HttpMethod.Get, $"{_config.ApiBaseUrl.TrimEnd('/')}/api/users");
            ApiHelpers.AddAuthHeader(request, TokenManager.Instance!);
            var response = await _httpClient.SendAsync(request);
            if (!response.IsSuccessStatusCode)
            {
                return;
            }
            var stream = await response.Content.ReadAsStreamAsync();
            var list = await JsonSerializer.DeserializeAsync<List<PresenceDto>>(stream) ?? new List<PresenceDto>();
            _ = PluginServices.Instance!.Framework.RunOnTick(() =>
            {
                _presences.Clear();
                _presences.AddRange(list);
            });
            _loaded = true;
        }
        catch
        {
            // ignore
        }
    }

    private async Task RunWebSocket(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            if (!ApiHelpers.ValidateApiBaseUrl(_config) || TokenManager.Instance?.IsReady() != true || !_config.Enabled)
            {
                _ = PluginServices.Instance!.Framework.RunOnTick(() =>
                    _statusMessage = "Invalid API URL");
                await DelayWithBackoff(TimeSpan.FromSeconds(5), token);
                _retryAttempt = 0;
                continue;
            }

            var hadTransportError = true;
            try
            {
                var pingService = PingService.Instance ?? new PingService(_httpClient, _config, TokenManager.Instance!);
                var pingResponse = await pingService.PingAsync(token);
                if (pingResponse?.IsSuccessStatusCode != true)
                {
                    if (pingResponse?.StatusCode == HttpStatusCode.NotFound)
                    {
                        PluginServices.Instance!.Log.Error("Backend ping endpoints missing. Please update or restart the backend.");
                    }
                    await DelayWithBackoff(TimeSpan.FromSeconds(5), token);
                    _retryAttempt = 0;
                    continue;
                }
                _ws?.Dispose();
                _ws = new ClientWebSocket();
                ApiHelpers.AddAuthHeader(_ws, TokenManager.Instance!);
                Uri? uri;
                try
                {
                    uri = BuildWebSocketUri();
                }
                catch (Exception ex)
                {
                    LogConnectionException(ex, "uri");
                    await DelayWithBackoff(TimeSpan.FromSeconds(5), token);
                    _retryAttempt = 0;
                    continue;
                }

                if (!IsValidWebSocketUri(uri))
                {
                    LogConnectionException(new InvalidOperationException("Missing WebSocket URL"), "uri");
                    await DelayWithBackoff(TimeSpan.FromSeconds(5), token);
                    _retryAttempt = 0;
                    continue;
                }

                await _ws.ConnectAsync(uri!, token);
                _retryAttempt = 0;
                hadTransportError = false;
                _loaded = false;
                await Refresh();
                _ = PluginServices.Instance!.Framework.RunOnTick(() => _statusMessage = string.Empty);
                var buffer = new byte[1024];
                while (_ws.State == WebSocketState.Open && !token.IsCancellationRequested)
                {
                    using var ms = new MemoryStream();
                    WebSocketReceiveResult result;
                    do
                    {
                        result = await _ws.ReceiveAsync(new ArraySegment<byte>(buffer), token);
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
                        {
                            var idx = _presences.FindIndex(p => p.Id == dto.Id);
                            if (idx >= 0)
                            {
                                var existing = _presences[idx];
                                dto.AvatarUrl ??= existing.AvatarUrl;
                                dto.AvatarTexture = existing.AvatarTexture;
                                if (dto.Roles.Count == 0)
                                    dto.Roles = existing.Roles;
                                _presences[idx] = dto;
                            }
                            else
                            {
                                _presences.Add(dto);
                            }
                        });
                    }
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
                _ws?.Dispose();
                _ws = null;
            }

            if (token.IsCancellationRequested)
                break;

            if (hadTransportError)
            {
                _retryAttempt++;
                var delay = GetRetryDelay(_retryAttempt);
                _ = PluginServices.Instance!.Framework.RunOnTick(() =>
                    _statusMessage = $"Reconnecting in {delay.TotalSeconds:0.#}s...");
                await DelayWithBackoff(delay, token);
            }
            else
            {
                _retryAttempt = 0;
                _ = PluginServices.Instance!.Framework.RunOnTick(() =>
                    _statusMessage = "Reconnecting...");
                await DelayWithBackoff(TimeSpan.FromSeconds(5), token);
            }
        }
    }

    private void HandleConnectionException(Exception ex)
    {
        LogConnectionException(ex, "connect");
        _ = PluginServices.Instance!.Framework.RunOnTick(() =>
            _statusMessage = $"Connection failed: {ex.Message}");
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

