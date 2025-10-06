using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Dalamud.Plugin.Services;
using DemiCatPlugin;
using DemiCatPlugin.Emoji;
using DemiCat.UI;
using DiscordHelper;
using Xunit;

public class TemplateButtonRoundTripTests
{
    private class StubHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK));
    }

    private sealed class CapturingHandler : HttpMessageHandler
    {
        public string? Body { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Body = request.Content == null ? null : await request.Content.ReadAsStringAsync();
            return new HttpResponseMessage(HttpStatusCode.OK);
        }
    }

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
        public void Verbose(string message) { }
        public void Verbose(string message, System.Exception exception) { }
        public void Debug(string message) { }
        public void Debug(string message, System.Exception exception) { }
        public void Info(string message) { }
        public void Info(string message, System.Exception exception) { }
        public void Warning(string message) { }
        public void Warning(string message, System.Exception exception) { }
        public void Error(string message) { }
        public void Error(System.Exception exception, string message) { }
        public void Fatal(string message) { }
        public void Fatal(System.Exception exception, string message) { }
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

    private static TemplatesWindow CreateWindow(ButtonRows state)
    {
        var config = new Config();
        var http = new HttpClient(new StubHandler());
        var tokenManager = new TokenManager();
        var channelService = new ChannelService(config, http, tokenManager);
        var selection = new ChannelSelectionService(config);
        var emojiManager = new EmojiManager(http, tokenManager, config);
        var window = new TemplatesWindow(config, http, channelService, selection, emojiManager, tokenManager);
        var field = typeof(TemplatesWindow).GetField("_buttonRows", BindingFlags.NonPublic | BindingFlags.Instance);
        field!.SetValue(window, state);
        return window;
    }

    [Fact]
    public void ToEmbedDto_UsesButtonLabels()
    {
        var rows = new ButtonRows(new() { new() { new ButtonData { Label = "Join" } } });
        rows.SetLabel(0, 0, "Signup");

        var window = CreateWindow(rows);
        var tmpl = new Template { Title = "T", Description = "D" };

        var embed = window.ToEmbedDto(tmpl);
        var btn = Assert.Single(embed.Buttons!);
        Assert.Equal("Signup", btn.Label);
        Assert.Equal(ButtonStyle.Primary, btn.Style);
        Assert.Null(btn.Emoji);
        Assert.Null(btn.MaxSignups);
        Assert.Equal(ButtonSizeHelper.ComputeWidth("Signup"), btn.Width);
    }

    [Fact]
    public void BuildButtonsPayload_ProducesMetadata()
    {
        var rows = new ButtonRows(new() { new() { new ButtonData { Label = "Join" } } });
        var window = CreateWindow(rows);

        var payload = window.BuildButtonsPayload(new Template());
        var btn = Assert.Single(payload);
        Assert.Equal("Join", btn.label);
        Assert.Equal((int)ButtonStyle.Primary, btn.style);
        Assert.Equal(0, btn.rowIndex);

        Assert.Null(btn.emoji);
        Assert.Null(btn.maxSignups);
        Assert.Equal(ButtonSizeHelper.ComputeWidth("Join"), btn.width);
        Assert.Null(btn.url);
    }

    [Fact]
    public void BuildButtonsPayload_PreservesTemplateButtonUrl()
    {
        var rows = new ButtonRows(new()
        {
            new() { new ButtonData { Tag = "link", Label = "Guide" } }
        });
        var window = CreateWindow(rows);

        var tmpl = new Template
        {
            Buttons = new List<Template.TemplateButton>
            {
                new Template.TemplateButton { Tag = "link", Label = "Guide", Url = "https://example.com", Style = ButtonStyle.Link }
            }
        };

        var payload = window.BuildButtonsPayload(tmpl);
        var btn = Assert.Single(payload);
        Assert.Equal("https://example.com", btn.url);
    }

    [Fact]
    public void BuildButtonsPayload_IgnoresEmptyTemplateTags()
    {
        var rows = new ButtonRows(new() { new() { new ButtonData { Label = "Join" } } });
        var window = CreateWindow(rows);

        var tmpl = new Template
        {
            Buttons = new List<Template.TemplateButton>
            {
                new Template.TemplateButton { Label = "Join", Tag = string.Empty }
            }
        };

        var payload = window.BuildButtonsPayload(tmpl);
        var btn = Assert.Single(payload);
        Assert.Equal("Join", btn.label);
    }

    [Fact]
    public void BuildButtonsPayload_AssignsDistinctIdsForDuplicateLabels()
    {
        var rows = new ButtonRows(new()
        {
            new() { new ButtonData { Label = "Join" }, new ButtonData { Label = "Join" } },
            new() { new ButtonData { Label = "Join" } }
        });
        var window = CreateWindow(rows);

        var payload = window.BuildButtonsPayload(new Template());
        Assert.Equal(3, payload.Count);
        Assert.NotEqual(payload[0].customId, payload[1].customId);
        Assert.NotEqual(payload[0].customId, payload[2].customId);
        Assert.NotEqual(payload[1].customId, payload[2].customId);
    }

    [Fact]
    public void BuildButtonsPayload_ComputesWidthFromLabel()
    {
        var rows = new ButtonRows(new() { new() { new ButtonData { Label = "Short" }, new ButtonData { Label = "Much Longer Label" } } });
        var window = CreateWindow(rows);

        var payload = window.BuildButtonsPayload(new Template());
        Assert.Equal(ButtonSizeHelper.ComputeWidth("Short"), payload[0].width);
        Assert.Equal(ButtonSizeHelper.ComputeWidth("Much Longer Label"), payload[1].width);
        Assert.True(payload[1].width > payload[0].width);
    }

    [Fact]
    public async Task PostTemplate_MentionsRemainStrings()
    {
        var previousServices = SetupPluginServices();
        var previousTokenManager = TokenManager.Instance;
        try
        {
            var handler = new CapturingHandler();
            using var http = new HttpClient(handler);
            var config = new Config
            {
                ApiBaseUrl = "http://localhost",
                EventChannelId = "channel-1"
            };
            var tokenManager = new TokenManager();
            var channelService = new ChannelService(config, http, tokenManager);
            var selection = new ChannelSelectionService(config);
            using var emojiManager = new EmojiManager(http, tokenManager, config);
            var window = new TemplatesWindow(config, http, channelService, selection, emojiManager, tokenManager);
            var template = new Template { Title = "T", Description = "D" };

            var templatesField = typeof(TemplatesWindow).GetField("_templates", BindingFlags.NonPublic | BindingFlags.Instance)!;
            var templateList = (IList)templatesField.GetValue(window)!;
            var templateItemType = typeof(TemplatesWindow).GetNestedType("TemplateItem", BindingFlags.NonPublic)!;
            var templateItem = Activator.CreateInstance(templateItemType!, "tmpl-id", template)!;
            templateList.Add(templateItem);

            typeof(TemplatesWindow).GetField("_selectedIndex", BindingFlags.NonPublic | BindingFlags.Instance)!
                .SetValue(window, 0);

            var mentionsField = typeof(TemplatesWindow).GetField("_mentions", BindingFlags.NonPublic | BindingFlags.Instance)!;
            var mentions = (HashSet<string>)mentionsField.GetValue(window)!;
            mentions.Clear();
            mentions.Add("123456789012345678");
            mentions.Add("987654321098765432");

            var postMethod = typeof(TemplatesWindow).GetMethod("PostTemplate", BindingFlags.NonPublic | BindingFlags.Instance)!;
            var task = (Task)postMethod.Invoke(window, new object[] { template })!;
            await task.ConfigureAwait(false);

            Assert.NotNull(handler.Body);
            using var doc = JsonDocument.Parse(handler.Body!);
            var root = doc.RootElement;

            Assert.True(root.TryGetProperty("mentions", out var mentionsElement));
            Assert.Equal(JsonValueKind.Array, mentionsElement.ValueKind);

            var actual = mentionsElement.EnumerateArray()
                .Select(element =>
                {
                    Assert.Equal(JsonValueKind.String, element.ValueKind);
                    return element.GetString();
                })
                .Where(value => value != null)
                .OrderBy(value => value)
                .ToList();

            var expected = new[] { "123456789012345678", "987654321098765432" }
                .OrderBy(value => value)
                .ToList();

            Assert.Equal(expected, actual);
        }
        finally
        {
            RestorePluginServices(previousServices);
            var tokenField = typeof(TokenManager).GetField("<Instance>k__BackingField", BindingFlags.Static | BindingFlags.NonPublic)!;
            tokenField.SetValue(null, previousTokenManager);
        }
    }
}
