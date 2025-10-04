using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Internal.Windows.Data.Widgets;
using Dalamud.Interface.Textures;
using Dalamud.Interface.Textures.TextureWraps;
using DemiCatPlugin.Avatars;
using Xunit;

public class AvatarCacheTests
{
    private static readonly byte[] SinglePixelPng = Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAIAAACQd1PeAAAADElEQVR4nGMAAQAABQABDQottAAAAABJRU5ErkJggg==");

    [Fact]
    public async Task ReusingCacheDisposesExpiredTextures()
    {
        var provider = new FakeTextureProvider();
        using var client = new HttpClient(new FakeHttpMessageHandler(SinglePixelPng));
        using var cache = new AvatarCache(provider, client);

        const string url = "https://example.com/avatar.png";
        const string userId = "123";

        const int iterations = 5;
        for (var i = 0; i < iterations; i++)
        {
            var texture = await cache.GetAsync(url, userId);
            Assert.NotNull(texture);

            if (i < iterations - 1)
                ExpireCacheEntry(cache, url);
        }

        Assert.Equal(iterations, provider.CreatedCount);
        Assert.Equal(iterations - 1, provider.DisposedCount);
        Assert.Equal(1, provider.CreatedCount - provider.DisposedCount);

        await ForceRefreshAsync(cache, url);

        Assert.Equal(iterations + 1, provider.CreatedCount);
        Assert.Equal(iterations, provider.DisposedCount);
    }

    private static void ExpireCacheEntry(AvatarCache cache, string url)
    {
        var cacheField = typeof(AvatarCache).GetField("_cache", BindingFlags.Instance | BindingFlags.NonPublic)
                         ?? throw new InvalidOperationException("Missing _cache field");
        var lockField = typeof(AvatarCache).GetField("_lock", BindingFlags.Instance | BindingFlags.NonPublic)
                        ?? throw new InvalidOperationException("Missing _lock field");

        if (lockField.GetValue(cache) is not object syncRoot)
            throw new InvalidOperationException("Missing lock");

        lock (syncRoot)
        {
            if (cacheField.GetValue(cache) is not IDictionary dictionary)
                throw new InvalidOperationException("Unexpected cache type");

            if (!dictionary.Contains(url))
                return;

            var entry = dictionary[url];
            if (entry is null)
                return;

            var expirationField = entry.GetType().GetField("Expiration", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                                  ?? throw new InvalidOperationException("Missing Expiration field");
            expirationField.SetValue(entry, DateTime.UtcNow - TimeSpan.FromMinutes(1));
        }
    }

    private static async Task ForceRefreshAsync(AvatarCache cache, string url)
    {
        var fetchMethod = typeof(AvatarCache).GetMethod("FetchAsync", BindingFlags.Instance | BindingFlags.NonPublic)
                          ?? throw new InvalidOperationException("FetchAsync missing");
        if (fetchMethod.Invoke(cache, new object?[] { url }) is not Task<ISharedImmediateTexture?> task)
            throw new InvalidOperationException("Unexpected fetch result");

        await task.ConfigureAwait(false);
    }

    private sealed class FakeTextureProvider : ITextureProvider
    {
        private int _createdCount;
        private int _disposedCount;

        public int CreatedCount => Volatile.Read(ref _createdCount);
        public int DisposedCount => Volatile.Read(ref _disposedCount);

        public IDalamudTextureWrap CreateFromRaw(RawImageSpecification specs, ReadOnlySpan<byte> bytes, string? debugName = null)
        {
            Interlocked.Increment(ref _createdCount);
            return new FakeTextureWrap(this, specs.Width, specs.Height);
        }

        private void NotifyDisposed()
        {
            Interlocked.Increment(ref _disposedCount);
        }

        private sealed class FakeTextureWrap : IDalamudTextureWrap
        {
            private readonly FakeTextureProvider _provider;
            private int _disposed;

            public FakeTextureWrap(FakeTextureProvider provider, int width, int height)
            {
                _provider = provider;
                this.Width = width;
                this.Height = height;
            }

            public ImTextureID Handle => default;

            public int Width { get; }

            public int Height { get; }

            public void Dispose()
            {
                if (Interlocked.Exchange(ref _disposed, 1) == 0)
                    _provider.NotifyDisposed();
            }
        }

        public IDalamudTextureWrap CreateEmpty(RawImageSpecification specs, bool cpuRead, bool cpuWrite, string? debugName = null)
            => throw new NotSupportedException();

        public IDrawListTextureWrap CreateDrawListTexture(string? debugName = null)
            => throw new NotSupportedException();

        public Task<IDalamudTextureWrap> CreateFromExistingTextureAsync(IDalamudTextureWrap wrap, TextureModificationArgs args = default, bool leaveWrapOpen = false, string? debugName = null, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<IDalamudTextureWrap> CreateFromImGuiViewportAsync(ImGuiViewportTextureArgs args, string? debugName = null, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<IDalamudTextureWrap> CreateFromImageAsync(ReadOnlyMemory<byte> bytes, string? debugName = null, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<IDalamudTextureWrap> CreateFromImageAsync(Stream stream, bool leaveOpen = false, string? debugName = null, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<IDalamudTextureWrap> CreateFromRawAsync(RawImageSpecification specs, ReadOnlyMemory<byte> bytes, string? debugName = null, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<IDalamudTextureWrap> CreateFromRawAsync(RawImageSpecification specs, Stream stream, bool leaveOpen = false, string? debugName = null, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public IDalamudTextureWrap CreateFromTexFile(TexFile file)
            => throw new NotSupportedException();

        public Task<IDalamudTextureWrap> CreateFromTexFileAsync(TexFile file, string? debugName = null, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<IDalamudTextureWrap> CreateFromClipboardAsync(string? debugName = null, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public IEnumerable<IBitmapCodecInfo> GetSupportedImageDecoderInfos()
            => throw new NotSupportedException();

        public ISharedImmediateTexture GetFromGameIcon(in GameIconLookup lookup)
            => throw new NotSupportedException();

        public bool HasClipboardImage()
            => throw new NotSupportedException();

        public bool TryGetFromGameIcon(in GameIconLookup lookup, out ISharedImmediateTexture? texture)
            => throw new NotSupportedException();

        public ISharedImmediateTexture GetFromGame(string path)
            => throw new NotSupportedException();

        public ISharedImmediateTexture GetFromFile(string path)
            => throw new NotSupportedException();

        public ISharedImmediateTexture GetFromFile(FileInfo file)
            => throw new NotSupportedException();

        public ISharedImmediateTexture GetFromFileAbsolute(string fullPath)
            => throw new NotSupportedException();

        public ISharedImmediateTexture GetFromManifestResource(Assembly assembly, string name)
            => throw new NotSupportedException();

        public string GetIconPath(in GameIconLookup lookup)
            => throw new NotSupportedException();

        public bool TryGetIconPath(in GameIconLookup lookup, out string? path)
            => throw new NotSupportedException();

        public bool IsDxgiFormatSupported(int dxgiFormat)
            => throw new NotSupportedException();

        public bool IsDxgiFormatSupportedForCreateFromExistingTextureAsync(int dxgiFormat)
            => throw new NotSupportedException();

        public nint ConvertToKernelTexture(IDalamudTextureWrap wrap, bool leaveWrapOpen = false)
            => throw new NotSupportedException();
    }

    private sealed class FakeHttpMessageHandler : HttpMessageHandler
    {
        private readonly byte[] _payload;

        public FakeHttpMessageHandler(byte[] payload)
        {
            _payload = payload;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(_payload)
            };

            return Task.FromResult(response);
        }
    }
}
