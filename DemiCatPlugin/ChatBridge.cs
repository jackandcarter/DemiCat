using System;
using System.IO;
using System.Net.Http;
using System.Net.WebSockets;
using System.Net;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

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
    private readonly int[] _schedule = new[] { 0, 2, 5, 10, 30, 60 };
    private int _scheduleIndex;
    private DateTime _connectedSince;

    public event Action<string>? MessageReceived;
    public event Action? Linked;
    public event Action? Unlinked;
    public event Action<string>? StatusChanged;

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
        _ws?.Dispose();
        _ws = null;
    }

    public void Dispose() => Stop();

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

            if (_tokenManager.State == LinkState.Linking)
            {
                if (await ValidateToken(token))
                {
                    _tokenManager.State = LinkState.Linked;
                }
                else
                {
                    _tokenManager.Clear();
                    Unlinked?.Invoke();
                    StatusChanged?.Invoke("Authentication failed");
                    await DelayWithJitter(5, token);
                    continue;
                }
            }

            if (!_tokenManager.IsReady())
            {
                await DelayWithJitter(5, token);
                continue;
            }

            var forbidden = false;
            try
            {
                StatusChanged?.Invoke("Connecting...");
                _ws?.Dispose();
                _ws = new ClientWebSocket();
                ApiHelpers.AddAuthHeader(_ws, _tokenManager);
                var uri = _uriBuilder();
                await _ws.ConnectAsync(uri, token);
                _connectedSince = DateTime.UtcNow;
                _scheduleIndex = 0;
                Linked?.Invoke();
                StatusChanged?.Invoke(string.Empty);

                var buffer = new byte[8192];
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
                    if (json == "ping")
                    {
                        await _ws.SendAsync(new ArraySegment<byte>(Encoding.UTF8.GetBytes("pong")), WebSocketMessageType.Text, true, token);
                        continue;
                    }

                    MessageReceived?.Invoke(json);
                }
            }
            catch (Exception ex)
            {
                forbidden = ex is HttpRequestException hre && hre.StatusCode == HttpStatusCode.Forbidden
                    || (ex as WebSocketException)?.Message.Contains("403") == true
                    || (ex.InnerException as HttpRequestException)?.StatusCode == HttpStatusCode.Forbidden;
            }
            finally
            {
                _ws?.Dispose();
                _ws = null;
            }

            if (token.IsCancellationRequested)
            {
                break;
            }

            if (forbidden)
            {
                _tokenManager.Clear();
                Unlinked?.Invoke();
            }

            var delay = _schedule[Math.Min(_scheduleIndex, _schedule.Length - 1)];
            if ((DateTime.UtcNow - _connectedSince) > TimeSpan.FromSeconds(60))
            {
                _scheduleIndex = 0;
                delay = _schedule[0];
            }
            else if (_scheduleIndex < _schedule.Length - 1)
            {
                _scheduleIndex++;
            }
            StatusChanged?.Invoke(forbidden ? "Forbidden – check API key/roles" : $"Reconnecting in {delay}s...");
            await DelayWithJitter(delay, token);
        }
    }

    private async Task<bool> ValidateToken(CancellationToken token)
    {
        try
        {
            var request = new HttpRequestMessage(HttpMethod.Head, $"{_config.ApiBaseUrl.TrimEnd('/')}/api/ping");
            ApiHelpers.AddAuthHeader(request, _tokenManager);
            var response = await _httpClient.SendAsync(request, token);
            return response.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }

    private async Task DelayWithJitter(int seconds, CancellationToken token)
    {
        var jitter = _random.NextDouble();
        var ms = (int)(seconds * 1000 + jitter * 1000);
        try
        {
            await Task.Delay(ms, token);
        }
        catch
        {
            // ignore
        }
    }
}
