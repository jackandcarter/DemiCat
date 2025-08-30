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
    private readonly ClientWebSocket _ws = new();
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
            await _http.GetAsync("http://localhost:5050/api/presences");
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
            if (_ws.State != WebSocketState.Open)
            {
                await _ws.ConnectAsync(new Uri("ws://localhost:5050/ws/syncshell"), CancellationToken.None);
            }
            await _ws.SendAsync(bytes, WebSocketMessageType.Text, true, CancellationToken.None);
        }
        catch
        {
            // ignore socket errors
        }
    }

    public void Dispose()
    {
        ClientState.Login -= OnLogin;
        _ws.Dispose();
        _http.Dispose();
    }
}
