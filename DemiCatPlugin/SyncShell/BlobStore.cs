using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;

namespace DemiCatPlugin.SyncShell;

/// <summary>
/// Provides access to a local disk-backed blob cache used by SyncShell transfers.
/// </summary>
public sealed class BlobStore : IDisposable
{
    private const string RootFolderName = "syncshell";
    private const string BlobFolderName = "blobs";
    private const string TempFolderName = "tmp";
    private const int DefaultBufferSize = 64 * 1024;

    private readonly IDalamudPluginInterface _pluginInterface;
    private readonly string _rootPath;
    private readonly string _blobPath;
    private readonly string _tempPath;
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _locks = new(StringComparer.OrdinalIgnoreCase);
    private bool _disposed;

    public BlobStore(IDalamudPluginInterface pluginInterface)
    {
        _pluginInterface = pluginInterface ?? throw new ArgumentNullException(nameof(pluginInterface));

        _rootPath = Path.Combine(_pluginInterface.GetPluginConfigDirectory(), RootFolderName);
        _blobPath = Path.Combine(_rootPath, BlobFolderName);
        _tempPath = Path.Combine(_rootPath, TempFolderName);

        Directory.CreateDirectory(_blobPath);
        Directory.CreateDirectory(_tempPath);
    }

    /// <summary>
    /// Attempts to get the path to an existing blob.
    /// </summary>
    public bool TryGet(string sha256, out string fullPath)
    {
        ThrowIfDisposed();
        if (string.IsNullOrWhiteSpace(sha256))
        {
            fullPath = string.Empty;
            return false;
        }

        var shard = GetShard(sha256);
        var candidate = Path.Combine(_blobPath, shard, sha256);
        if (File.Exists(candidate))
        {
            fullPath = candidate;
            TryTouch(candidate);
            return true;
        }

        fullPath = string.Empty;
        return false;
    }

    /// <summary>
    /// Writes the supplied content stream to the cache and returns its final path.
    /// </summary>
    public async Task<string> PutAsync(Stream content, string sha256, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        if (content is null)
        {
            throw new ArgumentNullException(nameof(content));
        }

        if (string.IsNullOrWhiteSpace(sha256))
        {
            throw new ArgumentException("SHA-256 must be provided", nameof(sha256));
        }

        var lockHandle = GetLock(sha256);
        await lockHandle.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var targetDirectory = Path.Combine(_blobPath, GetShard(sha256));
            Directory.CreateDirectory(targetDirectory);

            var finalPath = Path.Combine(targetDirectory, sha256);
            var tempPath = Path.Combine(_tempPath, Guid.NewGuid().ToString("N"));

            await using (var file = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None, DefaultBufferSize, useAsync: true))
            {
                await content.CopyToAsync(file, DefaultBufferSize, cancellationToken).ConfigureAwait(false);
                await file.FlushAsync(cancellationToken).ConfigureAwait(false);
            }

            File.Move(tempPath, finalPath, overwrite: true);
            TryTouch(finalPath);
            return finalPath;
        }
        finally
        {
            lockHandle.Release();
        }
    }

    /// <summary>
    /// Ensures that the blob with the supplied hash exists locally.
    /// </summary>
    public async Task<string?> EnsureLocalAsync(string sha256, Func<CancellationToken, Task<Stream>> download, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        if (string.IsNullOrWhiteSpace(sha256))
        {
            throw new ArgumentException("SHA-256 must be provided", nameof(sha256));
        }

        if (TryGet(sha256, out var existing))
        {
            return existing;
        }

        if (download == null)
        {
            throw new ArgumentNullException(nameof(download));
        }

        var lockHandle = GetLock(sha256);
        await lockHandle.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (TryGet(sha256, out existing))
            {
                return existing;
            }

            await using var stream = await download(cancellationToken).ConfigureAwait(false);
            if (stream == null)
            {
                return null;
            }

            var targetDirectory = Path.Combine(_blobPath, GetShard(sha256));
            Directory.CreateDirectory(targetDirectory);
            var tempPath = Path.Combine(_tempPath, Guid.NewGuid().ToString("N"));

            await using (var file = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None, DefaultBufferSize, useAsync: true))
            {
                await stream.CopyToAsync(file, DefaultBufferSize, cancellationToken).ConfigureAwait(false);
                await file.FlushAsync(cancellationToken).ConfigureAwait(false);
            }

            var computed = Hasher.Sha256File(tempPath);
            if (!string.Equals(computed, sha256, StringComparison.OrdinalIgnoreCase))
            {
                File.Delete(tempPath);
                throw new InvalidDataException($"Blob hash mismatch: expected {sha256}, received {computed}");
            }

            var finalPath = Path.Combine(targetDirectory, sha256);
            File.Move(tempPath, finalPath, overwrite: true);
            TryTouch(finalPath);
            return finalPath;
        }
        finally
        {
            lockHandle.Release();
        }
    }

    public string GetLocalPath(string sha256)
    {
        ThrowIfDisposed();
        if (string.IsNullOrWhiteSpace(sha256))
        {
            throw new ArgumentException("SHA-256 must be provided", nameof(sha256));
        }

        if (TryGet(sha256, out var existing))
        {
            return existing;
        }

        var candidate = Path.Combine(_blobPath, GetShard(sha256), sha256);
        if (!File.Exists(candidate))
        {
            throw new FileNotFoundException("Blob not found", candidate);
        }

        TryTouch(candidate);
        return candidate;
    }

    /// <summary>
    /// Clears the cache respecting the configured size limit.
    /// </summary>
    public async Task EnforceLimitAsync(long limitBytes, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        if (limitBytes <= 0)
        {
            return;
        }

        var files = Directory.Exists(_blobPath)
            ? Directory.EnumerateFiles(_blobPath, "*", SearchOption.AllDirectories)
                .Select(path => new FileInfo(path))
                .Where(info => info.Exists)
                .OrderBy(info => info.LastWriteTimeUtc)
                .ToList()
            : new List<FileInfo>();

        long total = files.Sum(f => f.Length);
        foreach (var info in files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (total <= limitBytes)
            {
                break;
            }

            try
            {
                total -= info.Length;
                info.Delete();
            }
            catch
            {
                // Ignore failures – we'll try again next maintenance cycle.
            }
        }

        await Task.CompletedTask.ConfigureAwait(false);
    }

    public string GetWorkspacePath(string name)
    {
        ThrowIfDisposed();
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new ArgumentException("Name must be provided", nameof(name));
        }

        var sanitized = SanitizeWorkspaceName(name);
        var path = Path.Combine(_tempPath, sanitized);
        Directory.CreateDirectory(path);
        return path;
    }

    /// <summary>
    /// Removes all cached blobs.
    /// </summary>
    public void Clear()
    {
        ThrowIfDisposed();
        if (!Directory.Exists(_blobPath))
        {
            return;
        }

        foreach (var directory in Directory.EnumerateDirectories(_blobPath))
        {
            try
            {
                Directory.Delete(directory, recursive: true);
            }
            catch
            {
                // ignored
            }
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        foreach (var handle in _locks.Values)
        {
            handle.Dispose();
        }

        _locks.Clear();
    }

    private SemaphoreSlim GetLock(string sha256)
        => _locks.GetOrAdd(sha256, _ => new SemaphoreSlim(1, 1));

    private void ThrowIfDisposed()
    {
        if (_disposed)
        {
            throw new ObjectDisposedException(nameof(BlobStore));
        }
    }

    private static string SanitizeWorkspaceName(string value)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var buffer = new char[value.Length];
        for (var i = 0; i < value.Length; i++)
        {
            var c = value[i];
            buffer[i] = c == Path.DirectorySeparatorChar || c == Path.AltDirectorySeparatorChar || invalid.Contains(c)
                ? '_'
                : c;
        }

        return new string(buffer);
    }

    private static string GetShard(string sha256)
    {
        if (sha256.Length < 4)
        {
            return "0000";
        }

        return Path.Combine(sha256[..2], sha256.Substring(2, 2));
    }

    private static void TryTouch(string path)
    {
        try
        {
            File.SetLastWriteTimeUtc(path, DateTime.UtcNow);
        }
        catch
        {
            // ignored
        }
    }

    public static string? GuessDefaultPenumbraRoot(out bool fromSettingsJson)
    {
        fromSettingsJson = false;

        IEnumerable<string> Candidates()
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
                yield return Path.Combine(appData, "XIVLauncher", "pluginConfigs", "Penumbra", "settings.json");
            }

            if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            {
                var home = Environment.GetFolderPath(Environment.SpecialFolder.Personal);
                yield return Path.Combine(home, "Library", "Application Support", "XIV on Mac", "xivlauncher", "pluginConfigs", "Penumbra", "settings.json");
            }
        }

        foreach (var cfg in Candidates())
        {
            try
            {
                if (!File.Exists(cfg))
                {
                    continue;
                }

                using var doc = JsonDocument.Parse(File.ReadAllText(cfg));
                if (doc.RootElement.TryGetProperty("ModDirectory", out var md))
                {
                    var dir = md.GetString();
                    if (!string.IsNullOrWhiteSpace(dir) && Directory.Exists(dir))
                    {
                        fromSettingsJson = true;
                        return dir;
                    }
                }
            }
            catch
            {
                // ignored
            }
        }

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            var xivLauncher = Path.Combine(appData, "XIVLauncher", "pluginConfigs", "Penumbra");
            if (Directory.Exists(xivLauncher))
            {
                return xivLauncher;
            }
        }
        else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.Personal);
            var macPath = Path.Combine(home, "Library", "Application Support", "XIV on Mac", "xivlauncher", "pluginConfigs", "Penumbra");
            if (Directory.Exists(macPath))
            {
                return macPath;
            }
        }

        return null;
    }
}
