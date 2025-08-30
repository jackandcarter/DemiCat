using System;
using System.Net.Http;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Dalamud.IoC;
using Dalamud.Plugin;
using Dalamud.Plugin.Ipc;
using Dalamud.Plugin.Services;

namespace SyncShellClient;

public class SyncShellClient : IDalamudPlugin
{
    public string Name => "SyncShellClient";

    [PluginService] private IDalamudPluginInterface PluginInterface { get; set; } = null!;
    [PluginService] private ClientState ClientState { get; set; } = null!;

    private readonly HttpClient _http = new();
    private ClientWebSocket _ws = new();
    private string? _token;
    private IpcSubscriber<string[]>? _enabledMods;

    public SyncShellClient()
    {
        _enabledMods = PluginInterface.GetIpcSubscriber<string[]>("Penumbra.GetEnabledMods");
        ClientState.Login += OnLogin;
    }

    private async void OnLogin()
    {
        await RefreshPresenceAsync();
        await SendManifestAsync();
    }

    private async Task RefreshPresenceAsync()
    {
        try
        {
            await _http.GetAsync("http://127.0.0.1:5050/api/presences");
        }
        catch
        {
            // ignore network errors
        }
    }

    private async Task SendManifestAsync()
    {
        if (!ClientState.IsLoggedIn || _enabledMods is null)
            return;
        string[] mods;
        try
        {
            mods = _enabledMods.InvokeFunc();
        }
        catch
        {
            return;
        }
        var payload = JsonSerializer.Serialize(new { mods });
        var bytes = Encoding.UTF8.GetBytes(payload);
        try
        {
            if (!await EnsureConnectionAsync())
                return;
            await _ws.SendAsync(bytes, WebSocketMessageType.Text, true, CancellationToken.None);
        }
        catch
        {
            // retry once with a fresh token
            try
            {
                _token = null;
                _ws.Dispose();
                _ws = new ClientWebSocket();
                if (!await EnsureConnectionAsync())
                    return;
                await _ws.SendAsync(bytes, WebSocketMessageType.Text, true, CancellationToken.None);
            }
            catch
            {
                // ignore socket errors
            }
        }
    }

    public void Dispose()
    {
        ClientState.Login -= OnLogin;
        _ws.Dispose();
        _http.Dispose();
    }

    private async Task<bool> EnsureConnectionAsync()
    {
        if (_ws.State == WebSocketState.Open)
            return true;

        if (!await RefreshTokenAsync())
            return false;

        _ws.Options.SetRequestHeader("X-Api-Key", _token);
        await _ws.ConnectAsync(new Uri("ws://127.0.0.1:5050/ws/syncshell"), CancellationToken.None);
        return true;
    }

    private async Task<bool> RefreshTokenAsync()
    {
        if (!string.IsNullOrEmpty(_token))
            return true;

        try
        {
            var resp = await _http.PostAsync("http://127.0.0.1:5050/api/syncshell/pair", null);
            if (!resp.IsSuccessStatusCode)
                return false;
            var json = await resp.Content.ReadAsStringAsync();
            var parsed = JsonSerializer.Deserialize<PairResponse>(json);
            _token = parsed?.token;
            return !string.IsNullOrEmpty(_token);
        }
        catch
        {
            return false;
        }
    }

    private record PairResponse(string token);
}
