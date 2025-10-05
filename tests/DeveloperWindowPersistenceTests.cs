using System;
using System.Reflection;
using System.Text.Json;
using System.Threading.Tasks;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using DemiCatPlugin;
using Moq;
using Xunit;

public class DeveloperWindowPersistenceTests
{
    [Fact]
    public void CustomApiBaseUrlPersistsAfterRestart()
    {
        var previousServices = PluginServices.Instance;

        try
        {
            var services = new PluginServices();
            var pluginInterface = new Mock<IDalamudPluginInterface>();
            pluginInterface.Setup(pi => pi.SavePluginConfig(It.IsAny<Config>()));

            SetPluginService(services, "PluginInterface", pluginInterface.Object);
            SetPluginService(services, "Log", new TestLog());

            var config = new Config();
            var window = new DeveloperWindow(
                config,
                pluginInterface.Object,
                () => false,
                () => Task.CompletedTask,
                () => { });

            var method = typeof(DeveloperWindow).GetMethod(
                "ApplyApiBaseUrlChange",
                BindingFlags.Instance | BindingFlags.NonPublic)
                ?? throw new InvalidOperationException("ApplyApiBaseUrlChange not found");

            method.Invoke(window, new object[] { "https://unit.test/custom" });

            pluginInterface.Verify(pi => pi.SavePluginConfig(config), Times.Once);
            Assert.Equal("https://unit.test/custom", config.ApiBaseUrl);

            var json = JsonSerializer.Serialize(config);
            var reloaded = JsonSerializer.Deserialize<Config>(json)
                ?? throw new InvalidOperationException("Failed to deserialize config");
            reloaded.Migrate();

            Assert.Equal("https://unit.test/custom", reloaded.ApiBaseUrl);
        }
        finally
        {
            SetPluginServicesInstance(previousServices);
        }
    }

    private static void SetPluginService(PluginServices services, string propertyName, object value)
    {
        typeof(PluginServices)
            .GetProperty(propertyName, BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(services, value);
    }

    private static void SetPluginServicesInstance(PluginServices? value)
    {
        typeof(PluginServices)
            .GetProperty("Instance", BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public)!
            .SetValue(null, value);
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
        public void Error(string message, Exception exception) { }
        public void Fatal(string message) { }
        public void Fatal(string message, Exception exception) { }
    }
}

