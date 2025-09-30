using System;

namespace DemiCatPlugin.SyncShell;

/// <summary>
/// Provides override information for resolving Penumbra paths and collections.
/// </summary>
/// <param name="ModsDirectory">Optional override for the mods root directory.</param>
/// <param name="ConfigDirectory">Optional override for the configuration directory.</param>
/// <param name="Collection">Optional override for the active collection identifier.</param>
public sealed record PenumbraResolveOptions(string? ModsDirectory, string? ConfigDirectory, string? Collection)
{
    /// <summary>
    /// Creates an instance populated from plugin configuration.
    /// </summary>
    public static PenumbraResolveOptions FromConfig(Config config)
    {
        if (config == null)
            throw new ArgumentNullException(nameof(config));

        var mods = string.IsNullOrWhiteSpace(config.PenumbraModsDirectory)
            ? null
            : config.PenumbraModsDirectory.Trim();
        var cfg = string.IsNullOrWhiteSpace(config.PenumbraConfigDirectory)
            ? null
            : config.PenumbraConfigDirectory.Trim();
        var collection = string.IsNullOrWhiteSpace(config.PenumbraCollectionOverride)
            ? null
            : config.PenumbraCollectionOverride.Trim();

        return new PenumbraResolveOptions(mods, cfg, collection);
    }
}
