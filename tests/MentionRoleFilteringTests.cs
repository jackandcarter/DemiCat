using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Dalamud.Plugin.Services;
using DemiCatPlugin;
using DemiCatPlugin.Emoji;
using Xunit;

public class MentionRoleFilteringTests
{
    [Fact]
    public async Task EventCreateWindow_LoadRoles_FiltersAllowlistedIds()
    {
        RoleCache.Reset();
        var previousServices = PluginServices.Instance;
        var services = new PluginServices();
        SetServiceProperty(services, "Framework", new ImmediateFramework());
        SetServiceProperty(services, "Log", new TestLog());

        var tokenField = typeof(TokenManager).GetField("<Instance>k__BackingField", BindingFlags.Static | BindingFlags.NonPublic)!;
        var previousTokenManager = (TokenManager?)tokenField.GetValue(null);

        try
        {
            using var http = new HttpClient(new StubHandler());
            var config = new Config();
            config.GuildRoles.Add(new RoleDto { Id = "1", Name = "Everyone" });
            config.GuildRoles.Add(new RoleDto { Id = "2", Name = "Raiders" });
            config.MentionRoleIds.Add("2");

            var tokenManager = new TokenManager();
            var channelService = new ChannelService(config, http, tokenManager);
            var selection = new ChannelSelectionService(config);
            using var emojiManager = new EmojiManager(http, tokenManager, config);
            var window = new EventCreateWindow(config, http, channelService, selection, emojiManager);

            var mentionsField = typeof(EventCreateWindow).GetField("_mentions", BindingFlags.Instance | BindingFlags.NonPublic)!;
            var mentions = (HashSet<string>)mentionsField.GetValue(window)!;
            mentions.Add("1");
            mentions.Add("2");

            var loadMethod = typeof(EventCreateWindow).GetMethod("LoadRoles", BindingFlags.Instance | BindingFlags.NonPublic)!;
            await ((Task)loadMethod.Invoke(window, null)!).ConfigureAwait(false);

            var rolesField = typeof(EventCreateWindow).GetField("_roles", BindingFlags.Instance | BindingFlags.NonPublic)!;
            var roles = (List<RoleDto>)rolesField.GetValue(window)!;

            Assert.Single(roles);
            Assert.Equal("2", roles[0].Id);
            Assert.Single(mentions);
            Assert.Contains("2", mentions);
        }
        finally
        {
            RoleCache.Reset();
            SetPluginServicesInstance(previousServices);
            tokenField.SetValue(null, previousTokenManager);
        }
    }

    [Fact]
    public async Task TemplatesWindow_LoadRoles_FiltersAllowlistedIds()
    {
        RoleCache.Reset();
        var previousServices = PluginServices.Instance;
        var services = new PluginServices();
        SetServiceProperty(services, "Framework", new ImmediateFramework());
        SetServiceProperty(services, "Log", new TestLog());

        var tokenField = typeof(TokenManager).GetField("<Instance>k__BackingField", BindingFlags.Static | BindingFlags.NonPublic)!;
        var previousTokenManager = (TokenManager?)tokenField.GetValue(null);

        try
        {
            using var http = new HttpClient(new StubHandler());
            var config = new Config();
            config.GuildRoles.Add(new RoleDto { Id = "1", Name = "Everyone" });
            config.GuildRoles.Add(new RoleDto { Id = "2", Name = "Raiders" });
            config.MentionRoleIds.Add("2");

            var tokenManager = new TokenManager();
            var channelService = new ChannelService(config, http, tokenManager);
            var selection = new ChannelSelectionService(config);
            using var emojiManager = new EmojiManager(http, tokenManager, config);
            var window = new TemplatesWindow(config, http, channelService, selection, emojiManager);

            var mentionsField = typeof(TemplatesWindow).GetField("_mentions", BindingFlags.Instance | BindingFlags.NonPublic)!;
            var mentions = (HashSet<string>)mentionsField.GetValue(window)!;
            mentions.Add("1");
            mentions.Add("2");

            var loadMethod = typeof(TemplatesWindow).GetMethod("LoadRoles", BindingFlags.Instance | BindingFlags.NonPublic)!;
            await ((Task)loadMethod.Invoke(window, null)!).ConfigureAwait(false);

            var rolesField = typeof(TemplatesWindow).GetField("_roles", BindingFlags.Instance | BindingFlags.NonPublic)!;
            var roles = (List<RoleDto>)rolesField.GetValue(window)!;

            Assert.Single(roles);
            Assert.Equal("2", roles[0].Id);
            Assert.Single(mentions);
            Assert.Contains("2", mentions);
        }
        finally
        {
            RoleCache.Reset();
            SetPluginServicesInstance(previousServices);
            tokenField.SetValue(null, previousTokenManager);
        }
    }

    private static void SetServiceProperty(PluginServices services, string propertyName, object value)
    {
        typeof(PluginServices)
            .GetProperty(propertyName, BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(services, value);
    }

    private static void SetPluginServicesInstance(PluginServices? instance)
    {
        typeof(PluginServices)
            .GetProperty("Instance", BindingFlags.Static | BindingFlags.NonPublic)!
            .SetValue(null, instance);
    }

    private sealed class ImmediateFramework : IFramework
    {
        public event FrameworkUpdateDelegate? Update { add { } remove { } }
        public FrameworkUpdateType CurrentUpdateType => FrameworkUpdateType.None;
        public void RunOnTick(Action action, FrameworkUpdatePriority priority = FrameworkUpdatePriority.Normal) => action();
    }

    private sealed class TestLog : IPluginLog
    {
        public void Verbose(string message) { }
        public void Verbose(string message, Exception exception) { }
        public void Debug(string message) { }
        public void Debug(string message, Exception exception) { }
        public void Info(string message) { }
        public void Info(string message, Exception exception) { }
        public void Warning(string message) { }
        public void Warning(string message, Exception exception) { }
        public void Error(string message) { }
        public void Error(Exception exception, string message) { }
        public void Fatal(string message) { }
        public void Fatal(Exception exception, string message) { }
    }

    private sealed class StubHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(string.Empty)
            };
            return Task.FromResult(response);
        }
    }
}
