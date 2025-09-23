using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Runtime.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using DemiCatPlugin;
using DemiCatPlugin.Emoji;
using Moq;
using Xunit;

public class PluginChannelValidationTests
{
    [Fact]
    public async Task ValidationResponseStoresGuildAndEnablesCustomEmoji()
    {
        Cleanup();
        PingService.Instance = null;

        var plugin = (Plugin)FormatterServices.GetUninitializedObject(typeof(Plugin));
        var services = new PluginServices();

        var pluginInterfaceMock = new Mock<IDalamudPluginInterface>();
        pluginInterfaceMock
            .Setup(p => p.SavePluginConfig(It.IsAny<Config>()));

        var logMock = new Mock<IPluginLog>();
        logMock.Setup(l => l.Info(It.IsAny<string>()));
        logMock.Setup(l => l.Warning(It.IsAny<string>()));
        logMock.Setup(l => l.Warning(It.IsAny<Exception>(), It.IsAny<string>()));
        logMock.Setup(l => l.Error(It.IsAny<string>()));

        var frameworkMock = new Mock<IFramework>();
        frameworkMock
            .Setup(f => f.RunOnTick(It.IsAny<Action>(), It.IsAny<FrameworkUpdatePriority>()))
            .Callback<Action, FrameworkUpdatePriority>((action, _) => action());

        var toastMock = new Mock<IToastGui>();

        SetPluginService(services, "PluginInterface", pluginInterfaceMock.Object);
        SetPluginService(services, "Log", logMock.Object);
        SetPluginService(services, "Framework", frameworkMock.Object);
        SetPluginService(services, "ToastGui", toastMock.Object);

        var config = new Config
        {
            ApiBaseUrl = "https://example.com",
            Requests = false,
            Events = false,
            SyncedChat = false,
            EnableFcChat = false
        };
        config.Roles.Clear();

        var tokenManager = new TokenManager();
        var handler = new ValidationTestHandler();
        var httpClient = new HttpClient(handler);

        var channelService = new ChannelService(config, httpClient, tokenManager);
        var channelSelection = new ChannelSelectionService(config);
        var emojiManager = new EmojiManager(httpClient, tokenManager, config);
        var ui = new UiRenderer(config, httpClient, channelSelection, emojiManager);
        var requestWatcher = new RequestWatcher(config, httpClient, tokenManager);
        var chatWindow = new ChatWindow(config, httpClient, null, tokenManager, channelService);
        var officerChatWindow = new OfficerChatWindow(config, httpClient, null, tokenManager, channelService);
        var settingsWindow = new SettingsWindow(
            config,
            tokenManager,
            httpClient,
            () => Task.FromResult(true),
            () => Task.CompletedTask,
            logMock.Object,
            pluginInterfaceMock.Object
        );
        var mainWindow = new MainWindow(
            config,
            ui,
            chatWindow,
            officerChatWindow,
            settingsWindow,
            httpClient,
            channelService,
            channelSelection,
            emojiManager
        );
        var channelWatcher = new ChannelWatcher(
            config,
            ui,
            mainWindow.EventCreateWindow,
            mainWindow.TemplatesWindow,
            chatWindow,
            officerChatWindow,
            tokenManager,
            httpClient
        );

        settingsWindow.MainWindow = mainWindow;
        settingsWindow.ChatWindow = chatWindow;
        settingsWindow.OfficerChatWindow = officerChatWindow;
        settingsWindow.ChannelWatcher = channelWatcher;
        settingsWindow.RequestWatcher = requestWatcher;

        SetPrivateField(plugin, "_services", services);
        SetPrivateField(plugin, "_config", config);
        SetPrivateField(plugin, "_tokenManager", tokenManager);
        SetPrivateField(plugin, "_httpClient", httpClient);
        SetPrivateField(plugin, "_channelService", channelService);
        SetPrivateField(plugin, "_channelSelection", channelSelection);
        SetPrivateField(plugin, "_emojiManager", emojiManager);
        SetPrivateField(plugin, "_ui", ui);
        SetPrivateField(plugin, "_settings", settingsWindow);
        SetPrivateField(plugin, "_chatWindow", chatWindow);
        SetPrivateField(plugin, "_officerChatWindow", officerChatWindow);
        SetPrivateField(plugin, "_mainWindow", mainWindow);
        SetPrivateField(plugin, "_channelWatcher", channelWatcher);
        SetPrivateField(plugin, "_requestWatcher", requestWatcher);

        PingService.Instance = new PingService(httpClient, config, tokenManager);

        Assert.False(emojiManager.CanLoadCustom);

        var method = typeof(Plugin)
            .GetMethod("ValidateChannelSelectionAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;

        try
        {
            await (Task)method.Invoke(plugin, new object?[] { ChannelKind.Chat, string.Empty, "123456", null })!;

            Assert.Equal("guild-123", config.GuildId);
            pluginInterfaceMock.Verify(pi => pi.SavePluginConfig(config), Times.AtLeastOnce());
            Assert.True(emojiManager.CanLoadCustom);
            Assert.Contains(
                handler.Requests,
                request => request.Method == HttpMethod.Head && request.Uri?.AbsolutePath == "/api/ping"
            );
        }
        finally
        {
            Cleanup();
        }
    }

    [Fact]
    public async Task RevalidationClearsRejectedSelection()
    {
        Cleanup();
        PingService.Instance = null;

        var plugin = (Plugin)FormatterServices.GetUninitializedObject(typeof(Plugin));
        var services = new PluginServices();

        var pluginInterfaceMock = new Mock<IDalamudPluginInterface>();
        pluginInterfaceMock
            .Setup(p => p.SavePluginConfig(It.IsAny<Config>()));

        var logMock = new Mock<IPluginLog>();
        logMock.Setup(l => l.Info(It.IsAny<string>()));
        logMock.Setup(l => l.Warning(It.IsAny<string>()));
        logMock.Setup(l => l.Warning(It.IsAny<Exception>(), It.IsAny<string>()));
        logMock.Setup(l => l.Error(It.IsAny<string>()));

        var frameworkMock = new Mock<IFramework>();
        frameworkMock
            .Setup(f => f.RunOnTick(It.IsAny<Action>(), It.IsAny<FrameworkUpdatePriority>()))
            .Callback<Action, FrameworkUpdatePriority>((action, _) => action());

        var toastMock = new Mock<IToastGui>();

        SetPluginService(services, "PluginInterface", pluginInterfaceMock.Object);
        SetPluginService(services, "Log", logMock.Object);
        SetPluginService(services, "Framework", frameworkMock.Object);
        SetPluginService(services, "ToastGui", toastMock.Object);

        var config = new Config
        {
            ApiBaseUrl = "https://example.com",
            GuildId = "guild-new",
            Requests = false,
            Events = false,
            SyncedChat = false,
            EnableFcChat = false
        };
        config.Roles.Clear();

        var tokenManager = new TokenManager();
        var handler = new RevalidationTestHandler();
        var httpClient = new HttpClient(handler);

        var channelService = new ChannelService(config, httpClient, tokenManager);
        var channelSelection = new ChannelSelectionService(config);
        channelSelection.SetChannel(ChannelKind.Chat, config.GuildId, "chan-1");

        SetPrivateField(plugin, "_services", services);
        SetPrivateField(plugin, "_config", config);
        SetPrivateField(plugin, "_tokenManager", tokenManager);
        SetPrivateField(plugin, "_httpClient", httpClient);
        SetPrivateField(plugin, "_channelService", channelService);
        SetPrivateField(plugin, "_channelSelection", channelSelection);

        var method = typeof(Plugin)
            .GetMethod("RevalidateChannelSelectionsAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;

        try
        {
            await (Task)method.Invoke(
                plugin,
                new object?[]
                {
                    ChannelKeyHelper.NormalizeGuildId(config.GuildId),
                    ChannelKind.Event
                }
            )!;

            Assert.Equal(string.Empty, channelSelection.GetChannel(ChannelKind.Chat, config.GuildId));
            Assert.DoesNotContain(
                ChannelKeyHelper.BuildSelectionKey(config.GuildId, ChannelKind.Chat),
                config.ChannelSelections.Keys
            );
            Assert.Contains(
                handler.Requests,
                request => request.Method == HttpMethod.Get && request.Uri?.AbsolutePath == "/api/channels/chan-1/validate"
            );
            Assert.Equal("chan-1", handler.LastValidatedChannelId);
        }
        finally
        {
            Cleanup();
        }
    }

    private static void SetPrivateField(object instance, string fieldName, object value)
    {
        instance.GetType()
            .GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(instance, value);
    }

    private static void SetPluginService(PluginServices services, string propertyName, object value)
    {
        typeof(PluginServices)
            .GetProperty(propertyName, BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(services, value);
    }

    private static void Cleanup()
    {
        PingService.Instance = null;
        var instanceProperty = typeof(PluginServices)
            .GetProperty("Instance", BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);
        instanceProperty?.SetValue(null, null);
    }

    private sealed class ValidationTestHandler : HttpMessageHandler
    {
        public List<(HttpMethod Method, Uri? Uri)> Requests { get; } = new();

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add((request.Method, request.RequestUri));

            if (request.Method == HttpMethod.Head && request.RequestUri?.AbsolutePath == "/api/ping")
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
            }

            if (request.Method == HttpMethod.Get && request.RequestUri?.AbsolutePath == "/api/emojis/unicode")
            {
                return Task.FromResult(CreateJsonResponse("[]"));
            }

            if (request.Method == HttpMethod.Get && request.RequestUri?.AbsolutePath == "/api/emojis/guilds/guild-123")
            {
                return Task.FromResult(CreateJsonResponse("[]"));
            }

            if (request.Method == HttpMethod.Get && request.RequestUri?.AbsolutePath == "/api/channels/123456/validate")
            {
                return Task.FromResult(CreateJsonResponse("{\"ok\":true,\"guildId\":\"guild-123\",\"kind\":\"chat\",\"name\":\"General\"}"));
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        }

        private static HttpResponseMessage CreateJsonResponse(string json)
        {
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json)
            };
        }
    }

    private sealed class RevalidationTestHandler : HttpMessageHandler
    {
        public List<(HttpMethod Method, Uri? Uri)> Requests { get; } = new();

        public string? LastValidatedChannelId { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add((request.Method, request.RequestUri));

            if (request.Method == HttpMethod.Get &&
                request.RequestUri?.AbsolutePath.StartsWith("/api/channels/", StringComparison.Ordinal) == true &&
                request.RequestUri.AbsolutePath.EndsWith("/validate", StringComparison.Ordinal))
            {
                var segments = request.RequestUri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
                if (segments.Length >= 4)
                {
                    LastValidatedChannelId = segments[2];
                }

                const string json = "{\"ok\":false,\"guildId\":\"guild-old\",\"kind\":\"chat\"}";
                return Task.FromResult(CreateJsonResponse(json));
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        }

        private static HttpResponseMessage CreateJsonResponse(string json)
        {
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json)
            };
        }
    }
}
