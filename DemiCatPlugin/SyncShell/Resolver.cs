using System;
using System.Buffers;
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
using DemiCatPlugin;

namespace DemiCatPlugin.SyncShell;

/// <summary>
/// Resolves the local Penumbra state into a <see cref="SyncManifest"/> and applies manifests from peers.
/// </summary>
public interface IResolver
{
    /// <summary>
    /// Builds a manifest describing the local Penumbra state.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The populated manifest.</returns>
    Task<SyncManifest> BuildManifestAsync(PenumbraResolveOptions? options = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// Determines which blobs referenced by the manifest are missing from the backing <see cref="IBlobStore"/>.
    /// </summary>
    /// <param name="manifest">Manifest to inspect.</param>
    /// <returns>Hashes that are not currently cached.</returns>
    IEnumerable<string> GetMissingBlobs(SyncManifest manifest);

    /// <summary>
    /// Applies a manifest received from a peer.
    /// </summary>
    /// <param name="peerId">Identifier of the peer.</param>
    /// <param name="manifest">Manifest to apply.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task ApplyManifestAsync(string peerId, SyncManifest manifest, PenumbraResolveOptions? options = null, CancellationToken cancellationToken = default);
}

/// <inheritdoc />
public sealed class Resolver : IResolver
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly string[] PenumbraConfigCandidates =
    {
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "XIVLauncher", "pluginConfigs", "Penumbra"),
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "XIVLauncherCN", "pluginConfigs", "Penumbra"),
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "XIVLauncher", "pluginConfigs", "Penumbra"),
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "XIVLauncherCN", "pluginConfigs", "Penumbra"),
    };

    private readonly Config _config;
    private readonly IBlobStore _blobStore;
    private readonly IPluginLog _log;
    private readonly IDalamudPluginInterface? _pluginInterface;

    /// <summary>
    /// Initialises a new instance of the <see cref="Resolver"/> class.
    /// </summary>
    public Resolver(Config config, IBlobStore blobStore, IPluginLog log, IDalamudPluginInterface? pluginInterface = null)
    {
        _config = config ?? throw new ArgumentNullException(nameof(config));
        _blobStore = blobStore ?? throw new ArgumentNullException(nameof(blobStore));
        _log = log ?? throw new ArgumentNullException(nameof(log));
        _pluginInterface = pluginInterface ?? PluginServices.Instance?.PluginInterface;
    }

    /// <inheritdoc />
    public async Task<SyncManifest> BuildManifestAsync(PenumbraResolveOptions? options = null, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var manifest = new SyncManifest
        {
            Appearance =
            {
                LastUpdated = DateTimeOffset.UtcNow,
            },
        };

        var penumbraPaths = ResolvePenumbraPaths(options);
        if (penumbraPaths is null)
        {
            _log.Warning("Unable to resolve Penumbra directories; returning empty manifest");
            return manifest;
        }

        manifest.Appearance.CollectionId = penumbraPaths.CollectionName;

        var collection = new CollectionDelta
        {
            CollectionId = penumbraPaths.CollectionName,
        };

        await PopulateModsAsync(collection, penumbraPaths, cancellationToken).ConfigureAwait(false);
        await PopulatePatchesAsync(collection, penumbraPaths, cancellationToken).ConfigureAwait(false);

        manifest.Collections.Add(collection);

        var glam = TryInvokeIpc<string>("Glamourer.GetCharacterState");
        if (!string.IsNullOrWhiteSpace(glam))
        {
            manifest.Appearance.CustomState["glamourer"] = glam!;
            manifest.SizeHints.Add(new SizeHint { Path = "glamourer", Size = Encoding.UTF8.GetByteCount(glam!) });
        }

        var customize = TryInvokeIpc<string>("Customize.GetCharacterProfile") ?? TryInvokeIpc<string>("Customize.GetActiveProfile");
        if (!string.IsNullOrWhiteSpace(customize))
        {
            manifest.Appearance.CustomState["customize+"] = customize!;
            manifest.SizeHints.Add(new SizeHint { Path = "customize+", Size = Encoding.UTF8.GetByteCount(customize!) });
        }

        var simpleHeels = TryInvokeIpc<string>("SimpleHeels.GetCurrentProfile")
            ?? TryInvokeIpc<string>("SimpleHeels.GetProfile");
        if (!string.IsNullOrWhiteSpace(simpleHeels))
        {
            manifest.Appearance.CustomState["simpleheels"] = simpleHeels!;
            manifest.SizeHints.Add(new SizeHint { Path = "simpleheels", Size = Encoding.UTF8.GetByteCount(simpleHeels!) });
        }

        manifest.SizeHints.Add(new SizeHint
        {
            Path = penumbraPaths.CollectionName,
            Size = collection.Mods.Sum(static m => m.Size) + collection.Patches.Sum(static p => p.Size),
        });

        manifest.Appearance.ActiveMods.AddRange(collection.Mods.Where(static m => m.Enabled).Select(static m => m.ModId));

        return manifest;
    }

    /// <inheritdoc />
    public IEnumerable<string> GetMissingBlobs(SyncManifest manifest)
    {
        if (manifest is null)
        {
            throw new ArgumentNullException(nameof(manifest));
        }

        var missing = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var hash in EnumerateAllHashes(manifest))
        {
            if (missing.Contains(hash))
            {
                continue;
            }

            if (!_blobStore.Has(hash))
            {
                missing.Add(hash);
            }
        }

        return missing;
    }

    /// <inheritdoc />
    public async Task ApplyManifestAsync(string peerId, SyncManifest manifest, PenumbraResolveOptions? options = null, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(peerId))
        {
            throw new ArgumentException("Peer id must be supplied", nameof(peerId));
        }

        if (manifest is null)
        {
            throw new ArgumentNullException(nameof(manifest));
        }

        var paths = ResolvePenumbraPaths(options);
        if (paths is null)
        {
            throw new InvalidOperationException("Penumbra directories are not available");
        }

        cancellationToken.ThrowIfCancellationRequested();

        var syncShellRoot = Path.Combine(paths.ModsDirectory, "SyncShell", SanitizeFileName(peerId));
        Directory.CreateDirectory(syncShellRoot);

        foreach (var collection in manifest.Collections)
        {
            foreach (var mod in collection.Mods)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var modRoot = Path.Combine(syncShellRoot, SanitizeFileName(mod.ModId));
                Directory.CreateDirectory(modRoot);

                foreach (var file in mod.Files)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    var destination = Path.Combine(modRoot, file.Path.Replace('/', Path.DirectorySeparatorChar));
                    Directory.CreateDirectory(Path.GetDirectoryName(destination)!);

                    await using var destinationStream = File.Create(destination);
                    await using var blobStream = _blobStore.OpenRead(file.Hash);
                    await blobStream.CopyToAsync(destinationStream, cancellationToken).ConfigureAwait(false);
                }
            }

            foreach (var patch in collection.Patches)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var patchPath = Path.Combine(syncShellRoot, "patches", patch.Path.Replace('/', Path.DirectorySeparatorChar));
                Directory.CreateDirectory(Path.GetDirectoryName(patchPath)!);

                await using var destinationStream = File.Create(patchPath);
                await using var blobStream = _blobStore.OpenRead(patch.Hash);
                await blobStream.CopyToAsync(destinationStream, cancellationToken).ConfigureAwait(false);
            }
        }

        UpdateDefaultMod(paths, manifest, peerId);

        InvokeIpcSafe("Penumbra.Reload");
        InvokeIpcSafe("Penumbra.RedrawAll");

        if (manifest.Appearance.CustomState.TryGetValue("glamourer", out var glam) && !string.IsNullOrWhiteSpace(glam))
        {
            InvokeIpcSafe("Glamourer.Design.Apply", glam);
        }

        if (manifest.Appearance.CustomState.TryGetValue("customize+", out var customize) && !string.IsNullOrWhiteSpace(customize))
        {
            InvokeIpcSafe("Customize.ApplyProfile", customize);
        }

        if (manifest.Appearance.CustomState.TryGetValue("simpleheels", out var heels) && !string.IsNullOrWhiteSpace(heels))
        {
            InvokeIpcSafe("SimpleHeels.ApplyProfile", heels);
        }
    }

    private async Task PopulateModsAsync(CollectionDelta collection, PenumbraPaths paths, CancellationToken cancellationToken)
    {
        var defaultModPath = paths.DefaultModPath;
        if (!File.Exists(defaultModPath))
        {
            _log.Warning("default_mod.json not found at {Path}", defaultModPath);
            return;
        }

        var modsState = await ReadDefaultModAsync(defaultModPath, cancellationToken).ConfigureAwait(false);
        foreach (var modState in modsState)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var modDirectory = Path.Combine(paths.ModsDirectory, modState.DirectoryName);
            if (!Directory.Exists(modDirectory))
            {
                continue;
            }

            try
            {
                var modEntry = await BuildModEntryAsync(modDirectory, modState, cancellationToken).ConfigureAwait(false);
                collection.Mods.Add(modEntry);
            }
            catch (Exception ex)
            {
                _log.Error(ex, "Failed to build manifest for mod {Mod}", modState.DirectoryName);
            }
        }
    }

    private async Task PopulatePatchesAsync(CollectionDelta collection, PenumbraPaths paths, CancellationToken cancellationToken)
    {
        var patchesDirectory = Path.Combine(paths.ModsDirectory, "_patches");
        if (!Directory.Exists(patchesDirectory))
        {
            return;
        }

        foreach (var file in Directory.EnumerateFiles(patchesDirectory, "*", SearchOption.AllDirectories))
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                await using var stream = OpenFileRead(file);
                var (hash, size) = await HashStreamAsync(stream, cancellationToken).ConfigureAwait(false);
                if (!_blobStore.Has(hash))
                {
                    stream.Seek(0, SeekOrigin.Begin);
                    await _blobStore.StoreAsync(hash, stream, cancellationToken).ConfigureAwait(false);
                }

                var relativePath = Path.GetRelativePath(patchesDirectory, file).Replace(Path.DirectorySeparatorChar, '/');
                collection.Patches.Add(new PatchEntry
                {
                    Path = relativePath,
                    Hash = hash,
                    Size = size,
                    Kind = "file",
                });
            }
            catch (Exception ex)
            {
                _log.Warning(ex, "Failed to process patch file {File}", file);
            }
        }
    }

    private async Task<ModEntry> BuildModEntryAsync(string modDirectory, PenumbraModState modState, CancellationToken cancellationToken)
    {
        var modEntry = new ModEntry
        {
            ModId = modState.Identifier,
            Name = modState.Name,
            Enabled = modState.Enabled,
        };

        foreach (var option in modState.Options)
        {
            modEntry.Options[option.Key] = option.Value;
        }

        var files = Directory.EnumerateFiles(modDirectory, "*", SearchOption.AllDirectories)
            .Where(static f => !f.EndsWith("meta.json", StringComparison.OrdinalIgnoreCase))
            .Where(static f => !f.EndsWith("default_mod.json", StringComparison.OrdinalIgnoreCase))
            .ToList();

        using var aggregateHash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        long totalSize = 0;

        foreach (var file in files)
        {
            cancellationToken.ThrowIfCancellationRequested();

            await using var stream = OpenFileRead(file);
            var (hash, size) = await HashStreamAsync(stream, cancellationToken).ConfigureAwait(false);
            if (!_blobStore.Has(hash))
            {
                stream.Seek(0, SeekOrigin.Begin);
                await _blobStore.StoreAsync(hash, stream, cancellationToken).ConfigureAwait(false);
            }

            totalSize += size;
            aggregateHash.AppendData(Convert.FromHexString(hash));

            var relativePath = Path.GetRelativePath(modDirectory, file).Replace(Path.DirectorySeparatorChar, '/');
            modEntry.Files.Add(new FileHash
            {
                Path = relativePath,
                Hash = hash,
                Size = size,
            });
        }

        modEntry.Size = totalSize;
        modEntry.Hash = Convert.ToHexString(aggregateHash.GetHashAndReset()).ToLowerInvariant();

        var metaPath = Path.Combine(modDirectory, "meta.json");
        if (File.Exists(metaPath))
        {
            await using var stream = OpenFileRead(metaPath);
            var meta = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
            await PopulateModMetaAsync(modEntry, meta.RootElement, modDirectory, cancellationToken).ConfigureAwait(false);
        }

        return modEntry;
    }

    private async Task PopulateModMetaAsync(ModEntry modEntry, JsonElement meta, string modDirectory, CancellationToken cancellationToken)
    {
        if (meta.TryGetProperty("Tags", out var tags) && tags.ValueKind == JsonValueKind.Array)
        {
            foreach (var tag in tags.EnumerateArray())
            {
                if (tag.ValueKind == JsonValueKind.String)
                {
                    modEntry.Tags.Add(tag.GetString()!);
                }
            }
        }

        if (meta.TryGetProperty("FileSwaps", out var swaps) && swaps.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in swaps.EnumerateObject())
            {
                cancellationToken.ThrowIfCancellationRequested();

                var target = property.Name;
                var value = property.Value;
                var path = value.TryGetProperty("Path", out var pathElement)
                    ? pathElement.GetString()
                    : value.TryGetProperty("File", out var fileElement)
                        ? fileElement.GetString()
                        : value.ValueKind == JsonValueKind.String
                            ? value.GetString()
                            : null;

                if (string.IsNullOrEmpty(path))
                {
                    continue;
                }

                var localPath = Path.Combine(modDirectory, path.Replace('/', Path.DirectorySeparatorChar));
                if (!File.Exists(localPath))
                {
                    continue;
                }

                var kind = value.TryGetProperty("Type", out var typeElement)
                    ? typeElement.GetString()
                    : value.TryGetProperty("Kind", out var kindElement)
                        ? kindElement.GetString()
                        : null;

                await using var patchStream = OpenFileRead(localPath);
                var (hash, size) = await HashStreamAsync(patchStream, cancellationToken).ConfigureAwait(false);
                if (!_blobStore.Has(hash))
                {
                    patchStream.Seek(0, SeekOrigin.Begin);
                    await _blobStore.StoreAsync(hash, patchStream, cancellationToken).ConfigureAwait(false);
                }

                modEntry.Patches.Add(new PatchEntry
                {
                    Path = target,
                    Hash = hash,
                    Size = size,
                    Kind = kind,
                    Target = path,
                });
            }
        }

        if (meta.TryGetProperty("Meta", out var metaEntries) && metaEntries.ValueKind == JsonValueKind.Object)
        {
            foreach (var entry in metaEntries.EnumerateObject())
            {
                modEntry.Meta.Add(new MetaEntry
                {
                    Key = entry.Name,
                    Value = entry.Value.ToString() ?? string.Empty,
                    Scope = "mod",
                });
            }
        }
    }

    private static async Task<List<PenumbraModState>> ReadDefaultModAsync(string path, CancellationToken cancellationToken)
    {
        await using var stream = OpenFileRead(path);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);

        var mods = new List<PenumbraModState>();
        if (document.RootElement.TryGetProperty("Mods", out var modsElement) && modsElement.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in modsElement.EnumerateObject())
            {
                var state = ParseModState(property.Name, property.Value);
                mods.Add(state);
            }
        }
        else
        {
            foreach (var property in document.RootElement.EnumerateObject())
            {
                if (property.Value.ValueKind == JsonValueKind.Object)
                {
                    mods.Add(ParseModState(property.Name, property.Value));
                }
            }
        }

        return mods;
    }

    private static PenumbraModState ParseModState(string modId, JsonElement element)
    {
        var name = element.TryGetProperty("Name", out var nameElement) && nameElement.ValueKind == JsonValueKind.String
            ? nameElement.GetString()
            : modId;

        var enabled = element.TryGetProperty("Enabled", out var enabledElement)
            ? enabledElement.ValueKind switch
            {
                JsonValueKind.True => true,
                JsonValueKind.False => false,
                JsonValueKind.Number => enabledElement.TryGetInt32(out var value) && value != 0,
                JsonValueKind.String => bool.TryParse(enabledElement.GetString(), out var parsed) && parsed,
                _ => false,
            }
            : false;

        var directory = element.TryGetProperty("Directory", out var dirElement) && dirElement.ValueKind == JsonValueKind.String
            ? dirElement.GetString()
            : element.TryGetProperty("ModPath", out var modPathElement) && modPathElement.ValueKind == JsonValueKind.String
                ? modPathElement.GetString()
                : modId;

        directory ??= modId;

        var options = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        PopulateOptions(options, element, "Settings");
        PopulateOptions(options, element, "Options");
        PopulateOptions(options, element, "ModSettings");

        return new PenumbraModState
        {
            Identifier = modId,
            Name = name,
            DirectoryName = directory!,
            Enabled = enabled,
            Options = options,
        };
    }

    private static void PopulateOptions(IDictionary<string, string> target, JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var settings) || settings.ValueKind != JsonValueKind.Object)
        {
            return;
        }

        foreach (var property in settings.EnumerateObject())
        {
            target[property.Name] = property.Value.ToString() ?? string.Empty;
        }
    }

    private static IEnumerable<string> EnumerateAllHashes(SyncManifest manifest)
    {
        foreach (var collection in manifest.Collections)
        {
            foreach (var mod in collection.Mods)
            {
                foreach (var file in mod.Files)
                {
                    yield return file.Hash;
                }

                foreach (var patch in mod.Patches)
                {
                    if (!string.IsNullOrEmpty(patch.Hash))
                    {
                        yield return patch.Hash;
                    }
                }
            }

            foreach (var patch in collection.Patches)
            {
                if (!string.IsNullOrEmpty(patch.Hash))
                {
                    yield return patch.Hash;
                }
            }
        }
    }

    private void UpdateDefaultMod(PenumbraPaths paths, SyncManifest manifest, string peerId)
    {
        try
        {
            var defaultModPath = paths.DefaultModPath;
            var collection = new PenumbraCollectionConfig();
            if (File.Exists(defaultModPath))
            {
                var json = File.ReadAllText(defaultModPath);
                if (!string.IsNullOrWhiteSpace(json))
                {
                    collection = JsonSerializer.Deserialize<PenumbraCollectionConfig>(json, JsonOptions) ?? new PenumbraCollectionConfig();
                }
            }

            foreach (var mod in manifest.Collections.SelectMany(static c => c.Mods))
            {
                var directory = Path.Combine("SyncShell", SanitizeFileName(peerId), SanitizeFileName(mod.ModId));
                collection.Mods[mod.ModId] = new PenumbraCollectionConfig.ModState
                {
                    Enabled = mod.Enabled,
                    Directory = directory,
                    Options = mod.Options.ToDictionary(static kvp => kvp.Key, static kvp => kvp.Value, StringComparer.OrdinalIgnoreCase),
                };
            }

            var jsonText = JsonSerializer.Serialize(collection, JsonOptions);
            File.WriteAllText(defaultModPath, jsonText);
        }
        catch (Exception ex)
        {
            _log.Error(ex, "Failed to update default_mod.json");
        }
    }

    private PenumbraPaths? ResolvePenumbraPaths(PenumbraResolveOptions? options)
    {
        string? modsDirectory = null;
        string? configDirectory = null;

        var overrideMods = ValidateConfiguredDirectory(options?.ModsDirectory, "Penumbra mods directory override");
        if (!string.IsNullOrEmpty(overrideMods))
        {
            modsDirectory = overrideMods;
        }

        if (string.IsNullOrWhiteSpace(modsDirectory))
        {
            var configuredMods = ValidateConfiguredDirectory(_config.PenumbraModsDirectory, "Penumbra mods directory");
            if (!string.IsNullOrEmpty(configuredMods))
            {
                modsDirectory = configuredMods;
            }
        }

        if (string.IsNullOrWhiteSpace(modsDirectory))
        {
            try
            {
                modsDirectory = TryInvokeIpc<string>("Penumbra.GetModsDirectory");
            }
            catch (Exception ex)
            {
                _log.Warning(ex, "Failed to retrieve Penumbra mods directory via IPC");
            }
        }

        if (string.IsNullOrWhiteSpace(modsDirectory))
        {
            modsDirectory = PenumbraConfigCandidates
                .Select(static path => Path.Combine(path, "mods"))
                .FirstOrDefault(Directory.Exists);
        }

        if (string.IsNullOrWhiteSpace(modsDirectory) || !Directory.Exists(modsDirectory))
        {
            return null;
        }

        var overrideConfig = ValidateConfiguredDirectory(options?.ConfigDirectory, "Penumbra config directory override");
        if (!string.IsNullOrEmpty(overrideConfig))
        {
            configDirectory = overrideConfig;
        }

        if (string.IsNullOrWhiteSpace(configDirectory))
        {
            var configuredConfig = ValidateConfiguredDirectory(_config.PenumbraConfigDirectory, "Penumbra config directory");
            if (!string.IsNullOrEmpty(configuredConfig))
            {
                configDirectory = configuredConfig;
            }
        }

        if (string.IsNullOrWhiteSpace(configDirectory))
        {
            try
            {
                configDirectory = TryInvokeIpc<string>("Penumbra.GetConfigurationDirectory")
                    ?? TryInvokeIpc<string>("Penumbra.GetConfigDirectory");
            }
            catch (Exception ex)
            {
                _log.Debug(ex, "Failed to retrieve Penumbra config directory via IPC");
            }
        }

        if (string.IsNullOrWhiteSpace(configDirectory) || !Directory.Exists(configDirectory))
        {
            var parent = Directory.GetParent(modsDirectory);
            while (parent != null)
            {
                var candidate = Path.Combine(parent.FullName, "default_mod.json");
                if (File.Exists(candidate))
                {
                    configDirectory = parent.FullName;
                    break;
                }

                candidate = Path.Combine(parent.FullName, "config");
                if (Directory.Exists(candidate) && File.Exists(Path.Combine(candidate, "default_mod.json")))
                {
                    configDirectory = candidate;
                    break;
                }

                parent = parent.Parent;
            }
        }

        if (string.IsNullOrWhiteSpace(configDirectory))
        {
            configDirectory = PenumbraConfigCandidates.FirstOrDefault(Directory.Exists);
        }

        if (string.IsNullOrWhiteSpace(configDirectory))
        {
            return null;
        }

        var collectionOverride = options?.Collection;
        if (string.IsNullOrWhiteSpace(collectionOverride))
        {
            collectionOverride = _config.PenumbraCollectionOverride;
        }

        var defaultMod = ResolveCollectionPath(configDirectory, collectionOverride, out var collectionName);
        if (string.IsNullOrEmpty(defaultMod))
        {
            return null;
        }

        return new PenumbraPaths(modsDirectory, configDirectory, defaultMod, collectionName);
    }

    private string? ResolveCollectionPath(string configDirectory, string? collectionOverride, out string collectionName)
    {
        var defaultMod = Path.Combine(configDirectory, "default_mod.json");
        var fallbackAvailable = File.Exists(defaultMod);

        if (!string.IsNullOrWhiteSpace(collectionOverride))
        {
            var trimmed = collectionOverride.Trim();
            foreach (var candidate in EnumerateCollectionPathCandidates(configDirectory, trimmed))
            {
                try
                {
                    if (File.Exists(candidate))
                    {
                        collectionName = NormalizeCollectionName(trimmed, candidate);
                        return candidate;
                    }
                }
                catch (Exception ex)
                {
                    _log.Debug(ex, "Failed to inspect Penumbra collection candidate {Candidate}", candidate);
                }
            }

            _log.Warning("Specified Penumbra collection '{Collection}' not found; falling back to default_mod.json", trimmed);
        }

        if (fallbackAvailable)
        {
            collectionName = "default";
            return defaultMod;
        }

        _log.Warning("Unable to locate default_mod.json in {ConfigDirectory}", configDirectory);
        collectionName = string.IsNullOrWhiteSpace(collectionOverride)
            ? "default"
            : NormalizeCollectionName(collectionOverride, null);
        return null;
    }

    private static string NormalizeCollectionName(string rawName, string? resolvedPath)
    {
        if (string.IsNullOrWhiteSpace(rawName))
        {
            return "default";
        }

        var name = rawName.Trim();
        if (Path.IsPathRooted(name))
        {
            var fileName = Path.GetFileNameWithoutExtension(name);
            name = string.IsNullOrWhiteSpace(fileName) ? name : fileName;
        }
        else if (!string.IsNullOrWhiteSpace(resolvedPath))
        {
            var fileName = Path.GetFileNameWithoutExtension(resolvedPath);
            if (!string.IsNullOrWhiteSpace(fileName))
            {
                name = fileName;
            }
        }

        if (name.EndsWith("_mod", StringComparison.OrdinalIgnoreCase))
        {
            name = name[..^4];
        }

        name = name.Replace('\', '/');
        var slashIndex = name.LastIndexOf('/');
        if (slashIndex >= 0)
        {
            name = name[(slashIndex + 1)..];
        }

        return string.IsNullOrWhiteSpace(name) ? "default" : name;
    }

    private IEnumerable<string> EnumerateCollectionPathCandidates(string configDirectory, string identifier)
    {
        var results = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void AddCandidate(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return;
            }

            var candidate = path;
            if (!Path.IsPathRooted(candidate))
            {
                candidate = Path.Combine(configDirectory, candidate);
            }

            try
            {
                candidate = Path.GetFullPath(candidate);
            }
            catch
            {
                return;
            }

            if (seen.Add(candidate))
            {
                results.Add(candidate);
            }
        }

        if (string.IsNullOrWhiteSpace(identifier))
        {
            return results;
        }

        var trimmed = identifier.Trim();
        var sanitized = trimmed.Replace('/', Path.DirectorySeparatorChar).Replace('\\', Path.DirectorySeparatorChar);

        AddCandidate(trimmed);
        if (!ReferenceEquals(trimmed, sanitized))
        {
            AddCandidate(sanitized);
        }

        if (!sanitized.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
        {
            AddCandidate(sanitized + ".json");
        }

        if (!sanitized.EndsWith("_mod.json", StringComparison.OrdinalIgnoreCase))
        {
            if (sanitized.EndsWith("_mod", StringComparison.OrdinalIgnoreCase))
            {
                AddCandidate(sanitized + ".json");
            }
            else
            {
                AddCandidate(sanitized + "_mod.json");
            }
        }

        var collectionsDir = Path.Combine(configDirectory, "collections");
        if (Directory.Exists(collectionsDir))
        {
            AddCandidate(Path.Combine("collections", sanitized));
            if (!sanitized.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
            {
                AddCandidate(Path.Combine("collections", sanitized + ".json"));
            }

            if (!sanitized.EndsWith("_mod.json", StringComparison.OrdinalIgnoreCase))
            {
                if (sanitized.EndsWith("_mod", StringComparison.OrdinalIgnoreCase))
                {
                    AddCandidate(Path.Combine("collections", sanitized + ".json"));
                }
                else
                {
                    AddCandidate(Path.Combine("collections", sanitized + "_mod.json"));
                }
            }
        }

        return results;
    }

    private string? ValidateConfiguredDirectory(string? path, string description)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        var trimmed = path.Trim();
        try
        {
            if (!Directory.Exists(trimmed))
            {
                _log.Warning($"Configured {description} '{trimmed}' does not exist; falling back to automatic detection.");
                return null;
            }
        }
        catch (Exception ex)
        {
            _log.Warning(ex, $"Configured {description} '{trimmed}' is invalid; falling back to automatic detection.");
            return null;
        }

        return trimmed;
    }

    private T? TryInvokeIpc<T>(string channel)
    {
        if (_pluginInterface is null)
        {
            return default;
        }

        try
        {
            var subscriber = _pluginInterface.GetIpcSubscriber<T>(channel);
            return subscriber.InvokeFunc();
        }
        catch (Exception ex)
        {
            _log.Debug(ex, "IPC call {Channel} failed", channel);
            return default;
        }
    }

    private void InvokeIpcSafe(string channel)
    {
        if (_pluginInterface is null)
        {
            return;
        }

        try
        {
            _pluginInterface.GetIpcSubscriber<object>(channel).InvokeAction();
        }
        catch (Exception ex)
        {
            _log.Warning(ex, "IPC call {Channel} failed", channel);
        }
    }

    private void InvokeIpcSafe(string channel, string payload)
    {
        if (_pluginInterface is null)
        {
            return;
        }

        try
        {
            _pluginInterface.GetIpcSubscriber<string, object?>(channel).InvokeAction(payload);
        }
        catch (Exception ex)
        {
            _log.Warning(ex, "IPC call {Channel} failed", channel);
        }
    }

    private static FileStream OpenFileRead(string path)
        => new(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete, 4096, FileOptions.Asynchronous | FileOptions.SequentialScan);

    private static async Task<(string Hash, long Size)> HashStreamAsync(Stream stream, CancellationToken cancellationToken)
    {
        using var sha = SHA256.Create();
        var buffer = ArrayPool<byte>.Shared.Rent(81920);
        long total = 0;
        try
        {
            int read;
            while ((read = await stream.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken).ConfigureAwait(false)) > 0)
            {
                sha.TransformBlock(buffer, 0, read, null, 0);
                total += read;
            }

            sha.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
            if (stream.CanSeek)
            {
                stream.Seek(0, SeekOrigin.Begin);
            }

            return (Convert.ToHexString(sha.Hash!).ToLowerInvariant(), total);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private static string SanitizeFileName(string value)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var builder = new StringBuilder(value.Length);
        foreach (var ch in value)
        {
            builder.Append(invalid.Contains(ch) ? '_' : ch);
        }

        return builder.ToString();
    }

    private sealed record PenumbraPaths(string ModsDirectory, string ConfigDirectory, string DefaultModPath, string CollectionName);

    private sealed class PenumbraModState
    {
        public string Identifier { get; init; } = string.Empty;
        public string DirectoryName { get; init; } = string.Empty;
        public string? Name { get; init; }
        public bool Enabled { get; init; }
        public Dictionary<string, string> Options { get; init; } = new(StringComparer.OrdinalIgnoreCase);
    }

    private sealed class PenumbraCollectionConfig
    {
        public Dictionary<string, ModState> Mods { get; init; } = new(StringComparer.OrdinalIgnoreCase);

        public sealed class ModState
        {
            public bool Enabled { get; init; }
            public string Directory { get; init; } = string.Empty;
            public Dictionary<string, string> Options { get; init; } = new(StringComparer.OrdinalIgnoreCase);
        }
    }
}
