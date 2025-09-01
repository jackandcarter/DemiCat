using System;
using System.Collections.Generic;
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
        _wsCts?.Cancel();
        _ws?.Dispose();
        _ws = null;
        _wsCts = new CancellationTokenSource();
        _wsTask = RunWebSocket(_wsCts.Token);
    }

    public void Dispose()
    {
        _wsCts?.Cancel();
        _ws?.Dispose();
    }

    public async Task Refresh()
    {
        if (!ApiHelpers.ValidateApiBaseUrl(_config))
        {
            PluginServices.Instance!.Log.Warning("Cannot refresh presences: API base URL is not configured.");
            _ = PluginServices.Instance!.Framework.RunOnTick(() => _statusMessage = "Invalid API URL");
            return;
        }

        try
        {
            var request = new HttpRequestMessage(HttpMethod.Get, $"{_config.ApiBaseUrl.TrimEnd('/')}/api/users");
            ApiHelpers.AddAuthHeader(request, _config);
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
            if (!ApiHelpers.ValidateApiBaseUrl(_config))
            {
                _ = PluginServices.Instance!.Framework.RunOnTick(() =>
                    _statusMessage = "Invalid API URL");
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(5), token);
                }
                catch
                {
                    // ignore cancellation
                }
                continue;
            }

            try
            {
                _ws?.Dispose();
                _ws = new ClientWebSocket();
                ApiHelpers.AddAuthHeader(_ws, _config);
                var uri = BuildWebSocketUri();
                await _ws.ConnectAsync(uri, token);
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
            catch (Exception ex)
            {
                PluginServices.Instance!.Log.Error(ex, "WebSocket connection error");
                _ = PluginServices.Instance!.Framework.RunOnTick(() =>
                    _statusMessage = $"Connection failed: {ex.Message}");
            }
            finally
            {
                _ws?.Dispose();
                _ws = null;
            }
            try
            {
                _ = PluginServices.Instance!.Framework.RunOnTick(() =>
                    _statusMessage = "Reconnecting...");
                await Task.Delay(TimeSpan.FromSeconds(5), token);
            }
            catch
            {
                // ignore cancellation
            }
        }
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

