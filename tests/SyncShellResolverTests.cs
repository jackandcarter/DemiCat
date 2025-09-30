using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Dalamud.Plugin;
using Dalamud.Plugin.Ipc;
using Dalamud.Plugin.Services;
using DemiCatPlugin.SyncShell;
using DemiCatPlugin;
using Moq;
using Xunit;

public class SyncShellResolverTests
{
    [Fact]
    public async Task BuildManifestAsync_ComputesHashesAndStoresMissingBlobs()
    {
        using var temp = new TempDirectory();
        var modsDirectory = Path.Combine(temp.Path, "mods");
        var configDirectory = Path.Combine(temp.Path, "config");
        Directory.CreateDirectory(modsDirectory);
        Directory.CreateDirectory(configDirectory);

        var defaultModPath = Path.Combine(configDirectory, "default_mod.json");
        var modDirectory = Path.Combine(modsDirectory, "SampleMod");
        Directory.CreateDirectory(modDirectory);

        var filePath = Path.Combine(modDirectory, "character.mtrl");
        var fileBytes = Encoding.UTF8.GetBytes("resolver-test");
        await File.WriteAllBytesAsync(filePath, fileBytes).ConfigureAwait(false);

        var patchDirectory = Path.Combine(modsDirectory, "_patches");
        Directory.CreateDirectory(patchDirectory);
        var patchPath = Path.Combine(patchDirectory, "appearance.bin");
        var patchBytes = Encoding.UTF8.GetBytes("patch");
        await File.WriteAllBytesAsync(patchPath, patchBytes).ConfigureAwait(false);

        var defaultMod = new
        {
            Mods = new Dictionary<string, object>
            {
                ["sample-mod"] = new
                {
                    Name = "Sample Mod",
                    Enabled = true,
                    Directory = "SampleMod",
                    Settings = new Dictionary<string, string> { ["Option"] = "Value" },
                    Options = new Dictionary<string, string> { ["Choice"] = "One" },
                }
            }
        };
        await File.WriteAllTextAsync(defaultModPath, JsonSerializer.Serialize(defaultMod)).ConfigureAwait(false);

        var metaPath = Path.Combine(modDirectory, "meta.json");
        var meta = new
        {
            Tags = new[] { "tag1", "tag2" },
            Meta = new { Author = "Tester" }
        };
        await File.WriteAllTextAsync(metaPath, JsonSerializer.Serialize(meta)).ConfigureAwait(false);

        var blobStore = new Mock<IBlobStore>();
        var storedHashes = new List<string>();
        blobStore.Setup(bs => bs.Has(It.IsAny<string>())).Returns(false);
        blobStore
            .Setup(bs => bs.StoreAsync(It.IsAny<string>(), It.IsAny<Stream>(), It.IsAny<CancellationToken>()))
            .Callback<string, Stream, CancellationToken>((hash, stream, _) =>
            {
                storedHashes.Add(hash);
                using var sink = new MemoryStream();
                stream.CopyTo(sink);
            })
            .Returns(Task.CompletedTask);

        var glamState = "{\"design\":true}";
        var customizeState = "{\"profile\":true}";
        var pluginInterface = CreatePluginInterfaceMock(
            modsDirectory,
            configDirectory,
            glamState,
            customizeState);

        var resolver = new Resolver(new Config(), blobStore.Object, Mock.Of<IPluginLog>(), pluginInterface.Object);

        var manifest = await resolver.BuildManifestAsync().ConfigureAwait(false);

        var collection = Assert.Single(manifest.Collections);
        Assert.Equal("default", collection.CollectionId);
        var modEntry = Assert.Single(collection.Mods);
        Assert.Equal("sample-mod", modEntry.ModId);
        Assert.Equal("Sample Mod", modEntry.Name);
        Assert.True(modEntry.Enabled);
        Assert.Equal(fileBytes.Length, modEntry.Size);

        var fileEntry = Assert.Single(modEntry.Files);
        Assert.Equal("character.mtrl", fileEntry.Path);
        var expectedFileHash = ComputeSha256Hex(fileBytes);
        Assert.Equal(expectedFileHash, fileEntry.Hash);
        Assert.Contains(expectedFileHash, storedHashes);

        var patchEntry = Assert.Single(collection.Patches);
        Assert.Equal("appearance.bin", patchEntry.Path.Replace('\\', '/'));
        var expectedPatchHash = ComputeSha256Hex(patchBytes);
        Assert.Equal(expectedPatchHash, patchEntry.Hash);
        Assert.Contains(expectedPatchHash, storedHashes);

        var aggregate = SHA256.HashData(Convert.FromHexString(expectedFileHash));
        Assert.Equal(Convert.ToHexString(aggregate).ToLowerInvariant(), modEntry.Hash);

        Assert.Equal(glamState, manifest.Appearance.CustomState["glamourer"]);
        Assert.Equal(customizeState, manifest.Appearance.CustomState["customize+"]);
        Assert.Contains("sample-mod", manifest.Appearance.ActiveMods);

        var glamSizeHint = manifest.SizeHints.Single(h => h.Path == "glamourer");
        Assert.Equal(Encoding.UTF8.GetByteCount(glamState), glamSizeHint.Size);
    }

    [Fact]
    public async Task BuildManifestAsync_UsesConfiguredDirectoriesWhenPluginInterfaceUnavailable()
    {
        using var temp = new TempDirectory();
        var modsDirectory = Path.Combine(temp.Path, "mods");
        var configDirectory = Path.Combine(temp.Path, "config");
        Directory.CreateDirectory(modsDirectory);
        Directory.CreateDirectory(configDirectory);
        await File.WriteAllTextAsync(Path.Combine(configDirectory, "default_mod.json"), "{}").ConfigureAwait(false);

        var config = new Config
        {
            PenumbraModsDirectory = modsDirectory,
            PenumbraConfigDirectory = configDirectory,
        };

        var blobStore = new Mock<IBlobStore>();
        blobStore.Setup(bs => bs.Has(It.IsAny<string>())).Returns(true);

        var resolver = new Resolver(config, blobStore.Object, Mock.Of<IPluginLog>(), null);
        var manifest = await resolver.BuildManifestAsync().ConfigureAwait(false);

        var collection = Assert.Single(manifest.Collections);
        Assert.Equal("default", collection.CollectionId);
        Assert.Empty(collection.Mods);
        Assert.Empty(collection.Patches);
    }

    [Fact]
    public void GetMissingBlobs_ReturnsOnlyHashesNotPresent()
    {
        var blobStore = new Mock<IBlobStore>();
        blobStore.Setup(bs => bs.Has("present")).Returns(true);
        blobStore.Setup(bs => bs.Has("missing")).Returns(false);

        var manifest = new SyncManifest
        {
            Collections =
            {
                new CollectionDelta
                {
                    Mods =
                    {
                        new ModEntry
                        {
                            Files =
                            {
                                new FileHash { Path = "a", Hash = "present", Size = 1 },
                                new FileHash { Path = "b", Hash = "missing", Size = 1 },
                                new FileHash { Path = "c", Hash = "missing", Size = 1 },
                            }
                        }
                    }
                }
            }
        };

        var resolver = new Resolver(new Config(), blobStore.Object, Mock.Of<IPluginLog>(), null);
        var missing = resolver.GetMissingBlobs(manifest).ToList();

        Assert.Single(missing);
        Assert.Equal("missing", missing[0]);
    }

    [Fact]
    public async Task ApplyManifestAsync_WritesFilesAndInvokesIpc()
    {
        using var temp = new TempDirectory();
        var modsDirectory = Path.Combine(temp.Path, "mods");
        var configDirectory = Path.Combine(temp.Path, "config");
        Directory.CreateDirectory(modsDirectory);
        Directory.CreateDirectory(configDirectory);
        await File.WriteAllTextAsync(Path.Combine(configDirectory, "default_mod.json"), "{}").ConfigureAwait(false);

        var cacheDirectory = Path.Combine(temp.Path, "cache");
        Directory.CreateDirectory(cacheDirectory);
        var blobStore = new FileBlobStore(cacheDirectory);

        var modBytes = Encoding.UTF8.GetBytes("apply-data");
        var modHash = ComputeSha256Hex(modBytes);
        await blobStore.StoreAsync(modHash, new MemoryStream(modBytes)).ConfigureAwait(false);

        var patchBytes = Encoding.UTF8.GetBytes("patch-data");
        var patchHash = ComputeSha256Hex(patchBytes);
        await blobStore.StoreAsync(patchHash, new MemoryStream(patchBytes)).ConfigureAwait(false);

        var manifest = new SyncManifest();
        manifest.Collections.Add(new CollectionDelta
        {
            Mods =
            {
                new ModEntry
                {
                    ModId = "mod-one",
                    Enabled = true,
                    Hash = Convert.ToHexString(SHA256.HashData(Convert.FromHexString(modHash))).ToLowerInvariant(),
                    Files = { new FileHash { Path = "assets/file.bin", Hash = modHash, Size = modBytes.Length } },
                }
            },
            Patches =
            {
                new PatchEntry { Path = "patches/patch.bin", Hash = patchHash, Size = patchBytes.Length }
            }
        });
        manifest.Appearance.CustomState["glamourer"] = "glam-state";
        manifest.Appearance.CustomState["customize+"] = "custom-state";

        var reloadMock = new Mock<ICallGateSubscriber<object>>();
        reloadMock.Setup(sub => sub.InvokeAction());
        var redrawMock = new Mock<ICallGateSubscriber<object>>();
        redrawMock.Setup(sub => sub.InvokeAction());
        var glamApplyMock = new Mock<ICallGateSubscriber<string, object?>>();
        glamApplyMock.Setup(sub => sub.InvokeAction(It.IsAny<string>()));
        var customizeApplyMock = new Mock<ICallGateSubscriber<string, object?>>();
        customizeApplyMock.Setup(sub => sub.InvokeAction(It.IsAny<string>()));

        var pluginInterface = CreatePluginInterfaceMock(
            modsDirectory,
            configDirectory,
            glamState: null,
            customizeState: null);
        pluginInterface
            .Setup(pi => pi.GetIpcSubscriber<object>(It.Is<string>(c => c == "Penumbra.Reload")))
            .Returns(reloadMock.Object);
        pluginInterface
            .Setup(pi => pi.GetIpcSubscriber<object>(It.Is<string>(c => c == "Penumbra.RedrawAll")))
            .Returns(redrawMock.Object);
        pluginInterface
            .Setup(pi => pi.GetIpcSubscriber<string, object?>(It.Is<string>(c => c == "Glamourer.Design.Apply")))
            .Returns(glamApplyMock.Object);
        pluginInterface
            .Setup(pi => pi.GetIpcSubscriber<string, object?>(It.Is<string>(c => c == "Customize.ApplyProfile")))
            .Returns(customizeApplyMock.Object);

        var resolver = new Resolver(new Config(), blobStore, Mock.Of<IPluginLog>(), pluginInterface.Object);
        await resolver.ApplyManifestAsync("peer-one", manifest).ConfigureAwait(false);

        var appliedFile = Path.Combine(modsDirectory, "SyncShell", "peer-one", "mod-one", "assets", "file.bin");
        Assert.True(File.Exists(appliedFile));
        Assert.Equal(modBytes, await File.ReadAllBytesAsync(appliedFile).ConfigureAwait(false));

        var patchFile = Path.Combine(modsDirectory, "SyncShell", "peer-one", "patches", "patches", "patch.bin");
        Assert.True(File.Exists(patchFile));
        Assert.Equal(patchBytes, await File.ReadAllBytesAsync(patchFile).ConfigureAwait(false));

        var defaultModPath = Path.Combine(configDirectory, "default_mod.json");
        var defaultConfig = JsonSerializer.Deserialize<JsonElement>(await File.ReadAllTextAsync(defaultModPath).ConfigureAwait(false));
        var modConfig = defaultConfig.GetProperty("Mods").GetProperty("mod-one");
        Assert.True(modConfig.GetProperty("Enabled").GetBoolean());
        Assert.Equal(Path.Combine("SyncShell", "peer-one", "mod-one"), modConfig.GetProperty("Directory").GetString());

        reloadMock.Verify(sub => sub.InvokeAction(), Times.Once());
        redrawMock.Verify(sub => sub.InvokeAction(), Times.Once());
        glamApplyMock.Verify(sub => sub.InvokeAction("glam-state"), Times.Once());
        customizeApplyMock.Verify(sub => sub.InvokeAction("custom-state"), Times.Once());
    }

    private static string ComputeSha256Hex(ReadOnlySpan<byte> data)
        => Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant();

    private static Mock<IDalamudPluginInterface> CreatePluginInterfaceMock(
        string modsDirectory,
        string configDirectory,
        string? glamState,
        string? customizeState)
    {
        var pluginInterface = new Mock<IDalamudPluginInterface>();
        pluginInterface
            .Setup(pi => pi.GetIpcSubscriber<string>(It.IsAny<string>()))
            .Returns<string>(channel => channel switch
            {
                "Penumbra.GetModsDirectory" => CreateFuncSubscriber(modsDirectory),
                "Penumbra.GetConfigurationDirectory" => CreateFuncSubscriber(configDirectory),
                "Penumbra.GetConfigDirectory" => CreateFuncSubscriber(configDirectory),
                "Glamourer.GetCharacterState" => CreateFuncSubscriber(glamState),
                "Customize.GetCharacterProfile" => CreateFuncSubscriber(customizeState),
                "Customize.GetActiveProfile" => CreateFuncSubscriber<string?>(null),
                _ => CreateFuncSubscriber<string?>(null),
            });
        return pluginInterface;
    }

    private static ICallGateSubscriber<T> CreateFuncSubscriber<T>(T value)
    {
        var subscriber = new Mock<ICallGateSubscriber<T>>();
        subscriber.Setup(sub => sub.InvokeFunc()).Returns(value);
        return subscriber.Object;
    }

    private sealed class TempDirectory : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), Guid.NewGuid().ToString("N"));

        public TempDirectory()
        {
            Directory.CreateDirectory(Path);
        }

        public void Dispose()
        {
            try
            {
                if (Directory.Exists(Path))
                {
                    Directory.Delete(Path, recursive: true);
                }
            }
            catch
            {
                // best effort cleanup
            }
        }
    }
}
