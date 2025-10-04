using System;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Dalamud.Interface.Textures;
using Dalamud.Plugin.Services;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace DemiCatPlugin.Images;

internal sealed class ImmediateTextureFactory : IImmediateTextureFactory
{
    private readonly ITextureProvider _textureProvider;

    public ImmediateTextureFactory(ITextureProvider textureProvider)
    {
        _textureProvider = textureProvider ?? throw new ArgumentNullException(nameof(textureProvider));
    }

    public ValueTask<ISharedImmediateTexture?> CreateAsync(Image<Rgba32> image, CancellationToken cancellationToken = default)
    {
        if (image == null)
        {
            throw new ArgumentNullException(nameof(image));
        }

        cancellationToken.ThrowIfCancellationRequested();

        if (!image.DangerousTryGetSinglePixelMemory(out var memory))
        {
            throw new InvalidOperationException("Image pixels are not stored contiguously.");
        }

        var span = memory.Span;
        if (span.Length == 0)
        {
            return ValueTask.FromResult<ISharedImmediateTexture?>(null);
        }

        var bytes = MemoryMarshal.AsBytes(span);
        var wrap = _textureProvider.CreateFromRaw(
            RawImageSpecification.Rgba32(image.Width, image.Height),
            bytes);
        var texture = new ForwardingSharedImmediateTexture(wrap);
        return ValueTask.FromResult<ISharedImmediateTexture?>(texture);
    }
}
