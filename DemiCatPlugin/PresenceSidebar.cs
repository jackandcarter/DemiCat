using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Dalamud.Bindings.ImGui;

namespace DemiCatPlugin;

public class PresenceSidebar : IDisposable
{
    private readonly Config _config;
    private readonly HttpClient _httpClient;
    private readonly List<PresenceDto> _presences = new();
    private ClientWebSocket? _ws;
    private Task? _wsTask;
    private CancellationTokenSource? _wsCts;
    private bool _loaded;
    private string _statusMessage = string.Empty;

    public PresenceSidebar(Config config, HttpClient httpClient)
    {
        _config = config;
        _httpClient = httpClient;
    }

    public void Reload()
    {
        _loaded = false;
        _presences.Clear();
    }

    public void Reset()
    {
        _loaded = false;
        _wsCts?.Cancel();
        _ws?.Dispose();
        _ws = null;
        _wsCts = new CancellationTokenSource();
        _wsTask = RunWebSocket(_wsCts.Token);
    }

    public void Draw()
    {
        if (_wsTask == null)
        {
            _wsCts = new CancellationTokenSource();
            _wsTask = RunWebSocket(_wsCts.Token);
        }
        if (!_loaded)
        {
            _ = Refresh();
        }

        if (!string.IsNullOrEmpty(_statusMessage))
        {
            ImGui.TextUnformatted(_statusMessage);
            ImGui.Spacing();
        }

        var online = _presences.Where(p => p.Status != "offline").OrderBy(p => p.Name).ToList();
        var offline = _presences.Where(p => p.Status == "offline").OrderBy(p => p.Name).ToList();
        ImGui.TextUnformatted($"Online - {online.Count}");
        foreach (var p in online)
        {
            ImGui.TextUnformatted(p.Name);
        }
        ImGui.Spacing();
        ImGui.TextUnformatted($"Offline - {offline.Count}");
        foreach (var p in offline)
        {
            ImGui.TextUnformatted(p.Name);
        }
    }

    public void Dispose()
    {
        _wsCts?.Cancel();
        _ws?.Dispose();
    }

    private async Task Refresh()
    {
        if (!ApiHelpers.ValidateApiBaseUrl(_config))
        {
            return;
        }
        try
        {
            var request = new HttpRequestMessage(HttpMethod.Get, $"{_config.ApiBaseUrl.TrimEnd('/')}/api/presences");
            if (!string.IsNullOrEmpty(_config.AuthToken))
            {
                request.Headers.Add("X-Api-Key", _config.AuthToken);
            }
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
                    _statusMessage = "Invalid API base URL");
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
                if (!string.IsNullOrEmpty(_config.AuthToken))
                {
                    _ws.Options.SetRequestHeader("X-Api-Key", _config.AuthToken);
                }
                var uri = BuildWebSocketUri();
                await _ws.ConnectAsync(uri, token);
                _ = PluginServices.Instance!.Framework.RunOnTick(() => _statusMessage = string.Empty);
                var buffer = new byte[1024];
                while (_ws.State == WebSocketState.Open && !token.IsCancellationRequested)
                {
                    var result = await _ws.ReceiveAsync(new ArraySegment<byte>(buffer), token);
                    if (result.MessageType == WebSocketMessageType.Close)
                    {
                        break;
                    }
                    var json = Encoding.UTF8.GetString(buffer, 0, result.Count);
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

