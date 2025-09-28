using System;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Dalamud.Game.Gui.Toast;
using Dalamud.Plugin.Services;
using DemiCatPlugin;
using DemiCatPlugin.Emoji;
using Moq;
using Serilog;
using Serilog.Events;
using Xunit;

[CollectionDefinition("UiRenderer Shutdown Tests", DisableParallelization = true)]
public class UiRendererShutdownTestsCollection
{
}

[Collection("UiRenderer Shutdown Tests")]
public class UiRendererShutdownTests
{
    [Fact]
    public async Task StopNetworking_SwallowsShutdownExceptions()
    {
        await using var context = UiRendererTestContext.Create();

        await context.Handler.WaitForEmbedsAsync(TimeSpan.FromSeconds(2));

        context.Ui.StopNetworking();
        context.Handler.CompleteEmbeds(new TaskCanceledException("shutdown"));

        await context.WaitForPollLoopAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(0, context.GetFailureCount());
        Assert.Empty(context.GetErrorToasts());
    }

    [Fact]
    public async Task DisposeAsync_SwallowsShutdownExceptions()
    {
        await using var context = UiRendererTestContext.Create();

        await context.Handler.WaitForEmbedsAsync(TimeSpan.FromSeconds(2));

        var disposeTask = context.Ui.DisposeAsync();
        context.Handler.CompleteEmbeds(new ObjectDisposedException("HttpClient"));

        await context.WaitForPollLoopAsync(TimeSpan.FromSeconds(2));
        await disposeTask;

        Assert.Equal(0, context.GetFailureCount());
        Assert.Empty(context.GetErrorToasts());
    }

    private sealed class UiRendererTestContext : IAsyncDisposable
    {
        private readonly PluginServices? _previousServices;
        private readonly TokenManager? _previousToken;
        private readonly EmojiManager _emojiManager;
        private readonly PluginServices _services;
        private readonly TokenManager _tokenManager;

        private UiRendererTestContext(
            UiRenderer ui,
            TestHttpHandler handler,
            Task pollLoopTask,
            Mock<IToastGui> toastMock,
            PluginServices services,
            PluginServices? previousServices,
            TokenManager tokenManager,
            TokenManager? previousToken,
            EmojiManager emojiManager,
            CancellationTokenSource pollCts)
        {
            Ui = ui;
            Handler = handler;
            PollLoopTask = pollLoopTask;
            ToastMock = toastMock;
            PollCts = pollCts;
            _services = services;
            _previousServices = previousServices;
            _emojiManager = emojiManager;
            _tokenManager = tokenManager;
            _previousToken = previousToken;
        }

        public UiRenderer Ui { get; }
        public TestHttpHandler Handler { get; }
        public Task PollLoopTask { get; }
        public Mock<IToastGui> ToastMock { get; }
        public CancellationTokenSource PollCts { get; }
        public static UiRendererTestContext Create()
        {
            var previousServices = PluginServices.Instance;
            var services = new PluginServices();

            var toastMock = new Mock<IToastGui>(MockBehavior.Loose);
            var framework = new ImmediateFramework();
            var log = new NullPluginLog();

            SetServiceProperty(services, "Framework", framework);
            SetServiceProperty(services, "Log", log);
            SetServiceProperty(services, "ToastGui", toastMock.Object);

            var previousToken = TokenManager.Instance;
            var tokenManager = new TokenManager();

            var config = new Config
            {
                ApiBaseUrl = "https://unit.test",
                Enabled = true,
                Events = true,
                PollIntervalSeconds = 0
            };

            var selection = new ChannelSelectionService(config);
            var emojiManager = new EmojiManager(new HttpClient(new StubEmojiHandler()), tokenManager, config);
            var handler = new TestHttpHandler();
            var httpClient = new HttpClient(handler);
            var ui = new UiRenderer(config, httpClient, selection, emojiManager);

            var pollCts = new CancellationTokenSource();
            SetPrivateField(ui, "_networkingActive", true);
            SetPrivateField(ui, "_pollCts", pollCts);
            SetPrivateField(ui, "_nextWebSocketAttempt", DateTime.MaxValue);

            var pollLoop = typeof(UiRenderer)
                .GetMethod("PollLoop", BindingFlags.Instance | BindingFlags.NonPublic)!;
            var pollLoopTask = (Task)pollLoop.Invoke(ui, new object[] { pollCts.Token })!;

            return new UiRendererTestContext(
                ui,
                handler,
                pollLoopTask,
                toastMock,
                services,
                previousServices,
                tokenManager,
                previousToken,
                emojiManager,
                pollCts);
        }

        public async Task WaitForPollLoopAsync(TimeSpan timeout)
        {
            using var cts = new CancellationTokenSource(timeout);
            await PollLoopTask.WaitAsync(cts.Token);
        }

        public int GetFailureCount()
            => (int)typeof(UiRenderer)
                .GetField("_failureCount", BindingFlags.Instance | BindingFlags.NonPublic)!
                .GetValue(Ui)!;

        public string[] GetErrorToasts()
            => ToastMock.Invocations
                .Where(i => i.Method.Name == nameof(IToastGui.ShowError))
                .Select(i => i.Arguments.FirstOrDefault() as string ?? string.Empty)
                .ToArray();

        public async ValueTask DisposeAsync()
        {
            Handler.CompleteEmbeds();
            try
            {
                PollCts.Cancel();
            }
            catch
            {
            }

            try
            {
                await PollLoopTask.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }

            try
            {
                await Ui.DisposeAsync();
            }
            catch
            {
            }

            _emojiManager.Dispose();
            SetPluginServicesInstance(_previousServices);
            SetTokenManagerInstance(_previousToken);
            GC.KeepAlive(_services);
            GC.KeepAlive(_tokenManager);
        }

        private static void SetPrivateField(object instance, string name, object value)
            => instance.GetType()
                .GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!
                .SetValue(instance, value);

        private static void SetServiceProperty(PluginServices services, string name, object value)
            => typeof(PluginServices)
                .GetProperty(name, BindingFlags.Instance | BindingFlags.NonPublic)!
                .SetValue(services, value);

        private static void SetPluginServicesInstance(PluginServices? value)
            => typeof(PluginServices)
                .GetProperty("Instance", BindingFlags.Static | BindingFlags.NonPublic)!
                .GetSetMethod(true)!
                .Invoke(null, new object?[] { value });

        private static void SetTokenManagerInstance(TokenManager? value)
            => typeof(TokenManager)
                .GetProperty("Instance", BindingFlags.Static | BindingFlags.Public)!
                .GetSetMethod(true)!
                .Invoke(null, new object?[] { value });
    }

    private sealed class TestHttpHandler : HttpMessageHandler
    {
        private readonly TaskCompletionSource<bool> _embedsStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource<Exception?> _embedsCompletion = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task WaitForEmbedsAsync(TimeSpan timeout)
        {
            using var cts = new CancellationTokenSource(timeout);
            await _embedsStarted.Task.WaitAsync(cts.Token);
        }

        public void CompleteEmbeds(Exception? exception = null)
        {
            _embedsCompletion.TrySetResult(exception);
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.RequestUri != null && request.RequestUri.AbsolutePath.EndsWith("/api/embeds", StringComparison.OrdinalIgnoreCase))
            {
                _embedsStarted.TrySetResult(true);
                var exception = await _embedsCompletion.Task.ConfigureAwait(false);
                if (exception != null)
                {
                    throw exception;
                }

                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("[]")
                };
            }

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{}")
            };
        }
    }

    private sealed class ImmediateFramework : IFramework
    {
        public event FrameworkUpdateDelegate? Update
        {
            add { }
            remove { }
        }

        public FrameworkUpdateType CurrentUpdateType => FrameworkUpdateType.None;

        public void RunOnTick(Action action, FrameworkUpdatePriority priority = FrameworkUpdatePriority.Normal) => action();
    }

    private sealed class NullPluginLog : IPluginLog
    {
        public ILogger Logger { get; } = new LoggerConfiguration().CreateLogger();

        public LogEventLevel MinimumLogLevel { get; set; }

        public void Verbose(string messageTemplate, params object[] propertyValues)
        {
        }

        public void Verbose(Exception? exception, string messageTemplate, params object[] propertyValues)
        {
        }

        public void Debug(string messageTemplate, params object[] propertyValues)
        {
        }

        public void Debug(Exception? exception, string messageTemplate, params object[] propertyValues)
        {
        }

        public void Info(string messageTemplate, params object[] propertyValues)
        {
        }

        public void Info(Exception? exception, string messageTemplate, params object[] propertyValues)
        {
        }

        public void Warning(string messageTemplate, params object[] propertyValues)
        {
        }

        public void Warning(Exception? exception, string messageTemplate, params object[] propertyValues)
        {
        }

        public void Error(string messageTemplate, params object[] propertyValues)
        {
        }

        public void Error(Exception? exception, string messageTemplate, params object[] propertyValues)
        {
        }

        public void Fatal(string messageTemplate, params object[] propertyValues)
        {
        }

        public void Fatal(Exception? exception, string messageTemplate, params object[] propertyValues)
        {
        }
    }

    private sealed class StubEmojiHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{}")
            });
    }
}
