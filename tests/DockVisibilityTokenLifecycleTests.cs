using System;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Runtime.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Dalamud.Game.ClientState;
using Dalamud.Game.Command;
using Dalamud.Game.Gui.Toast;
using Dalamud.Interface.Textures;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using DemiCatPlugin;
using Moq;
using Xunit;

public class DockVisibilityTokenLifecycleTests
{
    [Fact]
    public void DockVisibilityTracksTokenLifecycle()
    {
        var previousServices = PluginServices.Instance;
        var previousTokenManager = TokenManager.Instance;
        try
        {
            var services = new PluginServices();

            var pluginInterfaceMock = new Mock<IDalamudPluginInterface>();
            pluginInterfaceMock
                .Setup(pi => pi.SavePluginConfig(It.IsAny<IPluginConfiguration>()));

            var logMock = new Mock<IPluginLog>();
            var frameworkMock = new Mock<IFramework>();
            frameworkMock
                .Setup(f => f.RunOnTick(It.IsAny<Action>(), It.IsAny<FrameworkUpdatePriority>()))
                .Callback<Action, FrameworkUpdatePriority>((action, _) => action());

            var chatGuiMock = new Mock<IChatGui>();
            chatGuiMock.Setup(c => c.RemoveChatLinkHandler(It.IsAny<uint>()));

            var toastMock = new Mock<IToastGui>();

            SetPrivateProperty(services, "PluginInterface", pluginInterfaceMock.Object);
            SetPrivateProperty(services, "Framework", frameworkMock.Object);
            SetPrivateProperty(services, "Log", logMock.Object);
            SetPrivateProperty(services, "ChatGui", chatGuiMock.Object);
            SetPrivateProperty(services, "ToastGui", toastMock.Object);
            SetPrivateProperty(services, "CommandManager", Mock.Of<ICommandManager>());
            SetPrivateProperty(services, "ClientState", Mock.Of<IClientState>());
            SetPrivateProperty(services, "DataManager", Mock.Of<IDataManager>());
            SetPrivateProperty(services, "TextureProvider", Mock.Of<ITextureProvider>());
            SetPrivateProperty(services, "TextureReadbackProvider", Mock.Of<ITextureReadbackProvider>());

            var config = new Config
            {
                ApiBaseUrl = "https://example.com",
                GuildId = "guild",
                DockVisible = true,
                Requests = false,
                Events = false,
                SyncedChat = false,
                EnableFcChat = false,
                Officer = false,
                Templates = false,
                NotePadEnabled = false
            };

            var tokenManager = new TokenManager();
            var httpClient = new HttpClient(new StaticOkHandler());
            var channelService = new ChannelService(config, httpClient, tokenManager);
            var channelSelection = new ChannelSelectionService(config);
            var emojiManager = new EmojiManager(httpClient, tokenManager, config);
            var uiRenderer = new UiRenderer(config, httpClient, channelSelection, emojiManager);
            var chatWindow = new ChatWindow(config, httpClient, null, tokenManager, channelService);
            var officerWindow = new OfficerChatWindow(config, httpClient, null, tokenManager, channelService);
            var settingsWindow = new SettingsWindow(
                config,
                tokenManager,
                httpClient,
                () => Task.FromResult(true),
                () => Task.CompletedTask,
                logMock.Object,
                pluginInterfaceMock.Object);
            var notePadService = new NotePadService(config, httpClient, tokenManager);
            var notePadWindow = new NotePadWindow(config, notePadService);

            var mainWindow = new MainWindow(
                config,
                uiRenderer,
                chatWindow,
                officerWindow,
                settingsWindow,
                httpClient,
                channelService,
                channelSelection,
                emojiManager,
                notePadWindow);

            var channelWatcher = new ChannelWatcher(
                config,
                uiRenderer,
                mainWindow.EventCreateWindow,
                mainWindow.TemplatesWindow,
                chatWindow,
                officerWindow,
                tokenManager,
                httpClient);
            var requestWatcher = new RequestWatcher(config, httpClient, tokenManager);

            settingsWindow.MainWindow = mainWindow;
            settingsWindow.ChatWindow = chatWindow;
            settingsWindow.OfficerChatWindow = officerWindow;
            settingsWindow.ChannelWatcher = channelWatcher;
            settingsWindow.RequestWatcher = requestWatcher;
            settingsWindow.NotePadService = notePadService;

            PingService.Instance = new PingService(httpClient, config, tokenManager);

            var plugin = (Plugin)FormatterServices.GetUninitializedObject(typeof(Plugin));
            SetPrivateField(plugin, "_services", services);
            SetPrivateField(plugin, "_config", config);
            SetPrivateField(plugin, "_tokenManager", tokenManager);
            SetPrivateField(plugin, "_httpClient", httpClient);
            SetPrivateField(plugin, "_channelService", channelService);
            SetPrivateField(plugin, "_channelSelection", channelSelection);
            SetPrivateField(plugin, "_emojiManager", emojiManager);
            SetPrivateField(plugin, "_ui", uiRenderer);
            SetPrivateField(plugin, "_settings", settingsWindow);
            SetPrivateField(plugin, "_chatWindow", chatWindow);
            SetPrivateField(plugin, "_officerChatWindow", officerWindow);
            SetPrivateField(plugin, "_mainWindow", mainWindow);
            SetPrivateField(plugin, "_channelWatcher", channelWatcher);
            SetPrivateField(plugin, "_requestWatcher", requestWatcher);
            SetPrivateField(plugin, "_notePadService", notePadService);
            SetPrivateField(plugin, "_notePadWindow", notePadWindow);

            mainWindow.IsOpen = true;
            Assert.True(config.DockVisible);

            var handleUnlinked = typeof(Plugin)
                .GetMethod("HandleTokenUnlinked", BindingFlags.Instance | BindingFlags.NonPublic)!;
            handleUnlinked.Invoke(plugin, new object?[] { null });

            Assert.False(mainWindow.IsOpen);
            Assert.True(config.DockVisible);

            var handleLinked = typeof(Plugin)
                .GetMethod("HandleTokenLinked", BindingFlags.Instance | BindingFlags.NonPublic)!;
            handleLinked.Invoke(plugin, Array.Empty<object>());

            Assert.True(mainWindow.IsOpen);
            Assert.True(config.DockVisible);
        }
        finally
        {
            typeof(PluginServices)
                .GetProperty("Instance", BindingFlags.Static | BindingFlags.NonPublic)!
                .SetValue(null, previousServices);
            typeof(TokenManager)
                .GetProperty("Instance", BindingFlags.Static | BindingFlags.Public)!
                .SetValue(null, previousTokenManager);
            PingService.Instance = null;
        }
    }

    private static void SetPrivateField(object instance, string name, object? value)
    {
        instance.GetType()
            .GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(instance, value);
    }

    private static void SetPrivateProperty(object instance, string name, object? value)
    {
        instance.GetType()
            .GetProperty(name, BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(instance, value);
    }

    private sealed class StaticOkHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("[]")
            };
            return Task.FromResult(response);
        }
    }
}
