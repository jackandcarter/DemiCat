using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Runtime.Serialization;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Dalamud.Configuration;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using DemiCatPlugin;
using Moq;
using Xunit;

public class RefreshRolesTests
{
    [Fact]
    public async Task FailedChannelFetch_DoesNotDisableFcChat()
    {
        RoleCache.Reset();

        var config = new Config
        {
            ApiBaseUrl = "https://example.com",
            SyncedChat = false,
            Events = false,
            Requests = false,
            EnableFcChat = true,
            EnableFcChatUserSet = true,
            Roles = new List<string> { "member" }
        };

        var handler = new SequenceHandler();
        handler.EnqueueResponse(_ => CreateJsonResponse("{\"roles\":[\"member\"]}"));
        handler.EnqueueResponse(_ => new HttpResponseMessage(HttpStatusCode.InternalServerError)
        {
            Content = new StringContent("error", Encoding.UTF8, "text/plain")
        });
        handler.EnqueueResponse(_ => CreateJsonResponse("[]"));

        using var httpClient = new HttpClient(handler);

        var plugin = (Plugin)FormatterServices.GetUninitializedObject(typeof(Plugin));
        var services = new PluginServices();

        var framework = new ImmediateFramework();
        SetServiceProperty(services, "Framework", framework);

        var log = new TestLog();
        SetServiceProperty(services, "Log", log);

        var pluginInterfaceMock = new Mock<IDalamudPluginInterface>();
        pluginInterfaceMock.Setup(p => p.SavePluginConfig(It.IsAny<IPluginConfiguration>()));
        SetServiceProperty(services, "PluginInterface", pluginInterfaceMock.Object);

        var tokenManager = new TokenManager();

        var chatWindow = (ChatWindow)FormatterServices.GetUninitializedObject(typeof(ChatWindow));
        chatWindow.ChannelsLoaded = true;

        var officerChatWindow = (OfficerChatWindow)FormatterServices.GetUninitializedObject(typeof(OfficerChatWindow));
        ((ChatWindow)officerChatWindow).ChannelsLoaded = true;

        var channelWatcher = (ChannelWatcher)FormatterServices.GetUninitializedObject(typeof(ChannelWatcher));
        SetPrivateField(channelWatcher, "_config", config);

        var requestWatcher = new RequestWatcher(config, httpClient, tokenManager);

        var mainWindow = (MainWindow)FormatterServices.GetUninitializedObject(typeof(MainWindow));

        SetPrivateField(plugin, "_services", services);
        SetPrivateField(plugin, "_config", config);
        SetPrivateField(plugin, "_tokenManager", tokenManager);
        SetPrivateField(plugin, "_httpClient", httpClient);
        SetPrivateField(plugin, "_chatWindow", chatWindow);
        SetPrivateField(plugin, "_officerChatWindow", officerChatWindow);
        SetPrivateField(plugin, "_channelWatcher", channelWatcher);
        SetPrivateField(plugin, "_requestWatcher", requestWatcher);
        SetPrivateField(plugin, "_mainWindow", mainWindow);
        SetPrivateField(plugin, "_watcherRestartLock", new SemaphoreSlim(1, 1));
        SetPrivateField(plugin, "_officerWatcherRunning", false);
        SetPrivateField(plugin, "_presenceService", null);

        try
        {
            var method = typeof(Plugin).GetMethod("RefreshRoles", BindingFlags.Instance | BindingFlags.NonPublic)!;
            var refreshTask = (Task<bool>)method.Invoke(plugin, new object?[] { log })!;
            var success = await refreshTask.ConfigureAwait(false);

            Assert.True(success);
            Assert.True(config.EnableFcChat);
            Assert.True(config.EnableFcChatUserSet);
            Assert.Contains(log.WarningMessages, message => message.Contains("channels could not be fetched", StringComparison.OrdinalIgnoreCase));
            Assert.True(chatWindow.ChannelsLoaded);
            Assert.True(((ChatWindow)officerChatWindow).ChannelsLoaded);
        }
        finally
        {
            RoleCache.Reset();
            typeof(PluginServices)
                .GetProperty("Instance", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)?
                .SetValue(null, null);
            typeof(TokenManager)
                .GetProperty("Instance", BindingFlags.Static | BindingFlags.Public)!
                .SetValue(null, null);
        }
    }

    private static HttpResponseMessage CreateJsonResponse(string json)
    {
        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };
    }

    private static void SetServiceProperty(PluginServices services, string propertyName, object value)
    {
        typeof(PluginServices)
            .GetProperty(propertyName, BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(services, value);
    }

    private static void SetPrivateField(object instance, string fieldName, object? value)
    {
        instance.GetType()
            .GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(instance, value);
    }

    private sealed class SequenceHandler : HttpMessageHandler
    {
        private readonly Queue<Func<HttpRequestMessage, HttpResponseMessage>> _responses = new();

        public void EnqueueResponse(Func<HttpRequestMessage, HttpResponseMessage> factory)
        {
            _responses.Enqueue(factory);
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (_responses.Count == 0)
            {
                throw new InvalidOperationException("No response queued.");
            }

            var response = _responses.Dequeue()(request);
            return Task.FromResult(response);
        }
    }

    private sealed class ImmediateFramework : IFramework
    {
        public event FrameworkUpdateDelegate? Update { add { } remove { } }
        public FrameworkUpdateType CurrentUpdateType => FrameworkUpdateType.None;
        public void RunOnTick(Action action, FrameworkUpdatePriority priority = FrameworkUpdatePriority.Normal) => action();
    }

    private sealed class TestLog : IPluginLog
    {
        public List<string> WarningMessages { get; } = new();

        public void Verbose(string message) { }
        public void Verbose(string message, Exception exception) { }
        public void Debug(string message) { }
        public void Debug(string message, Exception exception) { }
        public void Info(string message) { }
        public void Info(string message, Exception exception) { }
        public void Warning(string message) => WarningMessages.Add(message);
        public void Warning(string message, Exception exception) => WarningMessages.Add(message);
        public void Error(string message) { }
        public void Error(Exception exception, string message) { }
        public void Fatal(string message) { }
        public void Fatal(Exception exception, string message) { }
    }
}
