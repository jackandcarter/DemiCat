using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using DemiCatPlugin;
using Dalamud.Plugin.Services;
using Xunit;

public class ChatWindowMentionTests
{
    [Fact]
    public void ReplaceMentionTokens_ReplacesAllMentionTypes()
    {
        var mentions = new List<DiscordMentionDto>
        {
            new() { Id = "1", Name = "Alice", Type = "user" },
            new() { Id = "2", Name = "Admins", Type = "role" },
            new() { Id = "3", Name = "general", Type = "channel" }
        };
        var text = "<@1> <@&2> <#3>";
        var result = ChatWindow.ReplaceMentionTokens(text, mentions);
        Assert.Equal("@Alice @Admins #general", result);
    }

    [Fact]
    public void ResolveDetailed_WithInsertedMentionTokens_PreservesContentAndMentions()
    {
        var presences = new[] { new PresenceDto { Id = "1", Name = "Alice" } };
        var roles = new[] { new RoleDto { Id = "2", Name = "Admins" } };
        var text = "Hello <@1> and <@&2>";

        var result = MentionResolver.ResolveDetailed(text, presences, roles);

        Assert.Equal(text, result.Content);
        Assert.Collection(result.Mentions,
            mention =>
            {
                Assert.Equal("1", mention.Id);
                Assert.Equal("Alice", mention.Name);
                Assert.Equal("user", mention.Type);
            },
            mention =>
            {
                Assert.Equal("2", mention.Id);
                Assert.Equal("Admins", mention.Name);
                Assert.Equal("role", mention.Type);
            });
    }

    [Fact]
    public void InsertMentionCandidate_InsertsUserMentionAndResolvesTokens()
    {
        SetupServices();
        var config = new Config { ApiBaseUrl = "http://localhost" };
        var handler = new DummyHandler();
        var httpClient = new HttpClient(handler);
        var tokenManager = new TokenManager();
        var channelService = new ChannelService(config, httpClient, tokenManager);
        var presence = new DiscordPresenceService(config, httpClient);
        var presenceList = (List<PresenceDto>)typeof(DiscordPresenceService)
            .GetField("_presences", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(presence)!;
        presenceList.Add(new PresenceDto { Id = "1", Name = "Alice" });

        var window = new ChatWindow(config, httpClient, presence, tokenManager, channelService);

        SetInput(window, "Hello @", 7, 7);

        var state = EnsureMentionState(window);
        state.GetType().GetField("TokenStart")!.SetValue(state, 6);
        state.GetType().GetField("TokenEnd")!.SetValue(state, 7);

        var candidate = CreateMentionCandidate("User", "1", "Alice");
        InvokeInsertMentionCandidate(window, state, candidate);

        Assert.Equal("Hello @Alice ", GetInput(window));
        var (start, end) = GetSelection(window);
        Assert.Equal(13, start);
        Assert.Equal(13, end);

        var result = MentionResolver.ResolveDetailed(GetInput(window), presence.Presences, Array.Empty<RoleDto>());
        Assert.Equal("Hello <@1> ", result.Content);
        Assert.Single(result.Mentions);
        Assert.Equal("1", result.Mentions[0].Id);
        Assert.Equal("user", result.Mentions[0].Type);
        Assert.Equal("Alice", result.Mentions[0].Name);
    }

    [Fact]
    public void BuildMentionCandidates_RespectsRoleFilter()
    {
        SetupServices();
        RoleCache.Reset();
        try
        {
            var config = new Config { ApiBaseUrl = "http://localhost" };
            config.MentionRoleIds.Add("2");
            var handler = new DummyHandler();
            var httpClient = new HttpClient(handler);
            var tokenManager = new TokenManager();
            var channelService = new ChannelService(config, httpClient, tokenManager);
            var window = new ChatWindow(config, httpClient, null, tokenManager, channelService);

            typeof(RoleCache).GetField("_roles", BindingFlags.Static | BindingFlags.NonPublic)!
                .SetValue(null, new List<RoleDto>
                {
                    new() { Id = "1", Name = "Everyone" },
                    new() { Id = "2", Name = "Raiders" }
                });

            var candidates = InvokeBuildMentionCandidates(window, string.Empty).Cast<object>().ToList();

            Assert.Single(candidates);
            var candidateType = candidates[0].GetType();
            Assert.Equal("Raiders", candidateType.GetProperty("Name")!.GetValue(candidates[0]));
            Assert.Equal("2", candidateType.GetProperty("Id")!.GetValue(candidates[0]));
            var typeValue = candidateType.GetProperty("Type")!.GetValue(candidates[0])!;
            Assert.Equal(Enum.Parse(candidateType.GetProperty("Type")!.PropertyType, "Role"), typeValue);
        }
        finally
        {
            RoleCache.Reset();
        }
    }

    [Fact]
    public void UpdateMentionState_DismissesWhenQueryHasNoMatches()
    {
        SetupServices();
        var config = new Config { ApiBaseUrl = "http://localhost" };
        var handler = new DummyHandler();
        var httpClient = new HttpClient(handler);
        var tokenManager = new TokenManager();
        var channelService = new ChannelService(config, httpClient, tokenManager);
        var presence = new DiscordPresenceService(config, httpClient);
        var presenceList = (List<PresenceDto>)typeof(DiscordPresenceService)
            .GetField("_presences", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(presence)!;
        presenceList.Add(new PresenceDto { Id = "1", Name = "Alice" });

        var window = new ChatWindow(config, httpClient, presence, tokenManager, channelService);

        SetInput(window, "Hello @Ali", 10, 10);
        InvokeUpdateMentionState(window, composerActive: true, inputEdited: true);

        var state = EnsureMentionState(window);
        Assert.True((bool)state.GetType().GetField("Active")!.GetValue(state)!);
        var initialCandidates = (IList)state.GetType().GetProperty("Candidates")!.GetValue(state)!;
        Assert.NotEmpty(initialCandidates);

        SetInput(window, "Hello @Z", 8, 8);
        InvokeUpdateMentionState(window, composerActive: true, inputEdited: true);

        Assert.False((bool)state.GetType().GetField("Active")!.GetValue(state)!);
        var refreshedCandidates = (IList)state.GetType().GetProperty("Candidates")!.GetValue(state)!;
        Assert.Empty(refreshedCandidates);
        Assert.Equal(-1, (int)state.GetType().GetField("HighlightedIndex")!.GetValue(state)!);
    }

    private static void InvokeUpdateMentionState(ChatWindow window, bool composerActive, bool inputEdited)
    {
        typeof(ChatWindow)
            .GetMethod("UpdateMentionState", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(window, new object[] { composerActive, inputEdited });
    }

    private static IEnumerable InvokeBuildMentionCandidates(ChatWindow window, string query)
    {
        return (IEnumerable)typeof(ChatWindow)
            .GetMethod("BuildMentionCandidates", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(window, new object[] { query })!;
    }

    private static object CreateMentionCandidate(string kind, string id, string name)
    {
        var candidateType = typeof(ChatWindow).GetNestedType("MentionCandidate", BindingFlags.NonPublic)!;
        var enumType = typeof(ChatWindow).GetNestedType("MentionCandidateType", BindingFlags.NonPublic)!;
        var enumValue = Enum.Parse(enumType, kind);
        return Activator.CreateInstance(candidateType, enumValue, id, name)!;
    }

    private static void InvokeInsertMentionCandidate(ChatWindow window, object state, object candidate)
    {
        typeof(ChatWindow)
            .GetMethod("InsertMentionCandidate", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(window, new[] { state, candidate });
    }

    private static object EnsureMentionState(ChatWindow window)
    {
        return typeof(ChatWindow)
            .GetMethod("EnsureMentionDrawerState", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(window, Array.Empty<object>())!;
    }

    private static void SetInput(ChatWindow window, string text, int start, int end)
    {
        typeof(ChatWindow).GetField("_input", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(window, text);
        typeof(ChatWindow).GetField("_selectionStart", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(window, start);
        typeof(ChatWindow).GetField("_selectionEnd", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(window, end);
    }

    private static string GetInput(ChatWindow window)
    {
        return (string)typeof(ChatWindow).GetField("_input", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!;
    }

    private static (int Start, int End) GetSelection(ChatWindow window)
    {
        var start = (int)typeof(ChatWindow).GetField("_selectionStart", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(window)!;
        var end = (int)typeof(ChatWindow).GetField("_selectionEnd", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(window)!;
        return (start, end);
    }

    private static void SetupServices()
    {
        var services = new PluginServices();
        var framework = new TestFramework();
        var log = new TestLog();
        typeof(PluginServices).GetProperty("Framework", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(services, framework);
        typeof(PluginServices).GetProperty("Log", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(services, log);
    }

    private class DummyHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
        }
    }

    private class TestFramework : IFramework
    {
        public event FrameworkUpdateDelegate? Update { add { } remove { } }
        public FrameworkUpdateType CurrentUpdateType => FrameworkUpdateType.None;
        public void RunOnTick(System.Action action, FrameworkUpdatePriority priority = FrameworkUpdatePriority.Normal) => action();
    }

    private class TestLog : IPluginLog
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
        public void Fatal(string message, System.Exception exception) { }
    }
}
