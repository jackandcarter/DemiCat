using System;
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

public class PresenceServiceLifecycleTests : IDisposable
{
    private readonly PluginServices? _previousServices;
    private readonly TokenManager? _previousTokenManager;
    private readonly PingService? _previousPingService;

    public PresenceServiceLifecycleTests()
    {
        _previousServices = PluginServices.Instance;
        _previousTokenManager = TokenManager.Instance;
        _previousPingService = PingService.Instance;

        var services = new PluginServices();
        SetServiceProperty(services, "Framework", new ImmediateFramework());
        SetServiceProperty(services, "Log", new TestLog());
        SetPluginServicesInstance(services);
    }

    [Fact]
    public void StopAllWatchersAndPresence_MarksServiceNotReady()
    {
        var config = new Config { ApiBaseUrl = "https://example.invalid" };
        using var httpClient = new HttpClient(new StubPresenceHandler());
        var tokenManager = new TokenManager();
        var presence = new DiscordPresenceService(config, httpClient);
        presence.SetPresenceReady(true);
        var channelService = new ChannelService(config, httpClient, tokenManager);

        var settingsWindow = new SettingsWindow(
            config,
            tokenManager,
            httpClient,
            () => Task.FromResult(true),
            () => Task.CompletedTask,
            new TestLog(),
            Mock.Of<IDalamudPluginInterface>());

        settingsWindow.ChatWindow = new ChatWindow(config, httpClient, presence, tokenManager, channelService);

        var method = typeof(SettingsWindow).GetMethod(
            "StopAllWatchersAndPresence",
            BindingFlags.Instance | BindingFlags.NonPublic);

        Assert.NotNull(method);
        method!.Invoke(settingsWindow, Array.Empty<object>());

        Assert.False(presence.IsPresenceReady);
    }

    [Fact]
    public void ChatWindow_StartAndStopTogglePresenceReady()
    {
        var config = new Config { ApiBaseUrl = "https://example.invalid" };
        using var httpClient = new HttpClient(new StubPresenceHandler());
        var tokenManager = new TokenManager();
        var presence = new DiscordPresenceService(config, httpClient);
        var channelService = new ChannelService(config, httpClient, tokenManager);

        PingService.Instance = new PingService(httpClient, config, tokenManager);

        presence.SetPresenceReady(false);
        var window = new ChatWindow(config, httpClient, presence, tokenManager, channelService);

        window.StartNetworking();
        Assert.True(presence.IsPresenceReady);

        window.StopNetworking();
        Assert.False(presence.IsPresenceReady);
    }

    public void Dispose()
    {
        SetPluginServicesInstance(_previousServices);
        SetTokenManagerInstance(_previousTokenManager);
        PingService.Instance = _previousPingService;
    }

    private static void SetServiceProperty(PluginServices services, string propertyName, object value)
        => typeof(PluginServices)
            .GetProperty(propertyName, BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(services, value);

    private static void SetPluginServicesInstance(PluginServices? instance)
        => typeof(PluginServices)
            .GetProperty("Instance", BindingFlags.Static | BindingFlags.NonPublic)!
            .GetSetMethod(true)!
            .Invoke(null, new object?[] { instance });

    private static void SetTokenManagerInstance(TokenManager? instance)
        => typeof(TokenManager)
            .GetProperty("Instance", BindingFlags.Static | BindingFlags.Public)!
            .GetSetMethod(true)!
            .Invoke(null, new object?[] { instance });

    private sealed class StubPresenceHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.Method == HttpMethod.Head)
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
            }

            if (request.Method == HttpMethod.Get && request.RequestUri != null &&
                request.RequestUri.AbsolutePath.EndsWith("/api/users", StringComparison.OrdinalIgnoreCase))
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("[]")
                });
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{}")
            });
        }
    }

    private sealed class ImmediateFramework : IFramework
    {
        public event FrameworkUpdateDelegate? Update { add { } remove { } }

        public FrameworkUpdateType CurrentUpdateType => FrameworkUpdateType.None;

        public Task RunOnTick(Action action, FrameworkUpdatePriority priority = FrameworkUpdatePriority.Normal)
        {
            action();
            return Task.CompletedTask;
        }

        public Task RunOnTick(Func<Task> action, FrameworkUpdatePriority priority = FrameworkUpdatePriority.Normal) => action();
    }

    private sealed class TestLog : IPluginLog
    {
        public void Verbose(string message) { }
        public void Verbose(string message, Exception exception) { }
        public void Debug(string message) { }
        public void Debug(string message, Exception exception) { }
        public void Info(string message) { }
        public void Info(string message, Exception exception) { }
        public void Warning(string message) { }
        public void Warning(string message, Exception exception) { }
        public void Error(string message) { }
        public void Error(Exception exception, string message) { }
        public void Fatal(string message) { }
        public void Fatal(Exception exception, string message) { }
    }
}
