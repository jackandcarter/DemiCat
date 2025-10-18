using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Net.WebSockets;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using DemiCatPlugin;
using Dalamud.Interface.Textures;
using Dalamud.Plugin.Services;
using Moq;
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
                request.RequestUri.AbsolutePath.EndsWith("/api/presences", StringComparison.OrdinalIgnoreCase))
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

    [Fact]
    public void ApplyPresenceUpdate_PreservesExistingVisuals()
    {
        var config = new Config { ApiBaseUrl = "http://unit-test" };
        using var httpClient = new HttpClient(new StubPresenceHandler());
        var service = new DiscordPresenceService(config, httpClient);

        var field = typeof(DiscordPresenceService).GetField("_presences", BindingFlags.NonPublic | BindingFlags.Instance)!;
        var list = (List<PresenceDto>)field.GetValue(service)!;

        var existingTexture = Mock.Of<ISharedImmediateTexture>();
        var existing = new PresenceDto
        {
            Id = "42",
            Name = "Existing",
            Status = "online",
            AvatarUrl = "https://example.com/avatar.png",
            BannerUrl = "https://example.com/banner.png",
            StatusText = "Hello"
        };
        existing.BannerTexture = existingTexture;
        existing.AccentColorValue = 0x112233;
        existing.Roles.Add("1");
        existing.RoleDetails.Add(new PresenceRoleDto { Id = "1", Name = "Role" });
        list.Add(existing);

        var update = new PresenceDto
        {
            Id = "42",
            Name = "Existing",
            Status = "online",
        };

        service.ApplyPresenceUpdate(update);

        Assert.Single(list);
        var updated = list[0];
        Assert.Equal("https://example.com/avatar.png", updated.AvatarUrl);
        Assert.Equal("https://example.com/banner.png", updated.BannerUrl);
        Assert.Same(existingTexture, updated.BannerTexture);
        Assert.Equal((uint)0x112233, updated.AccentColorValue);
        Assert.Equal("Hello", updated.StatusText);
        Assert.Equal(existing.Roles, updated.Roles);
        Assert.Equal(existing.RoleDetails, updated.RoleDetails);
    }

    [Fact]
    public void ApplyPresenceUpdate_MutatesInPlace()
    {
        var config = new Config { ApiBaseUrl = "http://unit-test" };
        using var httpClient = new HttpClient(new StubPresenceHandler());
        var service = new DiscordPresenceService(config, httpClient);

        var field = typeof(DiscordPresenceService).GetField("_presences", BindingFlags.NonPublic | BindingFlags.Instance)!;
        var list = (List<PresenceDto>)field.GetValue(service)!;

        var existing = new PresenceDto
        {
            Id = "99",
            Name = "Old",
            Status = "offline",
            AvatarUrl = "https://example.com/avatar.png"
        };
        list.Add(existing);

        var update = new PresenceDto
        {
            Id = "99",
            Name = "New",
            Status = "online"
        };

        service.ApplyPresenceUpdate(update);

        Assert.Single(list);
        Assert.Same(existing, list[0]);
        Assert.Equal("New", existing.Name);
        Assert.Equal("online", existing.Status);
        Assert.Equal("https://example.com/avatar.png", existing.AvatarUrl);
    }

    [Fact]
    public void ApplyPresenceUpdate_ClearsRolesWhenEmptyListProvided()
    {
        var config = new Config { ApiBaseUrl = "http://unit-test" };
        using var httpClient = new HttpClient(new StubPresenceHandler());
        var service = new DiscordPresenceService(config, httpClient);

        var field = typeof(DiscordPresenceService).GetField("_presences", BindingFlags.NonPublic | BindingFlags.Instance)!;
        var list = (List<PresenceDto>)field.GetValue(service)!;

        var existing = new PresenceDto
        {
            Id = "200",
            Name = "WithRoles",
            Status = "online"
        };
        existing.Roles.Add("a");
        existing.RoleDetails.Add(new PresenceRoleDto { Id = "a", Name = "Role" });
        list.Add(existing);

        var update = new PresenceDto
        {
            Id = "200",
            Name = "WithRoles",
            Status = "online",
            Roles = new List<string>(),
            RoleDetails = new List<PresenceRoleDto>()
        };

        service.ApplyPresenceUpdate(update);

        Assert.Empty(existing.Roles);
        Assert.Empty(existing.RoleDetails);
    }

    [Fact]
    public void ApplyPresenceUpdate_ClearsStatusTextWhenEmptyStringProvided()
    {
        var config = new Config { ApiBaseUrl = "http://unit-test" };
        using var httpClient = new HttpClient(new StubPresenceHandler());
        var service = new DiscordPresenceService(config, httpClient);

        var field = typeof(DiscordPresenceService).GetField("_presences", BindingFlags.NonPublic | BindingFlags.Instance)!;
        var list = (List<PresenceDto>)field.GetValue(service)!;

        var existing = new PresenceDto
        {
            Id = "201",
            Name = "Status",
            Status = "online",
            StatusText = "Hello"
        };
        list.Add(existing);

        var update = new PresenceDto
        {
            Id = "201",
            Name = "Status",
            Status = "online",
            StatusText = string.Empty
        };

        service.ApplyPresenceUpdate(update);

        Assert.Null(existing.StatusText);
    }

    [Fact]
    public void ApplyPresenceUpdate_NewBannerClearsOldTexture()
    {
        var config = new Config { ApiBaseUrl = "http://unit-test" };
        using var httpClient = new HttpClient(new StubPresenceHandler());
        var service = new DiscordPresenceService(config, httpClient);

        var field = typeof(DiscordPresenceService).GetField("_presences", BindingFlags.NonPublic | BindingFlags.Instance)!;
        var list = (List<PresenceDto>)field.GetValue(service)!;

        var existingTexture = Mock.Of<ISharedImmediateTexture>();
        var existing = new PresenceDto
        {
            Id = "43",
            Name = "Existing",
            Status = "online",
            BannerUrl = "https://example.com/old.png"
        };
        existing.BannerTexture = existingTexture;
        list.Add(existing);

        var update = new PresenceDto
        {
            Id = "43",
            Name = "Existing",
            Status = "online",
            BannerUrl = "https://example.com/new.png"
        };

        service.ApplyPresenceUpdate(update);

        var updated = list[0];
        Assert.Equal("https://example.com/new.png", updated.BannerUrl);
        Assert.Null(updated.BannerTexture);
    }

    [Fact]
    public void ApplyPresenceUpdate_RoleReorderDoesNotTouch()
    {
        var config = new Config { ApiBaseUrl = "http://unit-test" };
        using var httpClient = new HttpClient(new StubPresenceHandler());
        var service = new DiscordPresenceService(config, httpClient);

        var listField = typeof(DiscordPresenceService).GetField("_presences", BindingFlags.NonPublic | BindingFlags.Instance)!;
        var list = (List<PresenceDto>)listField.GetValue(service)!;

        var existing = new PresenceDto
        {
            Id = "501",
            Name = "RoleUser",
            Status = "online",
        };
        existing.Roles = new List<string> { "a", "b" };
        existing.Touch();
        var initialRevision = existing.Revision;
        list.Add(existing);

        var update = new PresenceDto
        {
            Id = "501",
            Name = "RoleUser",
            Status = "online",
            Roles = new List<string> { "b", "a" }
        };

        service.ApplyPresenceUpdate(update);

        Assert.Equal(initialRevision, existing.Revision);
        Assert.NotSame(update.Roles, existing.Roles);
        Assert.Equal(new[] { "a", "b" }, existing.Roles);
    }

    [Fact]
    public void ApplyPresenceUpdate_IdChangeUpdatesIndex()
    {
        var config = new Config { ApiBaseUrl = "http://unit-test" };
        using var httpClient = new HttpClient(new StubPresenceHandler());
        var service = new DiscordPresenceService(config, httpClient);

        var listField = typeof(DiscordPresenceService).GetField("_presences", BindingFlags.NonPublic | BindingFlags.Instance)!;
        var list = (List<PresenceDto>)listField.GetValue(service)!;
        var indexField = typeof(DiscordPresenceService).GetField("_indexById", BindingFlags.NonPublic | BindingFlags.Instance)!;
        var index = (Dictionary<string, int>)indexField.GetValue(service)!;

        var existing = new PresenceDto
        {
            Id = "99",
            Name = "Indexed",
            Status = "online",
        };
        list.Add(existing);
        index["99"] = 0;

        var update = new PresenceDto
        {
            Id = "100",
            Name = "Indexed",
            Status = "online"
        };

        service.ApplyPresenceUpdate(update);

        Assert.Equal("100", existing.Id);
        Assert.True(index.TryGetValue("100", out var mappedIndex));
        Assert.Equal(0, mappedIndex);
        Assert.DoesNotContain(index.Keys, key => key == "99");
    }

    [Fact]
    public void ApplyPresenceUpdate_AvatarUrlChangeResetsTransientState()
    {
        var config = new Config { ApiBaseUrl = "http://unit-test" };
        using var httpClient = new HttpClient(new StubPresenceHandler());
        var service = new DiscordPresenceService(config, httpClient);

        var listField = typeof(DiscordPresenceService).GetField("_presences", BindingFlags.NonPublic | BindingFlags.Instance)!;
        var list = (List<PresenceDto>)listField.GetValue(service)!;
        var indexField = typeof(DiscordPresenceService).GetField("_indexById", BindingFlags.NonPublic | BindingFlags.Instance)!;
        var index = (Dictionary<string, int>)indexField.GetValue(service)!;

        var texture = Mock.Of<ISharedImmediateTexture>();
        var existing = new PresenceDto
        {
            Id = "75",
            Name = "AvatarUser",
            Status = "online",
            AvatarUrl = "https://example.com/old.png",
            AvatarTexture = texture,
            AvatarLoadRequested = true,
            AvatarLoadFailed = true,
            AvatarFailedAt = DateTime.UtcNow.AddMinutes(-5),
        };
        list.Add(existing);
        index["75"] = 0;
        var previousRevision = existing.Revision;

        var update = new PresenceDto
        {
            Id = "75",
            Name = "AvatarUser",
            Status = "online",
            AvatarUrl = "https://example.com/new.png"
        };

        service.ApplyPresenceUpdate(update);

        Assert.Equal("https://example.com/new.png", existing.AvatarUrl);
        Assert.Null(existing.AvatarTexture);
        Assert.False(existing.AvatarLoadRequested);
        Assert.False(existing.AvatarLoadFailed);
        Assert.Null(existing.AvatarFailedAt);
        Assert.True(existing.Revision > previousRevision);
    }

    [Fact]
    public void ApplyPresenceSnapshot_MutatesExistingEntriesAndRemovesMissing()
    {
        var config = new Config { ApiBaseUrl = "http://unit-test" };
        using var httpClient = new HttpClient(new StubPresenceHandler());
        var service = new DiscordPresenceService(config, httpClient);

        var listField = typeof(DiscordPresenceService).GetField("_presences", BindingFlags.NonPublic | BindingFlags.Instance)!;
        var list = (List<PresenceDto>)listField.GetValue(service)!;

        var existing = new PresenceDto
        {
            Id = "alpha",
            Name = "Original",
            Status = "idle"
        };
        var toRemove = new PresenceDto
        {
            Id = "beta",
            Name = "Remove",
            Status = "offline"
        };

        list.Add(existing);
        list.Add(toRemove);

        var snapshot = new List<PresenceDto>
        {
            new()
            {
                Id = "alpha",
                Name = "Updated",
                Status = "online"
            },
            new()
            {
                Id = "gamma",
                Name = "Added",
                Status = "online"
            }
        };

        var method = typeof(DiscordPresenceService).GetMethod("ApplyPresenceSnapshot", BindingFlags.NonPublic | BindingFlags.Instance)!;
        method.Invoke(service, new object?[] { snapshot });

        Assert.Equal(2, list.Count);
        Assert.Same(existing, list[0]);
        Assert.Equal("Updated", existing.Name);
        Assert.Equal("online", existing.Status);
        Assert.DoesNotContain(list, p => p.Id == "beta");
        Assert.Contains(list, p => p.Id == "gamma");
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
