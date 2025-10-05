using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using DemiCatPlugin;
using Moq;
using Xunit;

public class GameDataCacheTests
{
    [Fact]
    public async Task FallsBackToHttpWhenReadbackMissing()
    {
        var previousServices = PluginServices.Instance;
        var tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);

        try
        {
            var services = new PluginServices();

            var pluginInterface = new Mock<IDalamudPluginInterface>();
            pluginInterface
                .Setup(pi => pi.GetPluginConfigDirectory())
                .Returns(tempDir);

            SetPrivateProperty(services, "PluginInterface", pluginInterface.Object);
            SetPrivateProperty(services, "DataManager", Mock.Of<IDataManager>());
            SetPrivateProperty(services, "TextureProvider", Mock.Of<ITextureProvider>());
            SetPrivateProperty(services, "Log", Mock.Of<IPluginLog>());

            var handler = new FakeHandler();
            using var httpClient = new HttpClient(handler);

            var cache = new GameDataCache(httpClient);
            var entry = await cache.GetItem(123);

            Assert.NotNull(entry);
            Assert.Equal("Example Item", entry!.Name);
            Assert.True(File.Exists(entry.IconPath));
            Assert.True(handler.ItemRequested);
            Assert.True(handler.IconRequested);
        }
        finally
        {
            typeof(PluginServices)
                .GetProperty("Instance", BindingFlags.Static | BindingFlags.NonPublic)!
                .SetValue(null, previousServices);

            if (Directory.Exists(tempDir))
                Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public void PluginServicesHandlesMissingTextureReadbackProvider()
    {
        var services = new PluginServices();

        var pluginInterface = new Mock<IDalamudPluginInterface>();
        pluginInterface
            .Setup(pi => pi.Create<ITextureReadbackProvider>(It.IsAny<object[]>()))
            .Throws(
                new AggregateException(
                    new InvalidOperationException("Requested type Dalamud.Plugin.Services.ITextureReadbackProvider could not be found")));

        SetPrivateProperty(services, "PluginInterface", pluginInterface.Object);
        SetPrivateProperty(services, "Log", Mock.Of<IPluginLog>());

        Assert.Null(services.TextureReadbackProvider);
    }

    private static void SetPrivateProperty(object instance, string name, object? value)
    {
        instance.GetType()
            .GetProperty(name, BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(instance, value);
    }

    private sealed class FakeHandler : HttpMessageHandler
    {
        public bool ItemRequested { get; private set; }
        public bool IconRequested { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.RequestUri!.AbsolutePath == "/item/123")
            {
                ItemRequested = true;
                var content = new StringContent("{\"Name\":\"Example Item\",\"Icon\":\"/icons/example.png\"}");
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = content
                });
            }

            if (request.RequestUri.AbsolutePath == "/icons/example.png")
            {
                IconRequested = true;
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new ByteArrayContent(new byte[] { 1, 2, 3 })
                });
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        }
    }
}
