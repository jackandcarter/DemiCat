using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Dalamud.Interface.Textures;
using Dalamud.Interface.Textures.TextureWraps;
using Dalamud.Plugin.Services;
using StbImageSharp;

namespace DemiCatPlugin;

internal sealed class DockIconLoader : IDisposable
{
    private static readonly string[] ExpectedIconIds =
    {
        "events",
        "create",
        "templates",
        "notepad",
        "requests",
        "chat",
        "officer",
        "syncshell",
        "settings"
    };

    private readonly Dictionary<string, ISharedImmediateTexture> _icons = new(StringComparer.OrdinalIgnoreCase);
    private readonly ISharedImmediateTexture? _placeholder;
    private readonly IPluginLog? _log;
    private bool _disposed;

    public DockIconLoader()
    {
        var services = PluginServices.Instance ?? throw new InvalidOperationException("Plugin services are not initialized.");
        _log = services.Log;

        _placeholder = CreatePlaceholderTexture(services.TextureProvider);
        LoadDockIcons(services.PluginInterface.AssemblyLocation.Directory, services.TextureProvider);
    }

    public ISharedImmediateTexture? Placeholder => _placeholder;

    public ISharedImmediateTexture? Get(string id)
    {
        if (string.IsNullOrEmpty(id))
            return null;

        return _icons.TryGetValue(id, out var texture) ? texture : null;
    }

    private void LoadDockIcons(DirectoryInfo? assemblyDirectory, ITextureProvider textureProvider)
    {
        if (assemblyDirectory == null)
        {
            _log?.Warning("Unable to resolve plugin assembly directory when loading dock icons.");
            return;
        }

        var dockDirectory = Path.Combine(assemblyDirectory.FullName, "Dock");
        if (!Directory.Exists(dockDirectory))
        {
            _log?.Warning("Dock icon directory not found: {Directory}", dockDirectory);
            return;
        }

        foreach (var file in Directory.EnumerateFiles(dockDirectory, "*.png", SearchOption.TopDirectoryOnly))
        {
            var id = Path.GetFileNameWithoutExtension(file);
            if (string.IsNullOrWhiteSpace(id))
                continue;

            var normalizedId = id.ToLowerInvariant();
            try
            {
                using var stream = File.OpenRead(file);
                var image = ImageResult.FromStream(stream, ColorComponents.RedGreenBlueAlpha);
                var texture = textureProvider.CreateFromRaw(
                    RawImageSpecification.Rgba32(image.Width, image.Height),
                    image.Data);

                if (_icons.TryGetValue(normalizedId, out var existing))
                {
                    existing.Dispose();
                }

                _icons[normalizedId] = texture;
            }
            catch (Exception ex)
            {
                _log?.Warning(ex, "Failed to load dock icon from {Path}", file);
            }
        }

        ValidateExpectedIcons(dockDirectory);
    }

    private void ValidateExpectedIcons(string dockDirectory)
    {
        var missing = ExpectedIconIds.Where(id => !_icons.ContainsKey(id)).ToList();
        if (missing.Count > 0)
        {
            _log?.Warning("Missing dock icon assets in {Directory}: {Icons}", dockDirectory, string.Join(", ", missing));
        }
        else
        {
            _log?.Debug("Loaded dock icons: {Icons}", string.Join(", ", _icons.Keys.OrderBy(id => id)));
        }
    }

    private ISharedImmediateTexture? CreatePlaceholderTexture(ITextureProvider provider)
    {
        try
        {
            var pixel = new byte[] { 255, 255, 255, 255 };
            return provider.CreateFromRaw(RawImageSpecification.Rgba32(1, 1), pixel);
        }
        catch (Exception ex)
        {
            _log?.Warning(ex, "Failed to create dock icon placeholder texture.");
            return null;
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        foreach (var texture in _icons.Values)
        {
            texture.Dispose();
        }

        _icons.Clear();
        _placeholder?.Dispose();
        _disposed = true;
    }
}
