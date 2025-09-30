using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Dalamud.Plugin.Services;
using DemiCatPlugin;
using Xunit;

public class ApiHelpersTests
{
    private sealed class TestFramework : IFramework
    {
        public event FrameworkUpdateDelegate? Update { add { } remove { } }

        public FrameworkUpdateType CurrentUpdateType => FrameworkUpdateType.None;

        public Task RunOnTick(System.Action action, FrameworkUpdatePriority priority = FrameworkUpdatePriority.Normal)
        {
            action();
            return Task.CompletedTask;
        }

        public Task RunOnTick(Func<Task> action, FrameworkUpdatePriority priority = FrameworkUpdatePriority.Normal) => action();
    }

    private sealed class TestLog : IPluginLog
    {
        public void Verbose(string message)
        {
        }

        public void Verbose(string message, System.Exception exception)
        {
        }

        public void Debug(string message)
        {
        }

        public void Debug(string message, System.Exception exception)
        {
        }

        public void Info(string message)
        {
        }

        public void Info(string message, System.Exception exception)
        {
        }

        public void Warning(string message)
        {
        }

        public void Warning(string message, System.Exception exception)
        {
        }

        public void Error(string message)
        {
        }

        public void Error(System.Exception exception, string message)
        {
        }

        public void Fatal(string message)
        {
        }

        public void Fatal(System.Exception exception, string message)
        {
        }
    }

    private sealed class FlakyHandler : HttpMessageHandler
    {
        private int _calls;
        private readonly List<string> _bodies = new();

        public int Calls => _calls;
        public IReadOnlyList<string> Bodies => _bodies;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            _calls++;
            if (request.Content != null)
            {
                _bodies.Add(await request.Content.ReadAsStringAsync());
            }

            if (_calls == 1)
            {
                throw new HttpRequestException("Transient failure");
            }

            return new HttpResponseMessage(HttpStatusCode.OK);
        }
    }

    private static PluginServices? SetupPluginServices()
    {
        var previous = PluginServices.Instance;
        var services = new PluginServices();
        typeof(PluginServices).GetProperty("Framework", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(services, new TestFramework());
        typeof(PluginServices).GetProperty("Log", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(services, new TestLog());
        return previous;
    }

    private static void RestorePluginServices(PluginServices? previous)
        => typeof(PluginServices).GetProperty("Instance", BindingFlags.Static | BindingFlags.NonPublic)!
            .SetValue(null, previous);

    [Fact]
    public async Task SendWithRetries_RetriesAfterHttpRequestException()
    {
        var previousServices = SetupPluginServices();
        try
        {
            var handler = new FlakyHandler();
            using var client = new HttpClient(handler);
            const string payload = "{\"message\":\"hello\"}";
            var factoryCalls = 0;

            var response = await ApiHelpers.SendWithRetries(() =>
            {
                factoryCalls++;
                var request = new HttpRequestMessage(HttpMethod.Post, "https://example.com/api")
                {
                    Content = new StringContent(payload, Encoding.UTF8, "application/json")
                };
                return request;
            }, client);

            Assert.NotNull(response);
            Assert.True(response!.IsSuccessStatusCode);
            Assert.Equal(2, handler.Calls);
            Assert.Equal(2, factoryCalls);
            Assert.Equal(2, handler.Bodies.Count);
            Assert.All(handler.Bodies, body => Assert.Equal(payload, body));
            response.Dispose();
        }
        finally
        {
            RestorePluginServices(previousServices);
        }
    }
}
