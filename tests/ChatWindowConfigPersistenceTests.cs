using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Reflection;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using DemiCatPlugin;
using Moq;
using Xunit;

public class ChatWindowConfigPersistenceTests
{
    [Fact]
    public void PrepareChannelsForDisplay_SavesWithoutFramework()
    {
        var pluginInterface = new Mock<IDalamudPluginInterface>();
        var services = new PluginServices();
        typeof(PluginServices).GetProperty("PluginInterface", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(services, pluginInterface.Object);
        typeof(PluginServices).GetProperty("Framework", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(services, null);
        typeof(PluginServices).GetProperty("Log", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(services, new TestLog());

        var config = new Config();
        using var client = new HttpClient(new HttpClientHandler());
        var tokenManager = new TokenManager();
        var channelService = new ChannelService(config, client, tokenManager);
        var window = new ChatWindow(config, client, null, tokenManager, channelService);

        var channels = new List<ChannelDto>
        {
            new()
            {
                Id = "1",
                Name = "General",
                GuildId = "Guild-1"
            }
        };

        var method = typeof(ChatWindow).GetMethod(
            "PrepareChannelsForDisplay",
            BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("PrepareChannelsForDisplay not found");

        var prepared = (List<ChannelDto>)method.Invoke(window, new object[] { channels })!;

        Assert.Single(prepared);
        Assert.Equal("1", prepared[0].Id);
        Assert.Equal(ChannelKeyHelper.NormalizeGuildId("Guild-1"), config.GuildId);

        pluginInterface.Verify(pi => pi.SavePluginConfig(config), Times.Once);
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
}
