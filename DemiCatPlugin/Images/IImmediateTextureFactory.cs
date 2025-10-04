using System.Threading;
using System.Threading.Tasks;
using Dalamud.Interface.Textures;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace DemiCatPlugin.Images;

internal interface IImmediateTextureFactory
{
    ValueTask<ISharedImmediateTexture?> CreateAsync(Image<Rgba32> image, CancellationToken cancellationToken = default);
}
