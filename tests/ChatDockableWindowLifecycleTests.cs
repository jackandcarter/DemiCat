using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using DemiCatPlugin;
using Xunit;

public class ChatDockableWindowLifecycleTests
{
    [Fact]
    public void ChatAndOfficerNetworkingPersistsWhenDockToggled()
    {
        var config = new Config
        {
            ApiBaseUrl = "https://example.com",
            GuildId = "guild",
            SyncedChat = true,
            EnableFcChat = true,
            Officer = true,
            IsOfficerToken = true
        };

        using var httpClient = new HttpClient(new FakeHandler());
        var tokenManager = new TokenManager();
        var channelService = new ChannelService(config, httpClient, tokenManager);

        var chatWindow = new TestChatWindow(config, httpClient, tokenManager, channelService);
        var officerWindow = new TestOfficerChatWindow(config, httpClient, tokenManager, channelService);

        var chatDock = new ChatDockableWindow(
            config,
            "Free Company Chat",
            chatWindow,
            () => true,
            () => 1f,
            "Link DemiCat to use chat.",
            () => { });

        var officerDock = new OfficerChatDockableWindow(
            config,
            officerWindow,
            () => true,
            () => true,
            () => { });

        chatWindow.StartNetworking();
        officerWindow.StartNetworking();

        Assert.Equal(1, chatWindow.StartCalls);
        Assert.Equal(1, officerWindow.StartCalls);
        Assert.True(chatWindow.IsNetworkingActive);
        Assert.True(officerWindow.IsNetworkingActive);

        chatDock.IsOpen = true;
        chatDock.IsOpen = false;
        chatDock.IsOpen = true;

        officerDock.IsOpen = true;
        officerDock.IsOpen = false;
        officerDock.IsOpen = true;

        Assert.Equal(1, chatWindow.StartCalls);
        Assert.Equal(1, officerWindow.StartCalls);
        Assert.True(chatWindow.IsNetworkingActive);
        Assert.True(officerWindow.IsNetworkingActive);
    }

    private sealed class TestChatWindow : ChatWindow
    {
        public TestChatWindow(
            Config config,
            HttpClient httpClient,
            TokenManager tokenManager,
            ChannelService channelService)
            : base(config, httpClient, presence: null, tokenManager, channelService)
        {
        }

        public int StartCalls { get; private set; }

        public override void StartNetworking()
        {
            StartCalls++;
            MarkNetworkingStarted();
        }
    }

    private sealed class TestOfficerChatWindow : OfficerChatWindow
    {
        public TestOfficerChatWindow(
            Config config,
            HttpClient httpClient,
            TokenManager tokenManager,
            ChannelService channelService)
            : base(config, httpClient, presence: null, tokenManager, channelService)
        {
        }

        public int StartCalls { get; private set; }

        public override void StartNetworking()
        {
            StartCalls++;
            MarkNetworkingStarted();
        }
    }

    private sealed class FakeHandler : HttpMessageHandler
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
