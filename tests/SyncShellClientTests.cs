using System;
using System.Reflection;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using DemiCatPlugin;
using DemiCatPlugin.SyncShell;
using Moq;
using Xunit;

public class SyncShellClientTests
{
    [Fact]
    public async Task HandleWantAsync_SuppressesUploadsWhenBudgetExhausted()
    {
        var previousTokenManager = TokenManager.Instance;
        var tokenManager = new TokenManager();
        try
        {
            using var client = CreateClient(tokenManager);
            await InvokeHandleWantAsync(client, BuildWantPayload("peer-one", 0, "blob-one"));

            var uploadState = GetUploadState(client, "peer-one");
            Assert.True(GetIsBudgetExhausted(uploadState));
            Assert.Equal(0, GetTotal(uploadState));
            Assert.False(TryReadOutgoing(client));
        }
        finally
        {
            typeof(TokenManager).GetProperty("Instance", BindingFlags.Static | BindingFlags.Public)!
                .SetValue(null, previousTokenManager);
        }
    }

    [Fact]
    public async Task HandleWantAsync_ResumesUploadsWhenBudgetRestored()
    {
        var previousTokenManager = TokenManager.Instance;
        var tokenManager = new TokenManager();
        try
        {
            using var client = CreateClient(tokenManager);
            await InvokeHandleWantAsync(client, BuildWantPayload("peer-one", 0, "blob-one"));
            Assert.True(GetIsBudgetExhausted(GetUploadState(client, "peer-one")));

            await InvokeHandleWantAsync(client, BuildWantPayload("peer-one", 2048, "blob-one"));

            var uploadState = GetUploadState(client, "peer-one");
            Assert.False(GetIsBudgetExhausted(uploadState));
            Assert.Equal(1, GetTotal(uploadState));
            Assert.True(TryReadOutgoing(client));
        }
        finally
        {
            typeof(TokenManager).GetProperty("Instance", BindingFlags.Static | BindingFlags.Public)!
                .SetValue(null, previousTokenManager);
        }
    }

    private static SyncClient CreateClient(TokenManager tokenManager)
    {
        var config = new Config { ApiBaseUrl = "http://localhost" };
        var resolver = Mock.Of<IResolver>();
        var blobStore = Mock.Of<IBlobStore>();
        return new SyncClient(config, tokenManager, resolver, blobStore, SyncLimits.Default);
    }

    private static async Task InvokeHandleWantAsync(SyncClient client, string json)
    {
        var method = typeof(SyncClient).GetMethod("HandleWantAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
        using var document = JsonDocument.Parse(json);
        var element = document.RootElement.Clone();
        var task = (Task)method.Invoke(client, new object[] { element, CancellationToken.None })!;
        await task.ConfigureAwait(false);
    }

    private static object GetUploadState(SyncClient client, string peerId)
    {
        var uploadsField = typeof(SyncClient).GetField("_uploads", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var uploads = uploadsField.GetValue(client)!;
        var tryGetValue = uploads.GetType().GetMethod("TryGetValue")!;
        var arguments = new object?[] { peerId, null };
        var found = (bool)tryGetValue.Invoke(uploads, arguments)!;
        Assert.True(found);
        return arguments[1]!;
    }

    private static bool GetIsBudgetExhausted(object uploadState)
    {
        var property = uploadState.GetType().GetProperty("IsBudgetExhausted", BindingFlags.Instance | BindingFlags.Public)!;
        return (bool)property.GetValue(uploadState)!;
    }

    private static int GetTotal(object uploadState)
    {
        var property = uploadState.GetType().GetProperty("Total", BindingFlags.Instance | BindingFlags.Public)!;
        return (int)property.GetValue(uploadState)!;
    }

    private static bool TryReadOutgoing(SyncClient client)
    {
        var channelField = typeof(SyncClient).GetField("_outgoingBlobs", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var channel = channelField.GetValue(client)!;
        var readerProperty = channel.GetType().GetProperty("Reader", BindingFlags.Instance | BindingFlags.Public)!;
        var reader = readerProperty.GetValue(channel)!;
        var tryRead = reader.GetType().GetMethod("TryRead")!;
        var arguments = new object?[] { null };
        var result = (bool)tryRead.Invoke(reader, arguments)!;
        return result;
    }

    private static string BuildWantPayload(string peerId, long throttleAfterBytes, params string[] blobs)
    {
        var payload = new
        {
            peerId,
            want = new
            {
                blobs,
                chunks = Array.Empty<object>(),
            },
            limits = new
            {
                budget = new
                {
                    throttleAfterBytes,
                },
            },
        };

        return JsonSerializer.Serialize(payload);
    }
}
