using System;
using System.Buffers;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Dalamud.Interface.Textures;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace DemiCatPlugin.Images;

internal sealed class ImageLoader : IDisposable
{
    private const int BufferSize = 80 * 1024;

    private readonly HttpClient _httpClient;
    private readonly IImmediateTextureFactory _textureFactory;
    private readonly SemaphoreSlim _downloadSemaphore;
    private readonly SemaphoreSlim _decodeSemaphore;
    private readonly long _byteBudget;
    private readonly int _maxDecodeWidth;
    private readonly int _maxDecodeHeight;
    private long _bytesInFlight;
    private bool _disposed;

    public ImageLoader(
        HttpClient httpClient,
        IImmediateTextureFactory textureFactory,
        int downloadConcurrency,
        int decodeConcurrency,
        long byteBudget,
        int maxDecodeWidth,
        int maxDecodeHeight)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _textureFactory = textureFactory ?? throw new ArgumentNullException(nameof(textureFactory));
        _downloadSemaphore = new SemaphoreSlim(Math.Max(1, downloadConcurrency), Math.Max(1, downloadConcurrency));
        _decodeSemaphore = new SemaphoreSlim(Math.Max(1, decodeConcurrency), Math.Max(1, decodeConcurrency));
        _byteBudget = Math.Max(0, byteBudget);
        _maxDecodeWidth = Math.Max(1, maxDecodeWidth);
        _maxDecodeHeight = Math.Max(1, maxDecodeHeight);
    }

    public async Task<ISharedImmediateTexture?> LoadIntoTextureAsync(string url, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return null;
        }

        ThrowIfDisposed();

        await _downloadSemaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
        long reservedBytes = 0;
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            using var response = await _httpClient
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            var contentLength = response.Content.Headers.ContentLength;
            if (_byteBudget > 0 && contentLength.HasValue && contentLength.Value > _byteBudget)
            {
                return null;
            }

            if (_byteBudget > 0 && contentLength.HasValue && contentLength.Value > 0)
            {
                if (!TryReserveBytes(contentLength.Value))
                {
                    return null;
                }

                reservedBytes = contentLength.Value;
            }

            await using var contentStream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            using var memoryStream = contentLength is { } length && length > 0 && length <= int.MaxValue
                ? new MemoryStream((int)length)
                : new MemoryStream();
            var buffer = ArrayPool<byte>.Shared.Rent(BufferSize);
            try
            {
                long bytesReadTotal = 0;
                while (true)
                {
                    var read = await contentStream
                        .ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken)
                        .ConfigureAwait(false);
                    if (read <= 0)
                    {
                        break;
                    }

                    if (_byteBudget > 0)
                    {
                        if (!contentLength.HasValue)
                        {
                            if (!TryReserveBytes(read))
                            {
                                return null;
                            }

                            reservedBytes += read;
                        }
                        else
                        {
                            bytesReadTotal += read;
                            if (bytesReadTotal > reservedBytes)
                            {
                                var extra = bytesReadTotal - reservedBytes;
                                if (!TryReserveBytes(extra))
                                {
                                    return null;
                                }

                                reservedBytes += extra;
                            }
                        }
                    }

                    memoryStream.Write(buffer, 0, read);
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }

            memoryStream.Position = 0;

            await _decodeSemaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                using var img = await Image.LoadAsync(memoryStream, cancellationToken).ConfigureAwait(false);
                if (img.Frames.Count > 1)
                {
                    for (var i = img.Frames.Count - 1; i >= 1; i--)
                    {
                        using var frame = img.Frames.RemoveFrame(i);
                    }
                }

                using var rgba = img.CloneAs<Rgba32>();
                ResizeToFit(rgba);
                return await _textureFactory.CreateAsync(rgba, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                _decodeSemaphore.Release();
            }
        }
        finally
        {
            if (reservedBytes > 0)
            {
                ReleaseBytes(reservedBytes);
            }

            _downloadSemaphore.Release();
        }
    }

    private void ResizeToFit(Image<Rgba32> image)
    {
        if (image.Width <= _maxDecodeWidth && image.Height <= _maxDecodeHeight)
        {
            return;
        }

        image.Mutate(ctx => ctx.Resize(new ResizeOptions
        {
            Mode = ResizeMode.Max,
            Size = new Size(_maxDecodeWidth, _maxDecodeHeight),
            Sampler = KnownResamplers.Lanczos3,
            Compand = true
        }));
    }

    private bool TryReserveBytes(long bytes)
    {
        if (_byteBudget <= 0 || bytes <= 0)
        {
            return true;
        }

        while (true)
        {
            var current = Volatile.Read(ref _bytesInFlight);
            var next = current + bytes;
            if (next > _byteBudget)
            {
                return false;
            }

            if (Interlocked.CompareExchange(ref _bytesInFlight, next, current) == current)
            {
                return true;
            }
        }
    }

    private void ReleaseBytes(long bytes)
    {
        if (_byteBudget <= 0 || bytes <= 0)
        {
            return;
        }

        Interlocked.Add(ref _bytesInFlight, -bytes);
    }

    private void ThrowIfDisposed()
    {
        if (_disposed)
        {
            throw new ObjectDisposedException(nameof(ImageLoader));
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _downloadSemaphore.Dispose();
        _decodeSemaphore.Dispose();
    }
}
