using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace DemiCatPlugin.SyncShell;

public sealed class SyncShellClient
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);

    private readonly HttpClient _httpClient;
    private readonly Config _config;
    private readonly TokenManager _tokenManager;

    private const string GetMetaPath = "/api/syncshell/meta";               // TODO: backend to confirm availability
    private const string PublishPath = "/api/syncshell/manifest";
    private const string BlobUploadPath = "/api/syncshell/blobs";           // TODO: backend implementation
    private const string BlobDownloadPath = "/api/syncshell/blobs";         // TODO: backend implementation
    private const string MembershipsPath = "/api/syncshell/memberships";
    private const string PresencePath = "/api/syncshell/presence";

    public SyncShellClient(HttpClient httpClient, Config config, TokenManager tokenManager)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _config = config ?? throw new ArgumentNullException(nameof(config));
        _tokenManager = tokenManager ?? throw new ArgumentNullException(nameof(tokenManager));
    }

    public async Task<UserMetaDto?> GetLatestMetaAsync(string discordId, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(discordId))
        {
            throw new ArgumentException("Discord ID must be provided", nameof(discordId));
        }

        var uri = BuildUri($"{GetMetaPath}?discordId={Uri.EscapeDataString(discordId)}");
        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        ApiHelpers.AddAuthHeader(request, _tokenManager);

        var response = await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            return null;
        }

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        return await JsonSerializer.DeserializeAsync<UserMetaDto>(stream, SerializerOptions, cancellationToken).ConfigureAwait(false);
    }

    public async Task<PublishResultDto> PublishAsync(PublishPayload payload, CancellationToken cancellationToken)
    {
        if (payload == null)
        {
            throw new ArgumentNullException(nameof(payload));
        }

        var uri = BuildUri(PublishPath);
        using var request = new HttpRequestMessage(HttpMethod.Post, uri)
        {
            Content = new StringContent(JsonSerializer.Serialize(payload, SerializerOptions))
        };
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        ApiHelpers.AddAuthHeader(request, _tokenManager);

        var response = await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        var result = await JsonSerializer.DeserializeAsync<PublishResultDto>(stream, SerializerOptions, cancellationToken).ConfigureAwait(false);
        return result ?? new PublishResultDto();
    }

    public async Task UploadBlobAsync(string sha256, Stream content, CancellationToken cancellationToken)
    {
        if (content == null)
        {
            throw new ArgumentNullException(nameof(content));
        }

        var uri = BuildUri($"{BlobUploadPath}?sha256={Uri.EscapeDataString(sha256)}");
        using var request = new HttpRequestMessage(HttpMethod.Put, uri)
        {
            Content = new StreamContent(content)
        };
        ApiHelpers.AddAuthHeader(request, _tokenManager);
        request.Headers.Add("X-Chunk-Size", "1048576");

        var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
    }

    public async Task<bool> BlobExistsAsync(string sha256, CancellationToken cancellationToken)
    {
        var uri = BuildUri($"{BlobDownloadPath}?sha256={Uri.EscapeDataString(sha256)}");
        using var request = new HttpRequestMessage(HttpMethod.Head, uri);
        ApiHelpers.AddAuthHeader(request, _tokenManager);

        var response = await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return false;
        }

        response.EnsureSuccessStatusCode();
        return true;
    }

    private sealed class ResponseStream : Stream
    {
        private readonly HttpResponseMessage _response;
        private readonly Stream _inner;

        public ResponseStream(HttpResponseMessage response, Stream inner)
        {
            _response = response;
            _inner = inner;
        }

        public override bool CanRead => _inner.CanRead;
        public override bool CanSeek => _inner.CanSeek;
        public override bool CanWrite => _inner.CanWrite;
        public override long Length => _inner.Length;
        public override long Position
        {
            get => _inner.Position;
            set => _inner.Position = value;
        }

        public override void Flush() => _inner.Flush();
        public override int Read(byte[] buffer, int offset, int count) => _inner.Read(buffer, offset, count);
        public override long Seek(long offset, SeekOrigin origin) => _inner.Seek(offset, origin);
        public override void SetLength(long value) => _inner.SetLength(value);
        public override void Write(byte[] buffer, int offset, int count) => _inner.Write(buffer, offset, count);
#if NET8_0_OR_GREATER
        public override ValueTask DisposeAsync()
        {
            var disposeTask = _inner.DisposeAsync();
            _response.Dispose();
            return disposeTask;
        }
#endif
        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _inner.Dispose();
            }

            _response.Dispose();
            base.Dispose(disposing);
        }
    }

    public async Task<Stream> DownloadBlobAsync(string sha256, CancellationToken cancellationToken)
    {
        var uri = BuildUri($"{BlobDownloadPath}?sha256={Uri.EscapeDataString(sha256)}");
        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        ApiHelpers.AddAuthHeader(request, _tokenManager);

        var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        return new ResponseStream(response, stream);
    }

    public async Task<Stream> DownloadBlobRangeAsync(string sha256, long start, long end, CancellationToken cancellationToken)
    {
        if (start < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(start));
        }

        if (end < start)
        {
            throw new ArgumentOutOfRangeException(nameof(end));
        }

        var uri = BuildUri($"{BlobDownloadPath}?sha256={Uri.EscapeDataString(sha256)}");
        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        ApiHelpers.AddAuthHeader(request, _tokenManager);
        request.Headers.Range = new RangeHeaderValue(start, end);

        var response = await _httpClient
            .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);

        if (response.StatusCode != HttpStatusCode.PartialContent && response.StatusCode != HttpStatusCode.OK)
        {
            response.EnsureSuccessStatusCode();
        }

        var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        return new ResponseStream(response, stream);
    }

    public async Task<MembershipsResponseDto?> GetMembershipsAsync(CancellationToken cancellationToken)
    {
        var uri = BuildUri(MembershipsPath);
        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        ApiHelpers.AddAuthHeader(request, _tokenManager);

        var response = await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            return null;
        }

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        return await JsonSerializer.DeserializeAsync<MembershipsResponseDto>(stream, SerializerOptions, cancellationToken).ConfigureAwait(false);
    }

    public async Task UpdatePresenceAsync(IEnumerable<string> activeMemberIds, CancellationToken cancellationToken)
    {
        var payload = new PresenceUpdateDto
        {
            ActiveMemberIds = new List<string>(activeMemberIds ?? Array.Empty<string>())
        };

        var uri = BuildUri(PresencePath);
        using var request = new HttpRequestMessage(HttpMethod.Post, uri)
        {
            Content = new StringContent(JsonSerializer.Serialize(payload, SerializerOptions))
        };
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        ApiHelpers.AddAuthHeader(request, _tokenManager);

        var response = await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
    }

    public async Task<bool> ValidateAsync(CancellationToken cancellationToken)
    {
        var response = await ApiHelpers.PingAsync(_httpClient, _config, _tokenManager, cancellationToken).ConfigureAwait(false);
        return response != null && response.IsSuccessStatusCode;
    }

    private Uri BuildUri(string path)
    {
        var baseUri = _config.ApiBaseUrl.TrimEnd('/');
        return new Uri(baseUri + path, UriKind.Absolute);
    }
}
