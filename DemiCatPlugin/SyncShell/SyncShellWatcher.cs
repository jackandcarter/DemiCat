using System;
using System.Linq;
using System.Net;
using System.Net.WebSockets;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Dalamud.Plugin.Services;

namespace DemiCatPlugin.SyncShell;

public sealed class SyncShellWatcher : ISyncShellWatcher
{
    private static readonly TimeSpan BaseRetryDelay = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan MaxRetryDelay = TimeSpan.FromSeconds(60);

    private readonly Config _config;
    private readonly TokenManager _tokenManager;
    private readonly IPluginLog _log;

    private Task? _runTask;
    private CancellationTokenSource? _cts;
    private int _attempt;
    private bool _disposed;
    private readonly object _gate = new();

    public SyncShellWatcher(Config config, TokenManager tokenManager, IPluginLog log)
    {
        _config = config ?? throw new ArgumentNullException(nameof(config));
        _tokenManager = tokenManager ?? throw new ArgumentNullException(nameof(tokenManager));
        _log = log ?? throw new ArgumentNullException(nameof(log));
    }

    public event Action? Connected;
    public event Action<string[]>? NearbySet;
    public event Action<string>? MemberChanged;

    public bool IsRunning
    {
        get
        {
            lock (_gate)
            {
                return _cts != null;
            }
        }
    }

    public void Start()
    {
        lock (_gate)
        {
            ThrowIfDisposed();
            if (_cts != null)
            {
                return;
            }

            _cts = new CancellationTokenSource();
            _runTask = Task.Run(() => RunAsync(_cts.Token), CancellationToken.None);
        }
    }

    public void Stop()
    {
        CancellationTokenSource? cts;
        Task? task;
        lock (_gate)
        {
            cts = _cts;
            task = _runTask;
            _cts = null;
            _runTask = null;
        }

        if (cts == null)
        {
            return;
        }

        cts.Cancel();
        if (task != null)
        {
            _ = task.ContinueWith(t =>
            {
                if (t.Exception != null)
                {
                    PluginServices.Instance?.Log.Debug(t.Exception, "SyncShell watcher stop fault");
                }
                cts.Dispose();
            }, TaskScheduler.Default);
        }
        else
        {
            cts.Dispose();
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
        }

        Stop();
    }

    private async Task RunAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            if (!_config.EnableSyncShell || !_tokenManager.IsReady())
            {
                await Task.Delay(BaseRetryDelay, token).ConfigureAwait(false);
                _attempt = 0;
                continue;
            }

            if (!ApiHelpers.ValidateApiBaseUrl(_config))
            {
                await Task.Delay(BaseRetryDelay, token).ConfigureAwait(false);
                _attempt = 0;
                continue;
            }

            ClientWebSocket? socket = null;
            try
            {
                socket = new ClientWebSocket();
                ApiHelpers.AddAuthHeader(socket, _tokenManager);
                var uri = BuildWebSocketUri();
                await socket.ConnectAsync(uri, token).ConfigureAwait(false);
                _attempt = 0;
                _log.Information("SyncShell watcher connected to {Uri}", uri);
                Connected?.Invoke();

                var buffer = new byte[4096];
                while (!token.IsCancellationRequested && socket.State == WebSocketState.Open)
                {
                    var (message, type) = await ChannelWatcher.ReceiveMessageAsync(socket, buffer, token).ConfigureAwait(false);
                    if (type == WebSocketMessageType.Close)
                    {
                        break;
                    }

                    HandleMessage(message);
                }
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                var status = ApiHelpers.ExtractStatusCode(ex);
                if (status == HttpStatusCode.Unauthorized && _tokenManager.IsReady())
                {
                    _tokenManager.Clear("Authentication failed");
                }
                else if (status == HttpStatusCode.Forbidden)
                {
                    _log.Warning("SyncShell watcher forbidden; check API key/roles");
                }

                _attempt++;
                _log.Warning(ex, "SyncShell watcher connection failed (attempt {Attempt})", _attempt);
            }
            finally
            {
                if (socket != null)
                {
                    try
                    {
                        socket.Abort();
                        socket.Dispose();
                    }
                    catch
                    {
                    }
                }
            }

            if (token.IsCancellationRequested)
            {
                break;
            }

            var delay = CalculateBackoff(_attempt);
            _log.Debug("SyncShell watcher retrying in {Delay}s", delay.TotalSeconds);
            await Task.Delay(delay, token).ConfigureAwait(false);
        }
    }

    private void HandleMessage(string? message)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return;
        }

        try
        {
            using var doc = JsonDocument.Parse(message);
            var root = doc.RootElement;
            var type = root.TryGetProperty("type", out var typeValue) ? typeValue.GetString() : null;
            switch (type)
            {
                case "hello":
                case "connected":
                    Connected?.Invoke();
                    break;
                case "nearby-set":
                    if (root.TryGetProperty("handles", out var handlesElement) && handlesElement.ValueKind == JsonValueKind.Array)
                    {
                        var handles = handlesElement
                            .EnumerateArray()
                            .Select(e => e.GetString())
                            .Where(s => !string.IsNullOrWhiteSpace(s))
                            .Select(s => s!.Trim())
                            .ToArray();
                        NearbySet?.Invoke(handles);
                    }
                    break;
                case "member-changed":
                    if (root.TryGetProperty("handle", out var handleElement))
                    {
                        var handle = handleElement.GetString();
                        if (!string.IsNullOrWhiteSpace(handle))
                        {
                            MemberChanged?.Invoke(handle!.Trim());
                        }
                    }
                    break;
                default:
                    _log.Debug("SyncShell watcher received unsupported message: {Message}", message);
                    break;
            }
        }
        catch (Exception ex)
        {
            _log.Warning(ex, "SyncShell watcher failed to process message: {Message}", message);
        }
    }

    private TimeSpan CalculateBackoff(int attempt)
    {
        if (attempt <= 0)
        {
            return BaseRetryDelay;
        }

        var delay = TimeSpan.FromSeconds(Math.Min(
            MaxRetryDelay.TotalSeconds,
            BaseRetryDelay.TotalSeconds * Math.Pow(2, Math.Min(attempt, 6))));
        return delay;
    }

    private Uri BuildWebSocketUri()
    {
        var baseUri = _config.ApiBaseUrl.TrimEnd('/');
        var builder = new UriBuilder(baseUri + "/ws/syncshell");
        if (builder.Scheme.Equals("https", StringComparison.OrdinalIgnoreCase))
        {
            builder.Scheme = "wss";
        }
        else if (builder.Scheme.Equals("http", StringComparison.OrdinalIgnoreCase))
        {
            builder.Scheme = "ws";
        }

        return builder.Uri;
    }

    private void ThrowIfDisposed()
    {
        if (_disposed)
        {
            throw new ObjectDisposedException(nameof(SyncShellWatcher));
        }
    }
}
