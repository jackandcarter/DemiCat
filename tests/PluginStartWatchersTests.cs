using System;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Runtime.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Dalamud.Game.Gui.Toast;
using Dalamud.Plugin.Services;
using DemiCatPlugin;
using Moq;
using Xunit;

public class PluginStartWatchersTests
{
    public static TheoryData<HttpStatusCode?> TransientPingResponses => new()
    {
        { HttpStatusCode.InternalServerError },
        { null }
    };

    [Theory]
    [MemberData(nameof(TransientPingResponses))]
    public async Task StartWatchersDoesNotClearTokenOnTransientFailures(HttpStatusCode? statusCode)
    {
        var (plugin, tokenManager, toastMock) = CreatePlugin(statusCode);
        try
        {
            string? unlinkReason = null;
            tokenManager.OnUnlinked += reason => unlinkReason = reason;

            await InvokeStartWatchersAsync(plugin);

            Assert.Null(unlinkReason);
            Assert.True(tokenManager.IsReady());

            var showErrorCalls = toastMock.Invocations
                .Where(i => i.Method.Name == nameof(IToastGui.ShowError))
                .ToList();

            Assert.NotEmpty(showErrorCalls);

            var message = showErrorCalls[0].Arguments.FirstOrDefault() as string;
            Assert.NotNull(message);
            Assert.Contains("Unable to reach the DemiCat backend", message!);
        }
        finally
        {
            Cleanup();
        }
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden)]
    public async Task StartWatchersClearsTokenOnAuthFailures(HttpStatusCode statusCode)
    {
        var (plugin, tokenManager, toastMock) = CreatePlugin(statusCode);
        try
        {
            string? unlinkReason = null;
            tokenManager.OnUnlinked += reason => unlinkReason = reason;

            await InvokeStartWatchersAsync(plugin);

            Assert.Equal("Invalid API key", unlinkReason);
            Assert.False(tokenManager.IsReady());

            var showErrorCalls = toastMock.Invocations
                .Where(i => i.Method.Name == nameof(IToastGui.ShowError))
                .ToList();
            Assert.Empty(showErrorCalls);
        }
        finally
        {
            Cleanup();
        }
    }

    private static (Plugin Plugin, TokenManager TokenManager, Mock<IToastGui> ToastMock) CreatePlugin(HttpStatusCode? statusCode)
    {
        PingService.Instance = null;

        var plugin = (Plugin)FormatterServices.GetUninitializedObject(typeof(Plugin));
        var services = new PluginServices();

        var logMock = new Mock<IPluginLog>();
        logMock.Setup(l => l.Warning(It.IsAny<string>()));
        logMock.Setup(l => l.Warning(It.IsAny<Exception>(), It.IsAny<string>()));
        logMock.Setup(l => l.Info(It.IsAny<string>()));

        var toastMock = new Mock<IToastGui>();
        var frameworkMock = new Mock<IFramework>();
        frameworkMock
            .Setup(f => f.RunOnTick(It.IsAny<Action>(), It.IsAny<FrameworkUpdatePriority>()))
            .Callback<Action, FrameworkUpdatePriority>((action, _) => action());

        var config = new Config
        {
            ApiBaseUrl = "https://example.com",
            Requests = false,
            Events = false,
            SyncedChat = false,
            EnableFcChat = false,
            Officer = false,
            Templates = false
        };
        config.Roles.Clear();

        var handler = statusCode.HasValue
            ? new StaticResponseHandler(statusCode.Value)
            : new ThrowingHandler();
        var httpClient = new HttpClient(handler);

        SetPrivateField(plugin, "_services", services);
        SetPrivateField(plugin, "_config", config);
        var tokenManager = new TokenManager();
        SetPrivateField(plugin, "_tokenManager", tokenManager);
        SetPrivateField(plugin, "_httpClient", httpClient);

        typeof(PluginServices)
            .GetProperty("Framework", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(services, frameworkMock.Object);
        typeof(PluginServices)
            .GetProperty("Log", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(services, logMock.Object);
        typeof(PluginServices)
            .GetProperty("ToastGui", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(services, toastMock.Object);

        return (plugin, tokenManager, toastMock);
    }

    private static void SetPrivateField(object instance, string fieldName, object value)
    {
        instance.GetType()
            .GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(instance, value);
    }

    private static Task InvokeStartWatchersAsync(Plugin plugin)
    {
        var method = typeof(Plugin)
            .GetMethod("StartWatchersAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
        return (Task)method.Invoke(plugin, null)!;
    }

    private static void Cleanup()
    {
        PingService.Instance = null;
        var instanceProperty = typeof(PluginServices)
            .GetProperty("Instance", BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);
        instanceProperty?.SetValue(null, null);
    }

    private sealed class StaticResponseHandler : HttpMessageHandler
    {
        private readonly HttpStatusCode _statusCode;

        public StaticResponseHandler(HttpStatusCode statusCode)
        {
            _statusCode = statusCode;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(new HttpResponseMessage(_statusCode));
    }

    private sealed class ThrowingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => throw new HttpRequestException("Simulated network failure");
    }
}
