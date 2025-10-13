using System;
using System.Threading;
using Dalamud.Interface.Textures;
using DemiCatPlugin;
using Moq;
using Xunit;

namespace DemiCatPlugin.Tests;

[CollectionDefinition("WebTextureCache", DisableParallelization = true)]
public sealed class WebTextureCacheCollection
{
}

[Collection("WebTextureCache")]
public sealed class WebTextureCacheTests : IDisposable
{
    private readonly int _originalCapacity;

    public WebTextureCacheTests()
    {
        _originalCapacity = WebTextureCache.Capacity;
        WebTextureCache.Clear();
    }

    [Fact]
    public void SetRespectsCapacityEvictLeastRecentlyUsed()
    {
        WebTextureCache.SetCapacity(2);
        WebTextureCache.Set("a", CreateTexture());
        Thread.Sleep(20);
        WebTextureCache.Set("b", CreateTexture());
        Thread.Sleep(20);
        WebTextureCache.Set("c", CreateTexture());

        Assert.False(WebTextureCache.TryGetTexture("a", out _));
        Assert.True(WebTextureCache.TryGetTexture("b", out _));
        Assert.True(WebTextureCache.TryGetTexture("c", out _));
    }

    [Fact]
    public void TryGetTexturePromotesEntry()
    {
        WebTextureCache.SetCapacity(2);
        WebTextureCache.Set("a", CreateTexture());
        Thread.Sleep(20);
        WebTextureCache.Set("b", CreateTexture());

        Assert.True(WebTextureCache.TryGetTexture("a", out _));
        Thread.Sleep(20);
        WebTextureCache.Set("c", CreateTexture());

        Assert.True(WebTextureCache.TryGetTexture("a", out _));
        Assert.False(WebTextureCache.TryGetTexture("b", out _));
        Assert.True(WebTextureCache.TryGetTexture("c", out _));
    }

    public void Dispose()
    {
        WebTextureCache.Clear();
        WebTextureCache.SetCapacity(_originalCapacity);
    }

    private static ISharedImmediateTexture CreateTexture()
        => Mock.Of<ISharedImmediateTexture>();
}
