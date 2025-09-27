using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace DemiCatPlugin.SyncShell;

/// <summary>
/// Abstraction over the blob cache used for SyncShell transfers.
/// </summary>
public interface IBlobStore
{
    /// <summary>
    /// Determines whether a blob already exists in the cache.
    /// </summary>
    /// <param name="hash">The content hash identifying the blob.</param>
    /// <returns><c>true</c> when the blob exists, otherwise <c>false</c>.</returns>
    bool Has(string hash);

    /// <summary>
    /// Stores the supplied blob into the cache.
    /// </summary>
    /// <param name="hash">The content hash identifying the blob.</param>
    /// <param name="source">A stream containing the blob data.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task StoreAsync(string hash, Stream source, CancellationToken cancellationToken = default);

    /// <summary>
    /// Opens a readable stream to the blob.
    /// </summary>
    /// <param name="hash">The content hash identifying the blob.</param>
    /// <returns>An open stream positioned at the beginning of the blob.</returns>
    Stream OpenRead(string hash);

    /// <summary>
    /// Calculates the total size of all cached blobs.
    /// </summary>
    /// <returns>The total number of bytes in the cache.</returns>
    long TotalSizeBytes();

    /// <summary>
    /// Trims the cache until the size is less than or equal to the provided limit.
    /// </summary>
    /// <param name="maximumBytes">The maximum number of bytes the cache should occupy.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task TrimTo(long maximumBytes, CancellationToken cancellationToken = default);
}

/// <summary>
/// File system based implementation of <see cref="IBlobStore"/>.
/// </summary>
public sealed class FileBlobStore : IBlobStore
{
    private const string MetadataExtension = ".meta.json";
    private const int MaxIoAttempts = 5;
    private static readonly TimeSpan RetryDelay = TimeSpan.FromMilliseconds(75);
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly string _cacheRoot;
    private readonly string _tempRoot;

    /// <summary>
    /// Initialises a new <see cref="FileBlobStore"/> rooted in the plugin configuration directory.
    /// </summary>
    public FileBlobStore()
        : this(GetDefaultCacheRoot())
    {
    }

    /// <summary>
    /// Initialises a new <see cref="FileBlobStore"/> rooted at the supplied path.
    /// </summary>
    /// <param name="cacheRoot">The root directory where blobs will be stored.</param>
    internal FileBlobStore(string cacheRoot)
    {
        if (string.IsNullOrWhiteSpace(cacheRoot))
        {
            throw new ArgumentException("Cache root must be provided", nameof(cacheRoot));
        }

        _cacheRoot = cacheRoot;
        _tempRoot = Path.Combine(_cacheRoot, "tmp");

        Directory.CreateDirectory(_cacheRoot);
        Directory.CreateDirectory(_tempRoot);
    }

    /// <inheritdoc />
    public bool Has(string hash)
    {
        var (blobPath, metaPath) = GetBlobPaths(hash);
        if (!File.Exists(blobPath))
        {
            return false;
        }

        UpdateLastAccess(metaPath, blobPath, ensureExists: true);
        return true;
    }

    /// <inheritdoc />
    public Stream OpenRead(string hash)
    {
        var (blobPath, metaPath) = GetBlobPaths(hash);
        if (!File.Exists(blobPath))
        {
            throw new FileNotFoundException("Blob not found", blobPath);
        }

        UpdateLastAccess(metaPath, blobPath, ensureExists: true);

        return OpenFileStreamWithRetry(blobPath, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete, FileOptions.Asynchronous | FileOptions.SequentialScan);
    }

    /// <inheritdoc />
    public async Task StoreAsync(string hash, Stream source, CancellationToken cancellationToken = default)
    {
        if (source is null)
        {
            throw new ArgumentNullException(nameof(source));
        }

        var (blobPath, metaPath) = GetBlobPaths(hash);
        var directory = Path.GetDirectoryName(blobPath)!;
        Directory.CreateDirectory(directory);

        var tempFile = Path.Combine(_tempRoot, Guid.NewGuid().ToString("N") + ".tmp");
        Directory.CreateDirectory(_tempRoot);

        await using (var tempStream = OpenFileStreamWithRetry(tempFile, FileMode.Create, FileAccess.Write, FileShare.None, FileOptions.Asynchronous | FileOptions.SequentialScan))
        {
            await source.CopyToAsync(tempStream, cancellationToken).ConfigureAwait(false);
            await tempStream.FlushAsync(cancellationToken).ConfigureAwait(false);
        }

        try
        {
            RetryIo(() => File.Move(tempFile, blobPath, true));
        }
        catch
        {
            TryDelete(tempFile);
            throw;
        }

        var size = RetryIo(() => new FileInfo(blobPath).Length);
        WriteMetadata(metaPath, new BlobMetadata(size, DateTimeOffset.UtcNow));
    }

    /// <inheritdoc />
    public long TotalSizeBytes()
        => EnumerateMetadata().Sum(static entry => entry.Size);

    /// <inheritdoc />
    public Task TrimTo(long maximumBytes, CancellationToken cancellationToken = default)
    {
        if (maximumBytes < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumBytes));
        }

        var entries = EnumerateMetadata()
            .OrderBy(static entry => entry.LastAccessUtc)
            .ToList();

        long total = entries.Sum(static entry => entry.Size);
        foreach (var entry in entries)
        {
            if (total <= maximumBytes)
            {
                break;
            }

            cancellationToken.ThrowIfCancellationRequested();

            if (DeleteBlob(entry))
            {
                total -= entry.Size;
            }
        }

        return Task.CompletedTask;
    }

    private static string GetDefaultCacheRoot()
    {
        var configDirectory = PluginServices.Instance?.PluginInterface.GetPluginConfigDirectory();
        if (string.IsNullOrEmpty(configDirectory))
        {
            throw new InvalidOperationException("Plugin configuration directory is unavailable.");
        }

        var cacheRoot = Path.Combine(configDirectory, ".syncshell", "cache");
        Directory.CreateDirectory(cacheRoot);
        return cacheRoot;
    }

    private (string BlobPath, string MetadataPath) GetBlobPaths(string hash)
    {
        if (string.IsNullOrWhiteSpace(hash))
        {
            throw new ArgumentException("Hash must be provided", nameof(hash));
        }

        var safeHash = hash.Trim();
        var shard = safeHash.Length >= 2 ? safeHash[..2] : "00";
        var directory = Path.Combine(_cacheRoot, shard);
        var blobPath = Path.Combine(directory, safeHash);
        var metaPath = blobPath + MetadataExtension;
        return (blobPath, metaPath);
    }

    private static void RetryIo(Action action)
    {
        _ = RetryIo(() =>
        {
            action();
            return true;
        });
    }

    private static T RetryIo<T>(Func<T> action)
    {
        var attempt = 0;
        while (true)
        {
            try
            {
                return action();
            }
            catch (IOException) when (++attempt < MaxIoAttempts)
            {
                Thread.Sleep(RetryDelay);
            }
            catch (UnauthorizedAccessException) when (++attempt < MaxIoAttempts)
            {
                Thread.Sleep(RetryDelay);
            }
        }
    }

    private static FileStream OpenFileStreamWithRetry(string path, FileMode mode, FileAccess access, FileShare share, FileOptions options)
    {
        return RetryIo(() => new FileStream(path, mode, access, share, 4096, options));
    }

    private void UpdateLastAccess(string metadataPath, string blobPath, bool ensureExists)
    {
        BlobMetadata metadata;
        if (File.Exists(metadataPath))
        {
            metadata = ReadMetadata(metadataPath);
            metadata = metadata with { LastAccessUtc = DateTimeOffset.UtcNow };
        }
        else if (ensureExists)
        {
            var size = RetryIo(() => new FileInfo(blobPath).Length);
            metadata = new BlobMetadata(size, DateTimeOffset.UtcNow);
        }
        else
        {
            return;
        }

        WriteMetadata(metadataPath, metadata);
    }

    private void WriteMetadata(string metadataPath, BlobMetadata metadata)
    {
        var directory = Path.GetDirectoryName(metadataPath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var json = JsonSerializer.Serialize(metadata, JsonOptions);
        RetryIo(() => File.WriteAllText(metadataPath, json));
    }

    private BlobMetadata ReadMetadata(string metadataPath)
    {
        try
        {
            var json = RetryIo(() => File.ReadAllText(metadataPath));
            var metadata = JsonSerializer.Deserialize<BlobMetadata>(json, JsonOptions);
            if (metadata != null)
            {
                return metadata;
            }
        }
        catch (Exception) when (File.Exists(metadataPath))
        {
            // Fall back to file info below.
        }

        var blobPath = metadataPath[..^MetadataExtension.Length];
        var size = File.Exists(blobPath) ? RetryIo(() => new FileInfo(blobPath).Length) : 0;
        return new BlobMetadata(size, DateTimeOffset.MinValue);
    }

    private IEnumerable<BlobEntry> EnumerateMetadata()
    {
        if (!Directory.Exists(_cacheRoot))
        {
            yield break;
        }

        foreach (var metadataPath in Directory.EnumerateFiles(_cacheRoot, "*" + MetadataExtension, SearchOption.AllDirectories))
        {
            BlobMetadata metadata;
            try
            {
                metadata = ReadMetadata(metadataPath);
            }
            catch
            {
                continue;
            }

            var blobPath = metadataPath[..^MetadataExtension.Length];
            if (!File.Exists(blobPath))
            {
                continue;
            }

            yield return new BlobEntry(blobPath, metadataPath, metadata.Size, metadata.LastAccessUtc);
        }
    }

    private bool DeleteBlob(BlobEntry entry)
    {
        try
        {
            RetryIo(() => File.Delete(entry.BlobPath));
        }
        catch (FileNotFoundException)
        {
        }
        catch (DirectoryNotFoundException)
        {
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }

        try
        {
            RetryIo(() => File.Delete(entry.MetadataPath));
        }
        catch
        {
            // Metadata delete failure is non-fatal.
        }

        return true;
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch
        {
            // ignored
        }
    }

    private sealed record BlobMetadata(long Size, DateTimeOffset LastAccessUtc);

    private sealed record BlobEntry(string BlobPath, string MetadataPath, long Size, DateTimeOffset LastAccessUtc);
}
