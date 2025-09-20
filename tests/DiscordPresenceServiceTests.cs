using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Net.WebSockets;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using DemiCatPlugin;
using Dalamud.Plugin.Services;
using Xunit;

public class DiscordPresenceServiceTests
{
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
        public void RunOnTick(Action action, FrameworkUpdatePriority priority = FrameworkUpdatePriority.Normal) => action();
    }

    private sealed class RecordingLog : IPluginLog
    {
        public List<Exception> Errors { get; } = new();

        public void Verbose(string message) { }
        public void Verbose(string message, Exception exception) { }
        public void Debug(string message) { }
        public void Debug(string message, Exception exception) { }
        public void Info(string message) { }
        public void Info(string message, Exception exception) { }
        public void Warning(string message) { }
        public void Warning(string message, Exception exception) { }
        public void Error(string message) { }
        public void Error(Exception exception, string message) => Errors.Add(exception);
        public void Fatal(string message) { }
        public void Fatal(Exception exception, string message) { }
    }

    private sealed class TestDiscordPresenceService : DiscordPresenceService
    {
        private readonly SemaphoreSlim _startSignal = new(0);
        private int _activeScopes;
        private int _maxActiveScopes;
        private int _started;
        private int _completed;

        public TestDiscordPresenceService(Config config, HttpClient httpClient)
            : base(config, httpClient)
        {
        }

        protected override IDisposable? EnterConnectionScope()
        {
            var current = Interlocked.Increment(ref _activeScopes);
            UpdateMax(current);
            Interlocked.Increment(ref _started);
            _startSignal.Release();
            return new Scope(() =>
            {
                Interlocked.Decrement(ref _activeScopes);
                Interlocked.Increment(ref _completed);
            });
        }

        protected override Task ConnectAsync(ClientWebSocket socket, Uri uri, CancellationToken token)
            => Task.CompletedTask;

        protected override Task ReceiveLoopAsync(ClientWebSocket socket, CancellationToken token)
            => Task.Delay(10, token);

        public int MaxActiveScopes => Volatile.Read(ref _maxActiveScopes);
        public int ConnectionsStarted => Volatile.Read(ref _started);
        public int ConnectionsCompleted => Volatile.Read(ref _completed);

        public async Task WaitForConnectionStartsAsync(int expected, TimeSpan timeout)
        {
            for (var i = 0; i < expected; i++)
            {
                if (!await _startSignal.WaitAsync(timeout).ConfigureAwait(false))
                {
                    throw new TimeoutException("Timed out waiting for connection start");
                }
            }
        }

        public async Task DrainAsync(TimeSpan timeout)
        {
            var end = DateTime.UtcNow + timeout;
            while (Volatile.Read(ref _activeScopes) > 0 && DateTime.UtcNow < end)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(5)).ConfigureAwait(false);
            }

            if (Volatile.Read(ref _activeScopes) > 0)
            {
                throw new TimeoutException("Active connections did not drain");
            }
        }

        private void UpdateMax(int current)
        {
            while (true)
            {
                var observed = Volatile.Read(ref _maxActiveScopes);
                if (current <= observed)
                {
                    return;
                }

                if (Interlocked.CompareExchange(ref _maxActiveScopes, current, observed) == observed)
                {
                    return;
                }
            }
        }

        private sealed class Scope : IDisposable
        {
            private readonly Action _onDispose;
            private int _disposed;

            public Scope(Action onDispose) => _onDispose = onDispose;

            public void Dispose()
            {
                if (Interlocked.Exchange(ref _disposed, 1) == 0)
                {
                    _onDispose();
                }
            }
        }
    }

    [Fact]
    public async Task Refresh_InvalidApiUrl_SetsStatusMessage()
    {
        var config = new Config { ApiBaseUrl = string.Empty };
        using var httpClient = new HttpClient(new StubPresenceHandler());

        var previousServices = PluginServices.Instance;
        var previousToken = TokenManager.Instance;

        try
        {
            var services = new PluginServices();
            var framework = new ImmediateFramework();
            var log = new RecordingLog();
            ConfigurePluginServices(services, framework, log);

            _ = new TokenManager();

            var service = new DiscordPresenceService(config, httpClient);

            await service.Refresh().ConfigureAwait(false);

            Assert.Equal("Invalid API URL", service.StatusMessage);
        }
        finally
        {
            SetPluginServicesInstance(previousServices);
            SetTokenManagerInstance(previousToken);
        }
    }

    [Fact]
    public async Task Refresh_MissingToken_SetsApiKeyStatus()
    {
        var config = new Config { ApiBaseUrl = "http://unit-test" };
        using var httpClient = new HttpClient(new StubPresenceHandler());

        var previousServices = PluginServices.Instance;
        var previousToken = TokenManager.Instance;

        try
        {
            var services = new PluginServices();
            var framework = new ImmediateFramework();
            var log = new RecordingLog();
            ConfigurePluginServices(services, framework, log);

            SetTokenManagerInstance(null);

            var service = new DiscordPresenceService(config, httpClient);

            await service.Refresh().ConfigureAwait(false);

            Assert.Equal("API key not configured", service.StatusMessage);
        }
        finally
        {
            SetPluginServicesInstance(previousServices);
            SetTokenManagerInstance(previousToken);
        }
    }

    [Fact]
    public async Task RunWebSocket_MissingToken_SetsApiKeyStatus()
    {
        var config = new Config { ApiBaseUrl = "http://unit-test" };
        using var httpClient = new HttpClient(new StubPresenceHandler());

        var previousServices = PluginServices.Instance;
        var previousToken = TokenManager.Instance;
        DiscordPresenceService? service = null;

        try
        {
            var services = new PluginServices();
            var framework = new ImmediateFramework();
            var log = new RecordingLog();
            ConfigurePluginServices(services, framework, log);

            SetTokenManagerInstance(null);

            service = new DiscordPresenceService(config, httpClient);

            service.Reset();

            await WaitForStatusAsync(service, "API key not configured").ConfigureAwait(false);
        }
        finally
        {
            service?.Stop();
            SetPluginServicesInstance(previousServices);
            SetTokenManagerInstance(previousToken);
        }
    }

    [Fact]
    public async Task RunWebSocket_DisabledConfig_SetsPluginDisabledStatus()
    {
        var config = new Config { ApiBaseUrl = "http://unit-test", Enabled = false };
        using var httpClient = new HttpClient(new StubPresenceHandler());

        var previousServices = PluginServices.Instance;
        var previousToken = TokenManager.Instance;
        DiscordPresenceService? service = null;

        try
        {
            var services = new PluginServices();
            var framework = new ImmediateFramework();
            var log = new RecordingLog();
            ConfigurePluginServices(services, framework, log);

            var tokenManager = new TokenManager();
            _ = tokenManager;

            service = new DiscordPresenceService(config, httpClient);

            service.Reset();

            await WaitForStatusAsync(service, "Plugin disabled").ConfigureAwait(false);
        }
        finally
        {
            service?.Stop();
            SetPluginServicesInstance(previousServices);
            SetTokenManagerInstance(previousToken);
        }
    }

    [Fact]
    public async Task ResetStress_DoesNotOverlapConnections()
    {
        var config = new Config { ApiBaseUrl = "http://unit-test" };
        using var httpClient = new HttpClient(new StubPresenceHandler());

        var previousPing = PingService.Instance;
        var previousToken = TokenManager.Instance;

        try
        {
            var tokenManager = new TokenManager();
            var pingService = new PingService(httpClient, config, tokenManager);
            PingService.Instance = pingService;

            var services = new PluginServices();
            var framework = new ImmediateFramework();
            var log = new RecordingLog();
            typeof(PluginServices).GetProperty("Framework", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(services, framework);
            typeof(PluginServices).GetProperty("Log", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(services, log);

            var service = new TestDiscordPresenceService(config, httpClient);

            service.Reset();
            await service.WaitForConnectionStartsAsync(1, TimeSpan.FromSeconds(1)).ConfigureAwait(false);

            var workers = new List<Task>();
            for (var i = 0; i < 4; i++)
            {
                workers.Add(Task.Run(() =>
                {
                    for (var j = 0; j < 25; j++)
                    {
                        service.Reset();
                    }
                }));
            }

            await Task.WhenAll(workers).ConfigureAwait(false);

            await Task.Delay(250).ConfigureAwait(false);

            service.Stop();
            await service.DrainAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);

            Assert.True(service.ConnectionsStarted > 0, "No connections were started");
            Assert.Equal(service.ConnectionsStarted, service.ConnectionsCompleted);
            Assert.True(service.MaxActiveScopes <= 1, "Connections overlapped");
            Assert.DoesNotContain(log.Errors, ex => ex is InvalidOperationException);
        }
        finally
        {
            PingService.Instance = previousPing;
            var instanceProp = typeof(TokenManager).GetProperty("Instance", BindingFlags.Static | BindingFlags.Public)!;
            instanceProp.GetSetMethod(true)!.Invoke(null, new object?[] { previousToken });
        }
    }

    private static void ConfigurePluginServices(PluginServices services, IFramework framework, IPluginLog log)
    {
        typeof(PluginServices).GetProperty("Framework", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(services, framework);
        typeof(PluginServices).GetProperty("Log", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(services, log);
    }

    private static void SetPluginServicesInstance(PluginServices? instance)
    {
        var property = typeof(PluginServices).GetProperty("Instance", BindingFlags.Static | BindingFlags.NonPublic);
        property?.GetSetMethod(true)!.Invoke(null, new object?[] { instance });
    }

    private static void SetTokenManagerInstance(TokenManager? instance)
    {
        var property = typeof(TokenManager).GetProperty("Instance", BindingFlags.Static | BindingFlags.Public);
        property?.GetSetMethod(true)!.Invoke(null, new object?[] { instance });
    }

    private static async Task WaitForStatusAsync(DiscordPresenceService service, string expected, TimeSpan? timeout = null)
    {
        var deadline = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(1));
        while (DateTime.UtcNow < deadline)
        {
            if (service.StatusMessage == expected)
            {
                return;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(10)).ConfigureAwait(false);
        }

        Assert.Equal(expected, service.StatusMessage);
    }
}
