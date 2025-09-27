using System;
using System.Buffers;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Dalamud.Plugin.Services;
using DemiCatPlugin;

namespace DemiCatPlugin.SyncShell;

/// <summary>
/// Connects to the SyncShell websocket endpoint, coordinates manifest exchange and blob transfers.
/// </summary>
public sealed class SyncClient : IDisposable
{
    private const int ReceiveBufferSize = 64 * 1024;
    private const int MaxMessageSize = 2 * 1024 * 1024;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNameCaseInsensitive = true,
        WriteIndented = false,
    };

    private readonly Config _config;
    private readonly TokenManager _tokenManager;
    private readonly IResolver _resolver;
    private readonly IBlobStore _blobStore;
    private readonly CancellationTokenSource _lifetimeCts = new();
    private readonly Channel<OutgoingBlob> _outgoingBlobs;
    private readonly ConcurrentDictionary<string, PeerDownloadState> _downloads = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, PeerUploadState> _uploads = new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim _sendLock = new(1, 1);

    private Task? _runTask;
    private SyncLimits _localLimits;
    private SyncLimits _negotiatedLimits;
    private TokenBucket _sendBucket;
    private bool _disposed;
    private ClientWebSocket? _activeSocket;

    /// <summary>
    /// Initialises a new instance of the <see cref="SyncClient"/> class.
    /// </summary>
    public SyncClient(Config config, TokenManager tokenManager, IResolver resolver, IBlobStore blobStore, SyncLimits? limits = null)
    {
        _config = config ?? throw new ArgumentNullException(nameof(config));
        _tokenManager = tokenManager ?? throw new ArgumentNullException(nameof(tokenManager));
        _resolver = resolver ?? throw new ArgumentNullException(nameof(resolver));
        _blobStore = blobStore ?? throw new ArgumentNullException(nameof(blobStore));
        _localLimits = limits ?? SyncLimits.Default;
        _negotiatedLimits = _localLimits;
        _sendBucket = new TokenBucket(_negotiatedLimits.BytesPerSecond, _negotiatedLimits.BytesPerSecond);
        _outgoingBlobs = Channel.CreateUnbounded<OutgoingBlob>(new UnboundedChannelOptions
        {
            SingleWriter = false,
            SingleReader = true,
        });
    }

    /// <summary>
    /// Raised when a peer offers a manifest.
    /// </summary>
    public event EventHandler<ManifestOfferedEventArgs>? ManifestOffered;

    /// <summary>
    /// Raised when the service publishes a peer discovery manifest snapshot.
    /// </summary>
    public event EventHandler<PeerManifestEventArgs>? PeerManifestReceived;

    /// <summary>
    /// Raised when the service publishes incremental discovery changes for a peer.
    /// </summary>
    public event EventHandler<PeerDeltaEventArgs>? PeerDeltaReceived;

    /// <summary>
    /// Raised when a peer requests blobs from this client.
    /// </summary>
    public event EventHandler<WantsReceivedEventArgs>? WantsReceived;

    /// <summary>
    /// Raised whenever a blob transfer (either direction) completes.
    /// </summary>
    public event EventHandler<BlobTransferEventArgs>? BlobTransferred;

    /// <summary>
    /// Raised when transfer progress changes for a peer.
    /// </summary>
    public event EventHandler<TransferProgressEventArgs>? TransferProgress;

    /// <summary>
    /// Raised after attempting to apply a manifest from a peer.
    /// </summary>
    public event EventHandler<ApplyResultEventArgs>? ApplyCompleted;

    /// <summary>
    /// Gets the limits negotiated with the service.
    /// </summary>
    public SyncLimits NegotiatedLimits => _negotiatedLimits;

    /// <summary>
    /// Starts the client background loop.
    /// </summary>
    public void Start()
    {
        ThrowIfDisposed();
        if (_runTask != null)
        {
            return;
        }

        _runTask = Task.Run(() => RunAsync(_lifetimeCts.Token));
    }

    /// <summary>
    /// Signals the client to stop and waits for the background loop to exit.
    /// </summary>
    public async Task StopAsync()
    {
        ThrowIfDisposed();
        _lifetimeCts.Cancel();
        if (_runTask != null)
        {
            try
            {
                await _runTask.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // ignored
            }
            finally
            {
                _runTask = null;
            }
        }
    }

    /// <summary>
    /// Builds and pushes the current manifest to the service immediately.
    /// </summary>
    public async Task PushManifestAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        var socket = _activeSocket;
        if (socket is null || socket.State != WebSocketState.Open)
        {
            throw new InvalidOperationException("SyncShell client is not connected.");
        }

        var manifest = await _resolver.BuildManifestAsync(cancellationToken).ConfigureAwait(false);
        await SendManifestAsync(socket, null, manifest, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _lifetimeCts.Cancel();
        _lifetimeCts.Dispose();
        _sendLock.Dispose();
    }

    private async Task RunAsync(CancellationToken token)
    {
        var backoff = TimeSpan.FromSeconds(2);
        while (!token.IsCancellationRequested)
        {
            try
            {
                if (!_tokenManager.IsReady() || !ApiHelpers.ValidateApiBaseUrl(_config))
                {
                    await Task.Delay(TimeSpan.FromSeconds(5), token).ConfigureAwait(false);
                    continue;
                }

                await ConnectAndPumpAsync(token).ConfigureAwait(false);
                backoff = TimeSpan.FromSeconds(2);
            }
            catch (OperationCanceledException ex) when (!ShouldRethrow(ex, token))
            {
                return;
            }
            catch (WebSocketException ex)
            {
                PluginServices.Instance?.Log.Warning(ex, "SyncShell websocket disconnected");
                await Task.Delay(backoff, token).ConfigureAwait(false);
                backoff = TimeSpan.FromSeconds(Math.Min(backoff.TotalSeconds * 2, 60));
            }
            catch (HttpRequestException ex) when (ex.StatusCode == HttpStatusCode.Unauthorized)
            {
                PluginServices.Instance?.Log.Warning("SyncShell authentication failed; clearing token");
                _tokenManager.Clear("SyncShell authentication failed");
                await Task.Delay(TimeSpan.FromSeconds(10), token).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                PluginServices.Instance?.Log.Error(ex, "SyncShell client encountered an unexpected error");
                await Task.Delay(backoff, token).ConfigureAwait(false);
                backoff = TimeSpan.FromSeconds(Math.Min(backoff.TotalSeconds * 2, 60));
            }
        }
    }

    private async Task ConnectAndPumpAsync(CancellationToken token)
    {
        await using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(token);
        var socket = new ClientWebSocket();
        ApiHelpers.AddAuthHeader(socket, _tokenManager);
        socket.Options.KeepAliveInterval = TimeSpan.FromSeconds(30);

        var uri = BuildWebSocketUri();
        PluginServices.Instance?.Log.Information($"Connecting to SyncShell websocket at {uri}");
        await socket.ConnectAsync(uri, token).ConfigureAwait(false);

        _negotiatedLimits = _localLimits;
        _sendBucket = new TokenBucket(_negotiatedLimits.BytesPerSecond, _negotiatedLimits.BytesPerSecond);
        _activeSocket = socket;

        var sendLoop = Task.Run(() => ProcessOutgoingBlobsAsync(socket, linkedCts.Token), linkedCts.Token);
        try
        {
            await SendHelloAsync(socket, token).ConfigureAwait(false);
            await ReceiveLoopAsync(socket, linkedCts.Token).ConfigureAwait(false);
        }
        finally
        {
            linkedCts.Cancel();
            _activeSocket = null;
            await CloseWebSocketGracefully(socket, token).ConfigureAwait(false);
            try
            {
                await sendLoop.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // ignored
            }
        }
    }

    private async Task ReceiveLoopAsync(ClientWebSocket socket, CancellationToken token)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(ReceiveBufferSize);
        try
        {
            using var stream = new MemoryStream();
            while (!token.IsCancellationRequested && socket.State == WebSocketState.Open)
            {
                WebSocketReceiveResult? result;
                do
                {
                    result = await socket.ReceiveAsync(buffer, token).ConfigureAwait(false);
                    if (result.MessageType == WebSocketMessageType.Close)
                    {
                        return;
                    }

                    if (result.Count > 0)
                    {
                        stream.Write(buffer, 0, result.Count);
                        if (stream.Length > MaxMessageSize)
                        {
                            throw new InvalidOperationException("SyncShell message exceeded maximum size");
                        }
                    }
                }
                while (!result.EndOfMessage);

                if (stream.Length == 0)
                {
                    continue;
                }

                stream.Position = 0;
                using var document = JsonDocument.Parse(stream, new JsonDocumentOptions { AllowTrailingCommas = true });
                var root = document.RootElement.Clone();
                await HandleMessageAsync(socket, root, token).ConfigureAwait(false);
                stream.SetLength(0);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private async Task HandleMessageAsync(ClientWebSocket socket, JsonElement element, CancellationToken token)
    {
        if (!element.TryGetProperty("type", out var typeProperty))
        {
            PluginServices.Instance?.Log.Warning("Received SyncShell payload without type");
            return;
        }

        var messageType = typeProperty.GetString();
        var payload = ExtractPayload(element);
        switch (messageType)
        {
            case "hello":
                await HandleHelloAsync(socket, payload, token).ConfigureAwait(false);
                break;
            case "manifest":
                await HandleManifestAsync(socket, payload, token).ConfigureAwait(false);
                break;
            case "peerManifest":
            case "peer-manifest":
                await HandlePeerManifestAsync(payload).ConfigureAwait(false);
                break;
            case "peerDelta":
            case "peer-delta":
            case "manifestDelta":
                await HandlePeerDeltaAsync(payload).ConfigureAwait(false);
                break;
            case "want":
                await HandleWantAsync(payload, token).ConfigureAwait(false);
                break;
            case "blob":
                await HandleBlobAsync(payload, token).ConfigureAwait(false);
                break;
            case "progress":
                HandleProgress(payload);
                break;
            default:
                PluginServices.Instance?.Log.Debug($"Ignoring unknown SyncShell message type '{messageType}'");
                break;
        }
    }

    private static JsonElement ExtractPayload(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object && element.TryGetProperty("payload", out var payload))
        {
            return payload.ValueKind == JsonValueKind.Object ? payload.Clone() : payload;
        }

        return element;
    }

    private async Task HandleHelloAsync(ClientWebSocket socket, JsonElement element, CancellationToken token)
    {
        if (element.TryGetProperty("limits", out var limitsElement))
        {
            var limits = limitsElement.Deserialize<SyncLimits>(JsonOptions);
            if (limits != null)
            {
                _negotiatedLimits = SyncLimits.Negotiate(_localLimits, limits);
                _sendBucket = new TokenBucket(_negotiatedLimits.BytesPerSecond, _negotiatedLimits.BytesPerSecond);
                PluginServices.Instance?.Log.Information($"SyncShell limits negotiated: {_negotiatedLimits}");
            }
        }

        // push initial manifest so peers can start requesting data
        var manifest = await _resolver.BuildManifestAsync(token).ConfigureAwait(false);
        await SendManifestAsync(socket, null, manifest, token).ConfigureAwait(false);
    }

    private async Task HandleManifestAsync(ClientWebSocket socket, JsonElement element, CancellationToken token)
    {
        var peerId = element.TryGetProperty("peerId", out var peerProperty)
            ? peerProperty.GetString() ?? "unknown"
            : "unknown";
        if (!element.TryGetProperty("manifest", out var manifestElement))
        {
            PluginServices.Instance?.Log.Warning("Received manifest message missing payload");
            return;
        }

        var manifest = manifestElement.Deserialize<SyncManifest>(JsonOptions);
        if (manifest is null)
        {
            PluginServices.Instance?.Log.Warning("Received invalid manifest payload");
            return;
        }

        ManifestOffered?.Invoke(this, new ManifestOfferedEventArgs(peerId, manifest));

        var missing = _resolver.GetMissingBlobs(manifest).ToList();
        if (missing.Count == 0)
        {
            await ApplyManifestAsync(peerId, manifest, token).ConfigureAwait(false);
            return;
        }

        var download = new PeerDownloadState(peerId, manifest, missing);
        _downloads[peerId] = download;
        TransferProgress?.Invoke(this, new TransferProgressEventArgs(peerId, download.ReceivedCount, download.TotalCount, TransferKind.Download));

        await SendWantBatchesAsync(socket, peerId, missing, token).ConfigureAwait(false);
    }

    private Task HandlePeerManifestAsync(JsonElement element)
    {
        var peerId = TryGetStringProperty(element, "peerId", "peer_id") ?? "unknown";
        var timestamp = TryGetTimestamp(element, "timestamp", "updatedAt", "updated_at") ?? DateTimeOffset.UtcNow;

        var assets = new List<DiscoveryAsset>();
        if (TryGetProperty(element, out var manifestsElement, "assets", "manifest", "items") &&
            manifestsElement.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in manifestsElement.EnumerateArray())
            {
                if (item.ValueKind == JsonValueKind.Object)
                {
                    assets.Add(ParseDiscoveryAsset(item));
                }
            }
        }

        PeerManifestReceived?.Invoke(this, new PeerManifestEventArgs(peerId, assets, timestamp));
        return Task.CompletedTask;
    }

    private Task HandlePeerDeltaAsync(JsonElement element)
    {
        var peerId = TryGetStringProperty(element, "peerId", "peer_id") ?? "unknown";
        var timestamp = TryGetTimestamp(element, "timestamp", "updatedAt", "updated_at") ?? DateTimeOffset.UtcNow;

        var updated = new List<DiscoveryAsset>();
        if (TryGetProperty(element, out var updatedElement, "updated", "assets", "items", "added") &&
            updatedElement.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in updatedElement.EnumerateArray())
            {
                if (item.ValueKind == JsonValueKind.Object)
                {
                    updated.Add(ParseDiscoveryAsset(item));
                }
            }
        }

        var removed = new List<string>();
        if (TryGetProperty(element, out var removedElement, "removed", "deleted", "removedIds", "removed_ids") &&
            removedElement.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in removedElement.EnumerateArray())
            {
                if (item.ValueKind == JsonValueKind.String)
                {
                    removed.Add(item.GetString() ?? string.Empty);
                }
            }
        }

        PeerDeltaReceived?.Invoke(this, new PeerDeltaEventArgs(peerId, updated, removed, timestamp));
        return Task.CompletedTask;
    }

    private async Task HandleWantAsync(JsonElement element, CancellationToken token)
    {
        var peerId = element.TryGetProperty("peerId", out var peerProperty)
            ? peerProperty.GetString() ?? "unknown"
            : "unknown";
        if (!element.TryGetProperty("want", out var wantElement))
        {
            PluginServices.Instance?.Log.Warning("Received want message without payload");
            return;
        }

        var want = wantElement.Deserialize<WantBlobs>(JsonOptions) ?? new WantBlobs();
        WantsReceived?.Invoke(this, new WantsReceivedEventArgs(peerId, want));

        var upload = _uploads.GetOrAdd(peerId, static _ => new PeerUploadState());
        upload.AddRequests(want);
        TransferProgress?.Invoke(this, new TransferProgressEventArgs(peerId, upload.Completed, upload.Total, TransferKind.Upload));

        foreach (var blob in want.Blobs)
        {
            _outgoingBlobs.Writer.TryWrite(new OutgoingBlob(peerId, blob, null));
        }

        foreach (var chunk in want.Chunks)
        {
            _outgoingBlobs.Writer.TryWrite(new OutgoingBlob(peerId, chunk.Blob, chunk));
        }
    }

    private async Task HandleBlobAsync(JsonElement element, CancellationToken token)
    {
        var peerId = element.TryGetProperty("peerId", out var peerProperty)
            ? peerProperty.GetString() ?? "unknown"
            : "unknown";
        if (!element.TryGetProperty("hash", out var hashProperty))
        {
            PluginServices.Instance?.Log.Warning("Received blob without hash");
            return;
        }

        var hash = hashProperty.GetString();
        if (string.IsNullOrEmpty(hash))
        {
            PluginServices.Instance?.Log.Warning("Received blob without hash");
            return;
        }

        if (!element.TryGetProperty("data", out var dataProperty))
        {
            PluginServices.Instance?.Log.Warning("Received blob without data");
            return;
        }

        var data = dataProperty.GetString();
        if (string.IsNullOrEmpty(data))
        {
            PluginServices.Instance?.Log.Warning("Received blob without data");
            return;
        }

        var bytes = Convert.FromBase64String(data);
        await using var stream = new MemoryStream(bytes, writable: false);
        await _blobStore.StoreAsync(hash!, stream, token).ConfigureAwait(false);

        if (_downloads.TryGetValue(peerId, out var download))
        {
            download.MarkReceived(hash!);
            TransferProgress?.Invoke(this, new TransferProgressEventArgs(peerId, download.ReceivedCount, download.TotalCount, TransferKind.Download));
            if (download.IsComplete)
            {
                await ApplyManifestAsync(peerId, download.Manifest, token).ConfigureAwait(false);
                _downloads.TryRemove(peerId, out _);
            }
        }

        BlobTransferred?.Invoke(this, new BlobTransferEventArgs(peerId, hash!, bytes.Length, BlobDirection.Inbound));
    }

    private void HandleProgress(JsonElement element)
    {
        var peerId = element.GetProperty("peerId").GetString() ?? "unknown";
        var received = element.TryGetProperty("received", out var recvElement) ? recvElement.GetInt32() : 0;
        var total = element.TryGetProperty("total", out var totalElement) ? totalElement.GetInt32() : 0;
        TransferProgress?.Invoke(this, new TransferProgressEventArgs(peerId, received, total, TransferKind.Remote));
    }

    private async Task ApplyManifestAsync(string peerId, SyncManifest manifest, CancellationToken token)
    {
        try
        {
            await _resolver.ApplyManifestAsync(peerId, manifest, token).ConfigureAwait(false);
            ApplyCompleted?.Invoke(this, new ApplyResultEventArgs(peerId, true, null));
        }
        catch (Exception ex)
        {
            PluginServices.Instance?.Log.Error(ex, "Failed to apply manifest for peer {Peer}", peerId);
            ApplyCompleted?.Invoke(this, new ApplyResultEventArgs(peerId, false, ex));
        }
    }

    private static DiscoveryAsset ParseDiscoveryAsset(JsonElement element)
    {
        var asset = new DiscoveryAsset();

        asset.Id = TryGetStringProperty(element, "id", "assetId", "asset_id") ?? string.Empty;
        asset.Name = TryGetStringProperty(element, "name") ?? asset.Id;
        asset.Kind = TryGetStringProperty(element, "kind", "type") ?? string.Empty;
        asset.Uploader = TryGetStringProperty(element, "uploader", "owner", "peer") ?? string.Empty;
        asset.DownloadUrl = TryGetStringProperty(element, "downloadUrl", "download_url", "url");

        if (TryGetProperty(element, out var sizeElement, "size") && sizeElement.TryGetInt64(out var size))
        {
            asset.Size = size;
        }

        asset.CreatedAt = TryGetTimestamp(element, "created_at", "createdAt");
        asset.UpdatedAt = TryGetTimestamp(element, "updated_at", "updatedAt");

        if (TryGetProperty(element, out var depsElement, "dependencies", "deps") && depsElement.ValueKind == JsonValueKind.Array)
        {
            foreach (var dep in depsElement.EnumerateArray())
            {
                if (dep.ValueKind == JsonValueKind.String)
                {
                    asset.Dependencies.Add(dep.GetString() ?? string.Empty);
                }
            }
        }

        if (TryGetProperty(element, out var itemsElement, "items", "children") && itemsElement.ValueKind == JsonValueKind.Array)
        {
            foreach (var child in itemsElement.EnumerateArray())
            {
                if (child.ValueKind == JsonValueKind.Object)
                {
                    asset.Items.Add(ParseDiscoveryAsset(child));
                }
            }
        }

        return asset;
    }

    private static DateTimeOffset? TryGetTimestamp(JsonElement element, params string[] propertyNames)
    {
        if (TryGetProperty(element, out var property, propertyNames) && property.ValueKind == JsonValueKind.String)
        {
            var value = property.GetString();
            if (!string.IsNullOrWhiteSpace(value) && DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var parsed))
            {
                return parsed;
            }
        }

        return null;
    }

    private static string? TryGetStringProperty(JsonElement element, params string[] propertyNames)
    {
        if (TryGetProperty(element, out var property, propertyNames) && property.ValueKind == JsonValueKind.String)
        {
            return property.GetString();
        }

        return null;
    }

    private static bool TryGetProperty(JsonElement element, out JsonElement property, params string[] propertyNames)
    {
        foreach (var name in propertyNames)
        {
            if (element.TryGetProperty(name, out property))
            {
                return true;
            }
        }

        property = default;
        return false;
    }

    private async Task SendHelloAsync(ClientWebSocket socket, CancellationToken token)
    {
        var payload = new SyncEnvelope<HelloPayload>
        {
            Type = "hello",
            Payload = new HelloPayload
            {
                Version = 1,
                Limits = _localLimits,
            },
        };

        await SendAsync(socket, payload, token).ConfigureAwait(false);
    }

    private async Task SendManifestAsync(ClientWebSocket socket, string? peerId, SyncManifest manifest, CancellationToken token)
    {
        var payload = new SyncEnvelope<ManifestPayload>
        {
            Type = "manifest",
            Payload = new ManifestPayload
            {
                PeerId = peerId,
                Manifest = manifest,
            },
        };

        await SendAsync(socket, payload, token).ConfigureAwait(false);
    }

    private async Task SendWantAsync(ClientWebSocket socket, string peerId, WantBlobs want, CancellationToken token)
    {
        var payload = new SyncEnvelope<WantPayload>
        {
            Type = "want",
            Payload = new WantPayload
            {
                PeerId = peerId,
                Want = want,
            },
        };

        await SendAsync(socket, payload, token).ConfigureAwait(false);
    }

    private async Task SendWantBatchesAsync(ClientWebSocket socket, string peerId, IReadOnlyList<string> hashes, CancellationToken token)
    {
        if (hashes.Count == 0)
        {
            return;
        }

        var batchSize = Math.Max(1, _negotiatedLimits.MaxOutstandingWants);
        for (var offset = 0; offset < hashes.Count; offset += batchSize)
        {
            var want = new WantBlobs();
            for (var i = offset; i < Math.Min(offset + batchSize, hashes.Count); i++)
            {
                want.Blobs.Add(hashes[i]);
            }

            await SendWantAsync(socket, peerId, want, token).ConfigureAwait(false);
        }
    }

    private async Task<long> SendBlobAsync(ClientWebSocket socket, string peerId, string hash, Stream stream, BlobChunk? chunk, CancellationToken token)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(_negotiatedLimits.ChunkSizeBytes);
        long totalSent = 0;
        try
        {
            if (chunk != null && chunk.Offset > 0)
            {
                stream.Seek(chunk.Offset, SeekOrigin.Begin);
            }

            var remaining = chunk?.Length ?? (int)(stream.Length - stream.Position);
            while (remaining > 0)
            {
                var toRead = Math.Min(buffer.Length, remaining);
                var read = await stream.ReadAsync(buffer.AsMemory(0, toRead), token).ConfigureAwait(false);
                if (read == 0)
                {
                    break;
                }

                await _sendBucket.WaitAsync(read, token).ConfigureAwait(false);

                var payload = new SyncEnvelope<BlobPayload>
                {
                    Type = "blob",
                    Payload = new BlobPayload
                    {
                        PeerId = peerId,
                        Hash = hash,
                        Offset = chunk?.Offset,
                        Data = Convert.ToBase64String(buffer, 0, read),
                    },
                };

                await SendAsync(socket, payload, token).ConfigureAwait(false);
                BlobTransferred?.Invoke(this, new BlobTransferEventArgs(peerId, hash, read, BlobDirection.Outbound));
                remaining -= read;
                totalSent += read;
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }

        return totalSent;
    }

    private async Task ProcessOutgoingBlobsAsync(ClientWebSocket socket, CancellationToken token)
    {
        await foreach (var request in _outgoingBlobs.Reader.ReadAllAsync(token).ConfigureAwait(false))
        {
            try
            {
                if (!_blobStore.Has(request.Hash))
                {
                    PluginServices.Instance?.Log.Warning("Requested blob {Hash} not available locally", request.Hash);
                    continue;
                }

                await using var stream = _blobStore.OpenRead(request.Hash);
                await SendBlobAsync(socket, request.PeerId, request.Hash, stream, request.Chunk, token).ConfigureAwait(false);

                if (_uploads.TryGetValue(request.PeerId, out var uploadState))
                {
                    uploadState.MarkSent(request.Hash, request.Chunk);
                    TransferProgress?.Invoke(this, new TransferProgressEventArgs(request.PeerId, uploadState.Completed, uploadState.Total, TransferKind.Upload));
                    if (uploadState.IsComplete)
                    {
                        _uploads.TryRemove(request.PeerId, out _);
                    }
                }
            }
            catch (OperationCanceledException ex) when (!ShouldRethrow(ex, token))
            {
                return;
            }
            catch (Exception ex)
            {
                PluginServices.Instance?.Log.Error(ex, "Failed to transmit blob {Hash} for peer {Peer}", request.Hash, request.PeerId);
            }
        }
    }

    private async Task SendAsync<T>(ClientWebSocket socket, SyncEnvelope<T> envelope, CancellationToken token)
    {
        if (envelope is null)
        {
            throw new ArgumentNullException(nameof(envelope));
        }

        var json = JsonSerializer.Serialize(envelope, JsonOptions);
        var bytes = Encoding.UTF8.GetBytes(json);
        await _sendBucket.WaitAsync(bytes.Length, token).ConfigureAwait(false);
        await _sendLock.WaitAsync(token).ConfigureAwait(false);
        try
        {
            await socket.SendAsync(bytes, WebSocketMessageType.Text, true, token).ConfigureAwait(false);
        }
        finally
        {
            _sendLock.Release();
        }
    }

    private Uri BuildWebSocketUri()
    {
        var builder = new UriBuilder(_config.ApiBaseUrl)
        {
            Scheme = _config.ApiBaseUrl.StartsWith("https", StringComparison.OrdinalIgnoreCase) ? "wss" : "ws",
            Path = "/ws/syncshell",
        };

        return builder.Uri;
    }

    private static async Task CloseWebSocketGracefully(WebSocket socket, CancellationToken token)
    {
        if (socket.State == WebSocketState.Open || socket.State == WebSocketState.CloseReceived)
        {
            try
            {
                await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, string.Empty, token).ConfigureAwait(false);
            }
            catch (OperationCanceledException ex) when (!ShouldRethrow(ex, token))
            {
                // ignored
            }
            catch (WebSocketException)
            {
                // ignored
            }
        }
    }

    private static bool ShouldRethrow(OperationCanceledException ex, CancellationToken token)
        => token.CanBeCanceled && token.IsCancellationRequested && ex.CancellationToken == token;

    private void ThrowIfDisposed()
    {
        if (_disposed)
        {
            throw new ObjectDisposedException(nameof(SyncClient));
        }
    }

    private sealed record OutgoingBlob(string PeerId, string Hash, BlobChunk? Chunk);

    private sealed class PeerDownloadState
    {
        private readonly HashSet<string> _pending;
        private readonly int _totalCount;

        public PeerDownloadState(string peerId, SyncManifest manifest, IEnumerable<string> missing)
        {
            PeerId = peerId;
            Manifest = manifest;
            _pending = new HashSet<string>(missing, StringComparer.OrdinalIgnoreCase);
            _totalCount = _pending.Count;
        }

        public string PeerId { get; }

        public SyncManifest Manifest { get; }

        public int TotalCount => _totalCount;

        public int ReceivedCount => _totalCount - _pending.Count;

        public bool IsComplete => _pending.Count == 0;

        public void MarkReceived(string hash)
        {
            _pending.Remove(hash);
        }
    }

    private sealed class PeerUploadState
    {
        private readonly HashSet<string> _pending = new(StringComparer.OrdinalIgnoreCase);
        private int _total;
        private int _completed;

        public int Total => _total;

        public int Completed => _completed;

        public bool IsComplete
        {
            get
            {
                lock (_pending)
                {
                    return _pending.Count == 0;
                }
            }
        }

        public void AddRequests(WantBlobs want)
        {
            if (want is null)
            {
                return;
            }

            lock (_pending)
            {
                foreach (var blob in want.Blobs)
                {
                    if (_pending.Add(blob))
                    {
                        _total++;
                    }
                }

                foreach (var chunk in want.Chunks)
                {
                    var key = BuildChunkKey(chunk);
                    if (_pending.Add(key))
                    {
                        _total++;
                    }
                }
            }
        }

        public void MarkSent(string hash, BlobChunk? chunk)
        {
            lock (_pending)
            {
                var key = chunk is null ? hash : BuildChunkKey(chunk);
                if (_pending.Remove(key))
                {
                    _completed++;
                }
            }
        }

        private static string BuildChunkKey(BlobChunk chunk)
        {
            if (!string.IsNullOrEmpty(chunk.Hash))
            {
                return $"{chunk.Hash}:{chunk.Offset}:{chunk.Length}";
            }

            return $"{chunk.Blob}:{chunk.Offset}:{chunk.Length}";
        }
    }
}

/// <summary>
/// Limits negotiated with the SyncShell service to ensure fair usage.
/// </summary>
public sealed class SyncLimits
{
    /// <summary>
    /// Gets the default limits used before negotiation.
    /// </summary>
    public static SyncLimits Default { get; } = new()
    {
        BytesPerSecond = 512 * 1024,
        ChunkSizeBytes = 64 * 1024,
        MaxOutstandingWants = 64,
    };

    /// <summary>
    /// Maximum sustained send rate in bytes per second.
    /// </summary>
    [JsonPropertyName("bytesPerSecond")]
    public int BytesPerSecond { get; init; } = 256 * 1024;

    /// <summary>
    /// Maximum chunk payload size in bytes.
    /// </summary>
    [JsonPropertyName("chunkSizeBytes")]
    public int ChunkSizeBytes { get; init; } = 64 * 1024;

    /// <summary>
    /// Maximum number of concurrent want requests.
    /// </summary>
    [JsonPropertyName("maxOutstandingWants")]
    public int MaxOutstandingWants { get; init; } = 64;

    /// <inheritdoc />
    public override string ToString()
        => $"{BytesPerSecond}B/s, chunk {ChunkSizeBytes}B, wants {MaxOutstandingWants}";

    /// <summary>
    /// Combines local and remote limits.
    /// </summary>
    public static SyncLimits Negotiate(SyncLimits local, SyncLimits remote)
        => new()
        {
            BytesPerSecond = Math.Min(local.BytesPerSecond, remote.BytesPerSecond),
            ChunkSizeBytes = Math.Min(local.ChunkSizeBytes, remote.ChunkSizeBytes),
            MaxOutstandingWants = Math.Min(local.MaxOutstandingWants, remote.MaxOutstandingWants),
        };
}

/// <summary>
/// Envelope for SyncShell websocket messages.
/// </summary>
internal sealed class SyncEnvelope<T>
{
    [JsonPropertyName("type")]
    public string Type { get; set; } = string.Empty;

    [JsonPropertyName("payload")]
    public T? Payload { get; set; }
}

internal sealed class HelloPayload
{
    [JsonPropertyName("version")]
    public int Version { get; set; }

    [JsonPropertyName("limits")]
    public SyncLimits? Limits { get; set; }
}

internal sealed class ManifestPayload
{
    [JsonPropertyName("peerId")]
    public string? PeerId { get; set; }

    [JsonPropertyName("manifest")]
    public SyncManifest Manifest { get; set; } = new();
}

internal sealed class WantPayload
{
    [JsonPropertyName("peerId")]
    public string PeerId { get; set; } = string.Empty;

    [JsonPropertyName("want")]
    public WantBlobs Want { get; set; } = new();
}

internal sealed class BlobPayload
{
    [JsonPropertyName("peerId")]
    public string PeerId { get; set; } = string.Empty;

    [JsonPropertyName("hash")]
    public string Hash { get; set; } = string.Empty;

    [JsonPropertyName("offset")]
    public long? Offset { get; set; }

    [JsonPropertyName("data")]
    public string Data { get; set; } = string.Empty;
}

/// <summary>
/// Lightweight representation of discovery assets transmitted via SyncShell discovery channels.
/// </summary>
public sealed class DiscoveryAsset
{
    /// <summary>
    /// Gets or sets the asset identifier.
    /// </summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the asset display name.
    /// </summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the asset kind/category.
    /// </summary>
    public string Kind { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the asset size in bytes.
    /// </summary>
    public long Size { get; set; }

    /// <summary>
    /// Gets or sets the uploader/owner label.
    /// </summary>
    public string? Uploader { get; set; }

    /// <summary>
    /// Gets or sets the optional download URL.
    /// </summary>
    public string? DownloadUrl { get; set; }

    /// <summary>
    /// Gets or sets when the asset was originally created.
    /// </summary>
    public DateTimeOffset? CreatedAt { get; set; }

    /// <summary>
    /// Gets or sets when the asset was last updated.
    /// </summary>
    public DateTimeOffset? UpdatedAt { get; set; }

    /// <summary>
    /// Gets the dependency identifiers declared for this asset.
    /// </summary>
    public List<string> Dependencies { get; } = new();

    /// <summary>
    /// Gets the child assets in bundle hierarchies.
    /// </summary>
    public List<DiscoveryAsset> Items { get; } = new();
}

/// <summary>
/// Event arguments for manifest offers.
/// </summary>
public sealed class ManifestOfferedEventArgs : EventArgs
{
    internal ManifestOfferedEventArgs(string peerId, SyncManifest manifest)
    {
        PeerId = peerId;
        Manifest = manifest;
    }

    public string PeerId { get; }

    public SyncManifest Manifest { get; }
}

/// <summary>
/// Event arguments published when a peer discovery manifest snapshot is received.
/// </summary>
public sealed class PeerManifestEventArgs : EventArgs
{
    internal PeerManifestEventArgs(string peerId, IReadOnlyList<DiscoveryAsset> assets, DateTimeOffset timestamp)
    {
        PeerId = peerId;
        Assets = assets;
        Timestamp = timestamp;
    }

    /// <summary>
    /// Gets the originating peer identifier.
    /// </summary>
    public string PeerId { get; }

    /// <summary>
    /// Gets the discovered assets for the peer.
    /// </summary>
    public IReadOnlyList<DiscoveryAsset> Assets { get; }

    /// <summary>
    /// Gets the timestamp associated with the snapshot.
    /// </summary>
    public DateTimeOffset Timestamp { get; }
}

/// <summary>
/// Event arguments published when a peer discovery delta arrives.
/// </summary>
public sealed class PeerDeltaEventArgs : EventArgs
{
    internal PeerDeltaEventArgs(string peerId, IReadOnlyList<DiscoveryAsset> updated, IReadOnlyList<string> removed, DateTimeOffset timestamp)
    {
        PeerId = peerId;
        Updated = updated;
        Removed = removed;
        Timestamp = timestamp;
    }

    /// <summary>
    /// Gets the peer associated with the delta.
    /// </summary>
    public string PeerId { get; }

    /// <summary>
    /// Gets the assets updated or added by the delta.
    /// </summary>
    public IReadOnlyList<DiscoveryAsset> Updated { get; }

    /// <summary>
    /// Gets the asset identifiers removed by the delta.
    /// </summary>
    public IReadOnlyList<string> Removed { get; }

    /// <summary>
    /// Gets when the delta was issued.
    /// </summary>
    public DateTimeOffset Timestamp { get; }
}

/// <summary>
/// Event arguments for want requests.
/// </summary>
public sealed class WantsReceivedEventArgs : EventArgs
{
    internal WantsReceivedEventArgs(string peerId, WantBlobs want)
    {
        PeerId = peerId;
        Want = want;
    }

    public string PeerId { get; }

    public WantBlobs Want { get; }
}

/// <summary>
/// Direction of blob transfer relative to the local client.
/// </summary>
public enum BlobDirection
{
    Inbound,
    Outbound,
}

/// <summary>
/// Event arguments for blob transfers.
/// </summary>
public sealed class BlobTransferEventArgs : EventArgs
{
    internal BlobTransferEventArgs(string peerId, string hash, int size, BlobDirection direction)
    {
        PeerId = peerId;
        Hash = hash;
        Size = size;
        Direction = direction;
    }

    public string PeerId { get; }

    public string Hash { get; }

    public int Size { get; }

    public BlobDirection Direction { get; }
}

/// <summary>
/// Event arguments describing progress for a peer.
/// </summary>
public sealed class TransferProgressEventArgs : EventArgs
{
    internal TransferProgressEventArgs(string peerId, int completed, int total, TransferKind kind)
    {
        PeerId = peerId;
        Completed = completed;
        Total = total;
        Kind = kind;
    }

    public string PeerId { get; }

    public int Completed { get; }

    public int Total { get; }

    public TransferKind Kind { get; }
}

/// <summary>
/// Identifies whether a progress update relates to uploading, downloading or remote reports.
/// </summary>
public enum TransferKind
{
    Download,
    Upload,
    Remote,
}

/// <summary>
/// Event arguments for manifest application results.
/// </summary>
public sealed class ApplyResultEventArgs : EventArgs
{
    internal ApplyResultEventArgs(string peerId, bool success, Exception? error)
    {
        PeerId = peerId;
        Success = success;
        Error = error;
    }

    public string PeerId { get; }

    public bool Success { get; }

    public Exception? Error { get; }
}

/// <summary>
/// Simple token bucket used to throttle outbound payload sizes.
/// </summary>
internal sealed class TokenBucket
{
    private readonly int _capacity;
    private readonly int _refillPerSecond;
    private double _tokens;
    private DateTime _lastRefill;
    private readonly object _lock = new();

    public TokenBucket(int capacity, int refillPerSecond)
    {
        _capacity = Math.Max(1, capacity);
        _refillPerSecond = Math.Max(1, refillPerSecond);
        _tokens = _capacity;
        _lastRefill = DateTime.UtcNow;
    }

    public async Task WaitAsync(int amount, CancellationToken token)
    {
        if (amount <= 0)
        {
            return;
        }

        while (true)
        {
            token.ThrowIfCancellationRequested();
            double waitSeconds;
            lock (_lock)
            {
                Refill();
                if (_tokens >= amount)
                {
                    _tokens -= amount;
                    return;
                }

                var deficit = amount - _tokens;
                waitSeconds = deficit / _refillPerSecond;
            }

            var delay = TimeSpan.FromSeconds(Math.Max(waitSeconds, 0.01));
            await Task.Delay(delay, token).ConfigureAwait(false);
        }
    }

    private void Refill()
    {
        var now = DateTime.UtcNow;
        var elapsed = (now - _lastRefill).TotalSeconds;
        if (elapsed <= 0)
        {
            return;
        }

        _tokens = Math.Min(_capacity, _tokens + (elapsed * _refillPerSecond));
        _lastRefill = now;
    }
}
