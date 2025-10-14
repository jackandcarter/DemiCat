using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Text.Json;
using Dalamud.Game.ClientState;
using Dalamud.Game.ClientState.Objects;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using DemiCatPlugin;
using DemiCatPlugin.SyncShell;
using Moq;
using Xunit;
using Tests;

public class SyncshellServiceHelperTests
{
    [Theory]
    [InlineData(0, "Idle")]
    [InlineData(1, "Syncing 1 member")]
    [InlineData(2, "Syncing 2 members")]
    public void FormatStatusForMembersMatchesExpected(int count, string expected)
    {
        var status = SyncShellService.FormatStatusForMembers(count);
        Assert.Equal(expected, status);
    }

    [Fact]
    public void PresenceUpdateSerializesStringIdentifiers()
    {
        var dto = new PresenceUpdateDto
        {
            ActiveMemberIds = { "123456789012345678", "987654321098765432" }
        };

        var json = JsonSerializer.Serialize(dto);
        Assert.Contains("\"123456789012345678\"", json);
        Assert.Contains("\"987654321098765432\"", json);
    }

    [Theory]
    [InlineData("2024-05-01T12:30:00Z", true)]
    [InlineData("2024-05-01 12:30:00", true)]
    [InlineData("not-a-timestamp", false)]
    [InlineData(null, false)]
    public void ParseTimestampHandlesInputs(string? value, bool expectValue)
    {
        var result = SyncShellService.ParseTimestamp(value);
        if (expectValue)
        {
            Assert.NotNull(result);
            Assert.True(result.Value > DateTimeOffset.MinValue);
        }
        else
        {
            Assert.Null(result);
        }
    }

    [Fact]
    public void ApplyTargetsHonorModeConfiguration()
    {
        var tempDir = Directory.CreateTempSubdirectory();
        var tokenField = typeof(TokenManager).GetField("<Instance>k__BackingField", BindingFlags.Static | BindingFlags.NonPublic)!;
        var previousToken = (TokenManager?)tokenField.GetValue(null);
        try
        {
            var pluginInterfaceMock = new Mock<IDalamudPluginInterface>();
            pluginInterfaceMock.Setup(i => i.GetPluginConfigDirectory()).Returns(tempDir.FullName);
            pluginInterfaceMock.Setup(i => i.GetIpcSubscriber<bool>(It.IsAny<string>())).Throws(new InvalidOperationException());
            pluginInterfaceMock.Setup(i => i.GetIpcSubscriber<string>(It.IsAny<string>())).Throws(new InvalidOperationException());
            pluginInterfaceMock.Setup(i => i.GetIpcSubscriber<string, object?>(It.IsAny<string>())).Throws(new InvalidOperationException());
            pluginInterfaceMock.Setup(i => i.GetIpcSubscriber<(int, int), object?>(It.IsAny<string>())).Throws(new InvalidOperationException());

            var logMock = new Mock<IPluginLog>();
            var clientStateMock = new Mock<IClientState>();
            var frameworkMock = new Mock<IFramework>();
            var objectTableMock = new Mock<IObjectTable>();

            using var blobStore = new BlobStore(pluginInterfaceMock.Object);
            var penumbra = new PenumbraIpc(pluginInterfaceMock.Object, logMock.Object);
            var glamourer = new GlamourerIpc(pluginInterfaceMock.Object, clientStateMock.Object, logMock.Object);

            var config = new Config
            {
                EnableSyncShell = true,
                ApiBaseUrl = "http://localhost",
                SyncAutoMode = false,
                OnlySyncVisible = false,
            };

            config.ManualAutoList.Add(123UL);

            var tokenManager = new TokenManager();
            tokenField.SetValue(null, tokenManager);
            using var httpClient = new HttpClient(new HttpClientHandler()) { BaseAddress = new Uri("http://localhost") };
            var client = new SyncShellClient(httpClient, config, tokenManager);
            var service = new SyncShellService(
                config,
                tokenManager,
                client,
                blobStore,
                penumbra,
                glamourer,
                logMock.Object,
                clientStateMock.Object,
                frameworkMock.Object,
                objectTableMock.Object,
                new FakeSyncShellWatcher());

            var members = new List<SyncshellMemberStatus>
            {
                new SyncshellMemberStatus { Id = "123", DisplayName = "One", Presence = "online", TokenLinked = true },
                new SyncshellMemberStatus { Id = "456", DisplayName = "Two", Presence = "online", TokenLinked = true },
            };

            typeof(SyncShellService).GetField("_members", BindingFlags.NonPublic | BindingFlags.Instance)!
                .SetValue(service, members);

            var buildMethod = typeof(SyncShellService).GetMethod("BuildActiveMemberList", BindingFlags.NonPublic | BindingFlags.Instance)!;

            var manualTargets = (string[])buildMethod.Invoke(service, Array.Empty<object?>())!;
            Assert.Single(manualTargets);
            Assert.Equal("123", manualTargets[0]);

            config.SyncAutoMode = true;
            var autoTargets = (string[])buildMethod.Invoke(service, Array.Empty<object?>())!;
            Assert.Contains("123", autoTargets);
            Assert.Contains("456", autoTargets);
        }
        finally
        {
            tokenField.SetValue(null, previousToken);
            Directory.Delete(tempDir.FullName, true);
        }
    }
}
