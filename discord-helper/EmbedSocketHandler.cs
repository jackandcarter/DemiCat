using System.Net.WebSockets;
using System.Text;
using System.Text.Json;

namespace DiscordHelper;

public class EmbedSocketHandler
{
    private readonly List<WebSocket> _sockets = new();
    private readonly object _lock = new();

    public async Task AddSocketAsync(WebSocket socket)
    {
        lock (_lock)
        {
            _sockets.Add(socket);
        }

        var buffer = new byte[4 * 1024];
        while (socket.State == WebSocketState.Open)
        {
            var result = await socket.ReceiveAsync(buffer, CancellationToken.None);
            if (result.CloseStatus.HasValue)
            {
                break;
            }
        }

        lock (_lock)
        {
            _sockets.Remove(socket);
        }

        await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "Closing", CancellationToken.None);
    }

    public async Task BroadcastAsync(EmbedDto embed)
    {
        string json = JsonSerializer.Serialize(embed);
        var data = Encoding.UTF8.GetBytes(json);
        List<WebSocket> sockets;
        lock (_lock)
        {
            sockets = _sockets.ToList();
        }

        foreach (var socket in sockets)
        {
            if (socket.State == WebSocketState.Open)
            {
                await socket.SendAsync(data, WebSocketMessageType.Text, true, CancellationToken.None);
            }
        }
    }
}
