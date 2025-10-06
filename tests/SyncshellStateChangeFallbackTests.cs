using System;
using System.IO;
using System.Net.Http;
using System.Reflection;
using Dalamud.Plugin;
using Dalamud.Plugin.Ipc;
using DemiCatPlugin;
using Moq;
using Penumbra.Api.Enums;
using Xunit;

public class SyncshellStateChangeFallbackTests
{
    [Fact]
    public void LegacyGateFallbackStillRaisesStateChange()
    {
        var previousServices = PluginServices.Instance;
        var previousTokenManager = TokenManager.Instance;
        SyncshellWindow? window = null;
        var tempDir = Path.Combine(Path.GetTempPath(), "DemiCat", "SyncshellFallbackTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);

        using var httpClient = new HttpClient();

        try
        {
            var services = new PluginServices();
            var pluginInterface = new Mock<IDalamudPluginInterface>();
            pluginInterface.Setup(pi => pi.GetPluginConfigDirectory()).Returns(tempDir);
            pluginInterface.Setup(pi => pi.SavePluginConfig(It.IsAny<Config>()));

            var modSettingLegacy = new Mock<ICallGateSubscriber<Guid, string, bool, object?>>();
            modSettingLegacy
                .Setup(sub => sub.Subscribe(It.IsAny<Action<Guid, string, bool>>()))
                .Verifiable();
            modSettingLegacy
                .Setup(sub => sub.Unsubscribe(It.IsAny<Action<Guid, string, bool>>()))
                .Verifiable();
            pluginInterface
                .Setup(pi => pi.GetIpcSubscriber<ModSettingChange, Guid, string, bool, object?>(It.Is<string>(c => c == Penumbra.Api.IpcSubscribers.ModSettingChanged.Label)))
                .Throws(new InvalidOperationException("typed gate unavailable"));
            pluginInterface
                .Setup(pi => pi.GetIpcSubscriber<ModSettingChange, Guid, string, bool, object?>(It.Is<string>(c => c == "Penumbra.ModSettingChanged")))
                .Throws(new InvalidOperationException("legacy gate used wrong signature"));
            pluginInterface
                .Setup(pi => pi.GetIpcSubscriber<Guid, string, bool, object?>(It.Is<string>(c => c == "Penumbra.ModSettingChanged")))
                .Returns(modSettingLegacy.Object);

            var enabledLegacy = new Mock<ICallGateSubscriber<bool, object?>>();
            enabledLegacy.Setup(sub => sub.Subscribe(It.IsAny<Action<bool>>()));
            enabledLegacy.Setup(sub => sub.Unsubscribe(It.IsAny<Action<bool>>()));
            pluginInterface
                .Setup(pi => pi.GetIpcSubscriber<bool, object?>(It.Is<string>(c => c == Penumbra.Api.IpcSubscribers.EnabledChange.Label)))
                .Throws(new InvalidOperationException("typed gate unavailable"));
            pluginInterface
                .Setup(pi => pi.GetIpcSubscriber<bool, object?>(It.Is<string>(c => c == "Penumbra.EnabledChange")))
                .Returns(enabledLegacy.Object);

            pluginInterface
                .Setup(pi => pi.GetIpcSubscriber<nint, object?>(It.Is<string>(c => c == Glamourer.Api.IpcSubscribers.StateChanged.Label)))
                .Throws(new InvalidOperationException("typed gate unavailable"));
            pluginInterface
                .Setup(pi => pi.GetIpcSubscriber<int, object?>(It.Is<string>(c => c == Glamourer.Api.IpcSubscribers.StateChanged.Label)))
                .Throws(new InvalidOperationException("typed gate unavailable"));
            pluginInterface
                .Setup(pi => pi.GetIpcSubscriber<nint, object?>(It.Is<string>(c => c == "Glamourer.StateChanged")))
                .Throws(new InvalidOperationException("legacy gate unavailable"));

            var glamourerLegacyInt = new Mock<ICallGateSubscriber<int, object?>>();
            Action<int>? glamourerHandler = null;
            glamourerLegacyInt
                .Setup(sub => sub.Subscribe(It.IsAny<Action<int>>()))
                .Callback<Action<int>>(handler => glamourerHandler = handler);
            glamourerLegacyInt
                .Setup(sub => sub.Unsubscribe(It.IsAny<Action<int>>()))
                .Verifiable();
            pluginInterface
                .Setup(pi => pi.GetIpcSubscriber<int, object?>(It.Is<string>(c => c == "Glamourer.StateChanged")))
                .Returns(glamourerLegacyInt.Object);

            SetPluginService(services, "PluginInterface", pluginInterface.Object);

            var tokenManager = new TokenManager();

            var config = new Config
            {
                FCSyncShell = true,
                SyncshellAutoSyncAllUsers = false,
                SyncshellManualSyncAllUsers = true,
            };

            window = new SyncshellWindow(config, httpClient, tokenManager);

            Assert.NotNull(glamourerHandler);

            var pendingField = typeof(SyncshellWindow)
                .GetField("_manualSyncPendingFlag", BindingFlags.Instance | BindingFlags.NonPublic)!;
            Assert.Equal(0, (int)pendingField.GetValue(window)!);

            glamourerHandler!(123);

            Assert.Equal(1, (int)pendingField.GetValue(window)!);

            window.Dispose();
            modSettingLegacy.Verify(sub => sub.Subscribe(It.IsAny<Action<Guid, string, bool>>()), Times.Once());
            modSettingLegacy.Verify(sub => sub.Unsubscribe(It.IsAny<Action<Guid, string, bool>>()), Times.Once());
            glamourerLegacyInt.Verify(sub => sub.Unsubscribe(It.IsAny<Action<int>>()), Times.Once());
            window = null;
        }
        finally
        {
            window?.Dispose();
            SetPluginServicesInstance(previousServices);
            SetTokenManagerInstance(previousTokenManager);
            TryDeleteDirectory(tempDir);
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

    private static void SetTokenManagerInstance(TokenManager? value)
    {
        typeof(TokenManager)
            .GetProperty("Instance", BindingFlags.Static | BindingFlags.Public)!
            .SetValue(null, value);
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch
        {
            // best effort cleanup
        }
    }
}
