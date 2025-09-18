using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using DemiCatPlugin;
using DemiCatPlugin.Emoji;
using Xunit;

public class EmojiServiceTests
{
    private sealed class EmptyEmojiHandler : HttpMessageHandler
    {
        public int CallCount { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            CallCount++;
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"ok\": true, \"emojis\": []}", Encoding.UTF8, "application/json")
            };
            return Task.FromResult(response);
        }
    }

    [Fact]
    public async Task RefreshStopsAfterEmptyResponseWithoutRetryAfter()
    {
        var handler = new EmptyEmojiHandler();
        using var client = new HttpClient(handler);
        var tokens = new TokenManager();
        var config = new Config { ApiBaseUrl = "http://localhost" };
        var service = new EmojiService(client, tokens, config);

        var updates = 0;
        service.Updated += () => updates++;

        await service.RefreshAsync();

        Assert.Equal(1, handler.CallCount);
        Assert.Empty(service.Custom);
        Assert.Equal(1, updates);
    }
}
