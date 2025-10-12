using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace DemiCatPlugin.SyncShell;

public static class Hasher
{
    public static string Sha256File(string fullPath)
    {
        if (string.IsNullOrEmpty(fullPath))
        {
            throw new ArgumentException("Path must be provided", nameof(fullPath));
        }

        using var stream = File.OpenRead(fullPath);
        return Sha256Stream(stream);
    }

    public static string Sha256Bytes(byte[] data)
    {
        if (data == null)
        {
            throw new ArgumentNullException(nameof(data));
        }

        using var sha = SHA256.Create();
        var hash = sha.ComputeHash(data);
        return ConvertToHex(hash);
    }

    private static string Sha256Stream(Stream stream)
    {
        using var sha = SHA256.Create();
        var hash = sha.ComputeHash(stream);
        return ConvertToHex(hash);
    }

    private static string ConvertToHex(byte[] bytes)
    {
        var builder = new StringBuilder(bytes.Length * 2);
        foreach (var value in bytes)
        {
            builder.Append(value.ToString("x2"));
        }

        return builder.ToString();
    }
}
