using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Dalamud.Game.ClientState;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using DemiCatPlugin;
using DemiCatPlugin.SyncShell;
using Moq;
using Xunit;

public class SyncshellPublishFlowTests : IDisposable
{
    private readonly string _tempRoot;
    private readonly object _tokenLock = new();
    private readonly System.Reflection.FieldInfo _tokenField;
    private readonly TokenManager? _previousToken;

    public SyncshellPublishFlowTests()
    {
        _tempRoot = Directory.CreateTempSubdirectory().FullName;
        _tokenField = typeof(TokenManager).GetField("<Instance>k__BackingField", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)!;
        _previousToken = (TokenManager?)_tokenField.GetValue(null);
        var tokenManager = new TokenManager();
        _tokenField.SetValue(null, tokenManager);
    }

    [Fact]
    public async Task TriggerPublishUploadsMissingBlobsAndFinalizes()
    {
        var services = new PluginServices();
        var logMock = new Mock<IPluginLog>();
        var pluginInterfaceMock = CreatePluginInterfaceMock();
        typeof(PluginServices).GetProperty("PluginInterface", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .SetValue(services, pluginInterfaceMock.Object);
        typeof(PluginServices).GetProperty("Log", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .SetValue(services, logMock.Object);

        using var handler = new RecordingHandler();
        using var httpClient = new HttpClient(handler)
        {
            BaseAddress = new Uri("http://localhost")
        };

        var config = new Config
        {
            EnableSyncShell = true,
            ApiBaseUrl = "http://localhost"
        };

        using var blobStore = new BlobStore(pluginInterfaceMock.Object);
        var penumbra = new PenumbraIpc(pluginInterfaceMock.Object, logMock.Object);
        var clientStateMock = new Mock<IClientState>();
        var frameworkMock = new Mock<IFramework>();
        var service = new SyncShellService(
            config,
            TokenManager.Instance!,
            new SyncShellClient(httpClient, config, TokenManager.Instance!),
            blobStore,
            penumbra,
            logMock.Object,
            clientStateMock.Object,
            frameworkMock.Object);

        try
        {
            await service.Start();

            var statuses = new List<string>();
            service.StatusChanged += (_, _) => statuses.Add(service.Status);

            var payload = Encoding.UTF8.GetBytes("syncshell-test");
            var localPath = Path.Combine(_tempRoot, "blob.bin");
            await File.WriteAllBytesAsync(localPath, payload);
            var sha = Hasher.Sha256Bytes(payload);

            service.UpdateLocalAppearance(
                new[] { new SyncShellService.LocalBlobInfo("blob.bin", sha, payload.Length, localPath) },
                glamourerJson: null);

            await service.TriggerPublishAsync();
            await Task.Delay(50);

            Assert.Contains(statuses, status => status == "Publishing…");
            Assert.Contains(statuses, status => status.StartsWith("Uploading 1 blob", StringComparison.Ordinal));
            Assert.Equal("Active", service.Status);

            Assert.Contains(handler.Requests, r => r.Method == HttpMethod.Head && r.Path == "/api/syncshell/blobs");
            Assert.Contains(handler.Requests, r => r.Method == HttpMethod.Put && r.Path == "/api/syncshell/blobs");
            Assert.Equal(1, handler.CompleteCalls);
            Assert.True(handler.StoredBlobs.Contains(sha));
        }
        finally
        {
            await service.Stop();
            service.Dispose();
        }
    }

    [Fact]
    public async Task TriggerPublishSkipsUploadWhenBlobExists()
    {
        var services = new PluginServices();
        var logMock = new Mock<IPluginLog>();
        var pluginInterfaceMock = CreatePluginInterfaceMock();
        typeof(PluginServices).GetProperty("PluginInterface", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .SetValue(services, pluginInterfaceMock.Object);
        typeof(PluginServices).GetProperty("Log", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .SetValue(services, logMock.Object);

        var existingPayload = Encoding.UTF8.GetBytes("existing-blob");
        var sha = Hasher.Sha256Bytes(existingPayload);

        using var handler = new RecordingHandler(new Dictionary<string, byte[]>
        {
            [sha] = existingPayload
        });
        using var httpClient = new HttpClient(handler)
        {
            BaseAddress = new Uri("http://localhost")
        };

        var config = new Config
        {
            EnableSyncShell = true,
            ApiBaseUrl = "http://localhost"
        };

        using var blobStore = new BlobStore(pluginInterfaceMock.Object);
        var penumbra = new PenumbraIpc(pluginInterfaceMock.Object, logMock.Object);
        var clientStateMock = new Mock<IClientState>();
        var frameworkMock = new Mock<IFramework>();
        var service = new SyncShellService(
            config,
            TokenManager.Instance!,
            new SyncShellClient(httpClient, config, TokenManager.Instance!),
            blobStore,
            penumbra,
            logMock.Object,
            clientStateMock.Object,
            frameworkMock.Object);

        try
        {
            await service.Start();

            var localPath = Path.Combine(_tempRoot, "existing.bin");
            await File.WriteAllBytesAsync(localPath, existingPayload);

            service.UpdateLocalAppearance(
                new[] { new SyncShellService.LocalBlobInfo("existing.bin", sha, existingPayload.Length, localPath) },
                glamourerJson: null);

            await service.TriggerPublishAsync();
            await Task.Delay(50);

            Assert.Equal("Active", service.Status);
            Assert.DoesNotContain(handler.Requests, r => r.Method == HttpMethod.Put && r.Path == "/api/syncshell/blobs");
            Assert.Equal(1, handler.CompleteCalls);
        }
        finally
        {
            await service.Stop();
            service.Dispose();
        }
    }

    public void Dispose()
    {
        lock (_tokenLock)
        {
            _tokenField.SetValue(null, _previousToken);
        }

        try
        {
            Directory.Delete(_tempRoot, true);
        }
        catch
        {
        }
    }

    private Mock<IDalamudPluginInterface> CreatePluginInterfaceMock()
    {
        var mock = new Mock<IDalamudPluginInterface>();
        mock.Setup(i => i.GetPluginConfigDirectory()).Returns(_tempRoot);
        mock.Setup(i => i.GetIpcSubscriber<bool>(It.IsAny<string>())).Throws(new InvalidOperationException());
        mock.Setup(i => i.GetIpcSubscriber<string>(It.IsAny<string>())).Throws(new InvalidOperationException());
        mock.Setup(i => i.GetIpcSubscriber<string, object?>(It.IsAny<string>())).Throws(new InvalidOperationException());
        mock.Setup(i => i.GetIpcSubscriber<(int, int), object?>(It.IsAny<string>())).Throws(new InvalidOperationException());
        return mock;
    }

    private sealed record RecordedRequest(HttpMethod Method, string Path, string? Query);

    private sealed class RecordingHandler : HttpMessageHandler
    {
        private readonly object _gate = new();
        private readonly Dictionary<string, byte[]> _initialBlobs;

        public RecordingHandler()
            : this(new Dictionary<string, byte[]>())
        {
        }

        public RecordingHandler(Dictionary<string, byte[]> initialBlobs)
        {
            _initialBlobs = initialBlobs;
            foreach (var pair in initialBlobs)
            {
                StoredBlobs.Add(pair.Key);
                BlobContents[pair.Key] = pair.Value;
            }
        }

        public List<RecordedRequest> Requests { get; } = new();
        public HashSet<string> StoredBlobs { get; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, byte[]> BlobContents { get; } = new(StringComparer.OrdinalIgnoreCase);
        public int CompleteCalls { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            lock (_gate)
            {
                Requests.Add(new RecordedRequest(request.Method, path, request.RequestUri.Query));
            }

            if (request.Method == HttpMethod.Head && path == "/api/ping")
            {
                return new HttpResponseMessage(HttpStatusCode.OK);
            }

            if (request.Method == HttpMethod.Get && path == "/api/syncshell/memberships")
            {
                var payload = JsonSerializer.Serialize(new
                {
                    members = Array.Empty<object>(),
                    currentlySynced = Array.Empty<object>(),
                    pendingApprovals = Array.Empty<object>(),
                    invites = Array.Empty<object>()
                });
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(payload, Encoding.UTF8, "application/json")
                };
            }

            if (request.Method == HttpMethod.Post && path == "/api/syncshell/presence")
            {
                return new HttpResponseMessage(HttpStatusCode.OK);
            }

            if (path == "/api/syncshell/blobs" && request.Method == HttpMethod.Head)
            {
                var sha = GetSha(request.RequestUri);
                if (sha != null && StoredBlobs.Contains(sha))
                {
                    var response = new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new ByteArrayContent(Array.Empty<byte>())
                    };
                    response.Content.Headers.ContentLength = BlobContents.TryGetValue(sha, out var bytes) ? bytes.LongLength : 0;
                    return response;
                }

                return new HttpResponseMessage(HttpStatusCode.NotFound);
            }

            if (path == "/api/syncshell/blobs" && request.Method == HttpMethod.Put)
            {
                var sha = GetSha(request.RequestUri);
                var bytes = await request.Content!.ReadAsByteArrayAsync(cancellationToken);
                var digest = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
                if (!string.Equals(sha, digest, StringComparison.OrdinalIgnoreCase))
                {
                    return new HttpResponseMessage(HttpStatusCode.BadRequest);
                }

                StoredBlobs.Add(digest);
                BlobContents[digest] = bytes;
                return new HttpResponseMessage(HttpStatusCode.Created);
            }

            if (path == "/api/syncshell/manifest" && request.Method == HttpMethod.Post)
            {
                var body = await request.Content!.ReadAsStringAsync(cancellationToken);
                using var document = JsonDocument.Parse(body);
                var root = document.RootElement;
                var complete = root.TryGetProperty("complete", out var completeProp) && completeProp.GetBoolean();
                var blobs = root.GetProperty("appearance").GetProperty("blobs");

                var missing = new List<string>();
                foreach (var item in blobs.EnumerateArray())
                {
                    var hash = item.GetProperty("sha256").GetString();
                    if (string.IsNullOrWhiteSpace(hash))
                    {
                        continue;
                    }

                    if (!StoredBlobs.Contains(hash))
                    {
                        missing.Add(hash);
                    }
                }

                if (complete)
                {
                    CompleteCalls++;
                }

                var responsePayload = JsonSerializer.Serialize(new { missing });
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(responsePayload, Encoding.UTF8, "application/json")
                };
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound);
        }

        private static string? GetSha(Uri uri)
        {
            var query = uri.Query.TrimStart('?');
            foreach (var part in query.Split('&', StringSplitOptions.RemoveEmptyEntries))
            {
                var kv = part.Split('=', 2);
                if (kv.Length == 2 && kv[0] == "sha256")
                {
                    return Uri.UnescapeDataString(kv[1]);
                }
            }

            return null;
        }
    }
}
