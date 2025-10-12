using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using DemiCatPlugin;
using Xunit;

public class NotePadWindowTests
{
    [Fact]
    public void ReorderSections_RebuildsOrderAndMarksDirty()
    {
        using var fixture = new NotePadWindowFixture();
        var method = typeof(NotePadWindow).GetMethod(
            "ReorderSections",
            BindingFlags.Instance | BindingFlags.NonPublic) ?? throw new InvalidOperationException();

        method.Invoke(fixture.Window, new object[] { "s3", "s1" });

        var order = (List<string>)typeof(NotePadWindow)
            .GetField("_sectionOrder", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(fixture.Window)!;
        var dirty = (bool)typeof(NotePadWindow)
            .GetField("_sectionOrderDirty", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(fixture.Window)!;

        Assert.Equal(new[] { "s3", "s1", "s2" }, order);
        Assert.True(dirty);
    }

    [Fact]
    public void ReorderPages_ReordersInPlaceAndFlagsDirty()
    {
        using var fixture = new NotePadWindowFixture();
        var section = fixture.Service.Sections.First(s => s.Id == "s1");

        var method = typeof(NotePadWindow).GetMethod(
            "ReorderPages",
            BindingFlags.Instance | BindingFlags.NonPublic) ?? throw new InvalidOperationException();

        method.Invoke(fixture.Window, new object[] { section, "p3", "p1" });

        Assert.Equal(new[] { "p3", "p1", "p2" }, section.Pages.Select(p => p.Id));
        var dirty = (bool)typeof(NotePadWindow)
            .GetField("_pageOrderDirty", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(fixture.Window)!;
        Assert.True(dirty);
    }

    [Fact]
    public async Task HandleAutosave_SendsSaveRequestAfterDelay()
    {
        using var fixture = new NotePadWindowFixture();
        fixture.Handler.Responder = request =>
        {
            Assert.Equal(HttpMethod.Patch, request.Method);
            Assert.Equal("/api/notepad/pages/p1/content", request.RequestUri!.AbsolutePath);
            var body = request.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;
            Assert.Equal("Updated", root.GetProperty("content").GetString());
            Assert.Equal(1, root.GetProperty("version").GetInt32());

            var payload = JsonSerializer.Serialize(new NotePadPage
            {
                Id = "p1",
                Title = "Notes",
                Content = "Updated",
                Version = 2,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow,
            });
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(payload, Encoding.UTF8, "application/json")
            };
        };

        Assert.False(fixture.Window.IsReadOnly);
        fixture.SetSelection("s1", "p1");
        fixture.SetEditorState(content: "Updated", version: 1, dirty: true, lastEditUtc: DateTime.UtcNow.AddMinutes(-2));

        var method = typeof(NotePadWindow).GetMethod(
            "HandleAutosave",
            BindingFlags.Instance | BindingFlags.NonPublic) ?? throw new InvalidOperationException();

        method.Invoke(fixture.Window, Array.Empty<object>());

        var request = await fixture.Handler.WaitForRequestAsync(TimeSpan.FromSeconds(1));
        Assert.Equal(HttpMethod.Patch, request.Method);
        Assert.Equal("/api/notepad/pages/p1/content", request.RequestUri!.AbsolutePath);
        var body = await request.Content!.ReadAsStringAsync();
        using (var document = JsonDocument.Parse(body))
        {
            var root = document.RootElement;
            Assert.Equal("Updated", root.GetProperty("content").GetString());
            Assert.Equal(1, root.GetProperty("version").GetInt32());
        }

        await fixture.WaitForDirtyFlagAsync(expectedDirty: false);
        var currentVersion = (int)typeof(NotePadWindow)
            .GetField("_editorVersion", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(fixture.Window)!;
        Assert.Equal(2, currentVersion);
        Assert.False(fixture.Window.IsReadOnly);
    }

    [Fact]
    public async Task CreatePageAsync_UsesGlobalPagesEndpointAndPayload()
    {
        using var fixture = new NotePadWindowFixture();
        fixture.Handler.Responder = request =>
        {
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Equal("/api/notepad/pages", request.RequestUri!.AbsolutePath);
            var body = request.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;
            Assert.Equal("s1", root.GetProperty("sectionId").GetString());
            Assert.Equal("New Page", root.GetProperty("title").GetString());
            Assert.Equal(string.Empty, root.GetProperty("content").GetString());
            Assert.Equal("#123456", root.GetProperty("color").GetString());

            var responsePayload = JsonSerializer.Serialize(new NotePadPage
            {
                Id = "p-new",
                Title = "New Page",
                Content = string.Empty,
                Version = 0,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow,
                Color = "#123456"
            });

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(responsePayload, Encoding.UTF8, "application/json")
            };
        };

        var page = await fixture.Service.CreatePageAsync("s1", "New Page", CancellationToken.None);

        Assert.NotNull(page);
        Assert.Equal("p-new", page!.Id);
        var request = fixture.Handler.Requests.Last();
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal("/api/notepad/pages", request.RequestUri!.AbsolutePath);
    }

    [Fact]
    public async Task HandleAutosave_ReadOnlySkipsSaves()
    {
        using var fixture = new NotePadWindowFixture();
        fixture.Window.IsReadOnly = true;
        fixture.SetSelection("s1", "p1");
        fixture.SetEditorState(content: "Updated", version: 1, dirty: true, lastEditUtc: DateTime.UtcNow.AddMinutes(-2));

        var method = typeof(NotePadWindow).GetMethod(
            "HandleAutosave",
            BindingFlags.Instance | BindingFlags.NonPublic) ?? throw new InvalidOperationException();

        method.Invoke(fixture.Window, Array.Empty<object>());

        await Task.Delay(50);
        Assert.Empty(fixture.Handler.Requests);
        var dirty = (bool)typeof(NotePadWindow)
            .GetField("_dirty", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(fixture.Window)!;
        Assert.True(dirty);
    }

    [Fact]
    public async Task HandleAutosave_WithRecentEditDoesNotSave()
    {
        using var fixture = new NotePadWindowFixture();
        fixture.SetSelection("s1", "p1");
        fixture.SetEditorState(content: "Updated", version: 1, dirty: true, lastEditUtc: DateTime.UtcNow);

        var method = typeof(NotePadWindow).GetMethod(
            "HandleAutosave",
            BindingFlags.Instance | BindingFlags.NonPublic) ?? throw new InvalidOperationException();

        method.Invoke(fixture.Window, Array.Empty<object>());

        await Task.Delay(50);
        Assert.Empty(fixture.Handler.Requests);
    }

    private sealed class NotePadWindowFixture : IDisposable
    {
        public Config Config { get; }
        public NotePadService Service { get; }
        public NotePadWindow Window { get; }
        public TestHttpMessageHandler Handler { get; }

        public NotePadWindowFixture()
        {
            Config = new Config();
            Handler = new TestHttpMessageHandler();
            Service = new NotePadService(Config, new HttpClient(Handler), new TokenManager());

            var sections = new List<NotePadSection>
            {
                new()
                {
                    Id = "s1",
                    Name = "Alpha",
                    Color = "#123456",
                    Pages = new List<NotePadPage>
                    {
                        new()
                        {
                            Id = "p1",
                            Title = "Notes",
                            Content = "Initial",
                            Version = 1,
                            CreatedAt = DateTime.UtcNow,
                            UpdatedAt = DateTime.UtcNow
                        },
                        new()
                        {
                            Id = "p2",
                            Title = "Todo",
                            Content = "Items",
                            Version = 1,
                            CreatedAt = DateTime.UtcNow,
                            UpdatedAt = DateTime.UtcNow
                        },
                        new()
                        {
                            Id = "p3",
                            Title = "Archive",
                            Content = "Old",
                            Version = 1,
                            CreatedAt = DateTime.UtcNow,
                            UpdatedAt = DateTime.UtcNow
                        }
                    }
                },
                new()
                {
                    Id = "s2",
                    Name = "Beta",
                    Color = "#654321",
                    Pages = new List<NotePadPage>
                    {
                        new()
                        {
                            Id = "p4",
                            Title = "Log",
                            Content = "",
                            Version = 0,
                            CreatedAt = DateTime.UtcNow,
                            UpdatedAt = DateTime.UtcNow
                        }
                    }
                },
                new()
                {
                    Id = "s3",
                    Name = "Gamma",
                    Color = "#abcdef",
                    Pages = new List<NotePadPage>()
                }
            };

            var sectionsField = typeof(NotePadService).GetField("_sections", BindingFlags.Instance | BindingFlags.NonPublic)
                ?? throw new InvalidOperationException("_sections field not found");
            sectionsField.SetValue(Service, sections);

            Window = new NotePadWindow(Config, Service);
        }

        public void Dispose()
        {
            Window.Dispose();
        }

        public void SetSelection(string sectionId, string pageId)
        {
            typeof(NotePadWindow).GetField("_selectedSectionId", BindingFlags.Instance | BindingFlags.NonPublic)!
                .SetValue(Window, sectionId);
            typeof(NotePadWindow).GetField("_selectedPageId", BindingFlags.Instance | BindingFlags.NonPublic)!
                .SetValue(Window, pageId);
        }

        public void SetEditorState(string content, int version, bool dirty, DateTime lastEditUtc)
        {
            typeof(NotePadWindow).GetField("_editorContent", BindingFlags.Instance | BindingFlags.NonPublic)!
                .SetValue(Window, content);
            typeof(NotePadWindow).GetField("_editorVersion", BindingFlags.Instance | BindingFlags.NonPublic)!
                .SetValue(Window, version);
            typeof(NotePadWindow).GetField("_dirty", BindingFlags.Instance | BindingFlags.NonPublic)!
                .SetValue(Window, dirty);
            typeof(NotePadWindow).GetField("_lastEditUtc", BindingFlags.Instance | BindingFlags.NonPublic)!
                .SetValue(Window, lastEditUtc);
        }

        public async Task WaitForDirtyFlagAsync(bool expectedDirty)
        {
            var dirtyField = typeof(NotePadWindow).GetField("_dirty", BindingFlags.Instance | BindingFlags.NonPublic)!;
            var timeout = DateTime.UtcNow.AddMilliseconds(200);
            while (DateTime.UtcNow < timeout)
            {
                if ((bool)dirtyField.GetValue(Window)! == expectedDirty)
                {
                    return;
                }

                await Task.Delay(10);
            }

            Assert.Equal(expectedDirty, (bool)dirtyField.GetValue(Window)!);
        }
    }

    public sealed class TestHttpMessageHandler : HttpMessageHandler
    {
        public List<HttpRequestMessage> Requests { get; } = new();
        public Func<HttpRequestMessage, HttpResponseMessage>? Responder { get; set; }
        private TaskCompletionSource<HttpRequestMessage>? _tcs;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            _tcs?.TrySetResult(request);

            if (Responder != null)
            {
                return Task.FromResult(Responder(request));
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
        }

        public Task<HttpRequestMessage> WaitForRequestAsync(TimeSpan timeout)
        {
            _tcs = new TaskCompletionSource<HttpRequestMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
            if (Requests.Count > 0)
            {
                _tcs.TrySetResult(Requests.Last());
            }

            return Task.WhenAny(_tcs.Task, Task.Delay(timeout))
                .ContinueWith(task =>
                {
                    if (_tcs.Task.IsCompletedSuccessfully)
                    {
                        return _tcs.Task.Result;
                    }

                    throw new TimeoutException("Timed out waiting for HTTP request");
                }, TaskScheduler.Default);
        }
    }
}
