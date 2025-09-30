using System;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using DemiCatPlugin;
using Moq;
using Xunit;

public class SettingsWindowIdentityTests
{
    [Fact]
    public async Task UpdateIdentityStoresOfficerClaim()
    {
        typeof(PluginServices)
            .GetProperty("Instance", BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public)!
            .SetValue(null, null);
        var services = new PluginServices();

        var frameworkMock = new Mock<IFramework>();
        frameworkMock
            .Setup(f => f.RunOnTick(It.IsAny<Action>(), It.IsAny<FrameworkUpdatePriority>()))
            .Callback<Action, FrameworkUpdatePriority>((action, _) => action())
            .Returns(Task.CompletedTask);

        var pluginInterfaceMock = new Mock<IDalamudPluginInterface>();
        pluginInterfaceMock.Setup(pi => pi.SavePluginConfig(It.IsAny<IPluginConfiguration>()));

        typeof(PluginServices)
            .GetProperty("Framework", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(services, frameworkMock.Object);
        typeof(PluginServices)
            .GetProperty("PluginInterface", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(services, pluginInterfaceMock.Object);

        var logMock = new Mock<IPluginLog>();
        typeof(PluginServices)
            .GetProperty("Log", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(services, logMock.Object);

        var config = new Config
        {
            ApiBaseUrl = "https://example.com",
            Officer = true
        };

        var handler = new IdentityHandler();
        using var httpClient = new HttpClient(handler);
        var tokenManager = new TokenManager();

        var settings = new SettingsWindow(
            config,
            tokenManager,
            httpClient,
            () => Task.FromResult(true),
            () => Task.CompletedTask,
            logMock.Object,
            pluginInterfaceMock.Object);

        var mainWindow = (MainWindow)FormatterServices.GetUninitializedObject(typeof(MainWindow));
        settings.MainWindow = mainWindow;

        var method = typeof(SettingsWindow)
            .GetMethod("UpdateIdentityAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
        await (Task)method.Invoke(settings, new object?[] { frameworkMock.Object })!;

        Assert.Equal("guild-123", config.GuildId);
        Assert.True(config.IsOfficerToken);
        Assert.True(settings.MainWindow!.HasOfficerAccess);

        pluginInterfaceMock.Verify(pi => pi.SavePluginConfig(config), Times.AtLeastOnce());

        typeof(PluginServices)
            .GetProperty("Instance", BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public)!
            .SetValue(null, null);
    }

    [Fact]
    public async Task UpdateIdentityWithoutOfficerClaimDisablesOfficerAccess()
    {
        typeof(PluginServices)
            .GetProperty("Instance", BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public)!
            .SetValue(null, null);
        var services = new PluginServices();

        var frameworkMock = new Mock<IFramework>();
        frameworkMock
            .Setup(f => f.RunOnTick(It.IsAny<Action>(), It.IsAny<FrameworkUpdatePriority>()))
            .Callback<Action, FrameworkUpdatePriority>((action, _) => action())
            .Returns(Task.CompletedTask);

        var pluginInterfaceMock = new Mock<IDalamudPluginInterface>();
        pluginInterfaceMock.Setup(pi => pi.SavePluginConfig(It.IsAny<IPluginConfiguration>()));

        typeof(PluginServices)
            .GetProperty("Framework", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(services, frameworkMock.Object);
        typeof(PluginServices)
            .GetProperty("PluginInterface", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(services, pluginInterfaceMock.Object);

        var logMock = new Mock<IPluginLog>();
        typeof(PluginServices)
            .GetProperty("Log", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(services, logMock.Object);

        var config = new Config
        {
            ApiBaseUrl = "https://example.com",
            Officer = true,
            IsOfficerToken = true
        };

        var handler = new IdentityHandler(isOfficer: false);
        using var httpClient = new HttpClient(handler);
        var tokenManager = new TokenManager();

        var settings = new SettingsWindow(
            config,
            tokenManager,
            httpClient,
            () => Task.FromResult(true),
            () => Task.CompletedTask,
            logMock.Object,
            pluginInterfaceMock.Object);

        var mainWindow = (MainWindow)FormatterServices.GetUninitializedObject(typeof(MainWindow));
        mainWindow.HasOfficerAccess = true;
        settings.MainWindow = mainWindow;

        var method = typeof(SettingsWindow)
            .GetMethod("UpdateIdentityAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
        await (Task)method.Invoke(settings, new object?[] { frameworkMock.Object })!;

        Assert.Equal("guild-123", config.GuildId);
        Assert.False(config.IsOfficerToken);
        Assert.False(settings.MainWindow!.HasOfficerAccess);

        pluginInterfaceMock.Verify(pi => pi.SavePluginConfig(config), Times.AtLeastOnce());

        typeof(PluginServices)
            .GetProperty("Instance", BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public)!
            .SetValue(null, null);
    }

    private sealed class IdentityHandler : HttpMessageHandler
    {
        private readonly bool _isOfficer;
        private readonly string _guildId;

        public IdentityHandler(bool isOfficer = true, string guildId = "guild-123")
        {
            _isOfficer = isOfficer;
            _guildId = guildId;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.Method == HttpMethod.Get && request.RequestUri?.AbsolutePath == "/api/users/me")
            {
                var payload = JsonSerializer.Serialize(new { guildId = _guildId, isOfficer = _isOfficer });
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(payload, Encoding.UTF8, "application/json")
                });
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        }
    }
}
