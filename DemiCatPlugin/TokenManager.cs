using System;
using System.IO;
using System.Text;
using System.Security.Cryptography;
using System.Runtime.InteropServices;
using Dalamud.Plugin;

namespace DemiCatPlugin;

public enum LinkState
{
    Unlinked,
    Linking,
    Linked
}

public class TokenManager
{
    private const string TokenFileName = "token.dat";
    private readonly IDalamudPluginInterface _pluginInterface;
    private string? _token;
    public LinkState State { get; private set; } = LinkState.Unlinked;

    public static TokenManager? Instance { get; private set; }

    public event Action? OnLinked;
    public event Action<string?>? OnUnlinked;

    public TokenManager()
    {
        _pluginInterface = null!;
        _token = "test";
        State = LinkState.Linked;
        Instance = this;
    }

    public TokenManager(IDalamudPluginInterface pluginInterface)
    {
        _pluginInterface = pluginInterface;
        Instance = this;
        Load();
    }

    public bool IsReady() => State == LinkState.Linked;

    public string? Token => _token;

    public void Load()
    {
        try
        {
            var path = Path.Combine(_pluginInterface.ConfigDirectory.FullName, TokenFileName);
            if (!File.Exists(path))
            {
                State = LinkState.Unlinked;
                _token = null;
                return;
            }

            var encrypted = File.ReadAllBytes(path);
            try
            {
                var bytes = ProtectedData.Unprotect(encrypted, null, DataProtectionScope.CurrentUser);
                _token = Encoding.UTF8.GetString(bytes);
            }
            catch (CryptographicException)
            {
                // Migration path for previously stored plain text tokens
                var text = Encoding.UTF8.GetString(encrypted);
                _token = string.IsNullOrEmpty(text) ? null : text;
                if (_token != null)
                {
                    try
                    {
                        var bytes = Encoding.UTF8.GetBytes(_token);
                        var cipher = ProtectedData.Protect(bytes, null, DataProtectionScope.CurrentUser);
                        File.WriteAllBytes(path, cipher);
                        RestrictPermissions(path);
                    }
                    catch (PlatformNotSupportedException)
                    {
                        File.WriteAllBytes(path, Encoding.UTF8.GetBytes(_token));
                        RestrictPermissions(path);
                    }
                }
            }

            State = string.IsNullOrEmpty(_token) ? LinkState.Unlinked : LinkState.Linked;
        }
        catch
        {
            _token = null;
            State = LinkState.Unlinked;
        }
    }

    public void Set(string token)
    {
        try
        {
            var path = Path.Combine(_pluginInterface.ConfigDirectory.FullName, TokenFileName);
            var bytes = Encoding.UTF8.GetBytes(token);
            byte[] encrypted;
            try
            {
                encrypted = ProtectedData.Protect(bytes, null, DataProtectionScope.CurrentUser);
            }
            catch (PlatformNotSupportedException)
            {
                // fallback to plain text but restrict permissions
                encrypted = bytes;
            }

            File.WriteAllBytes(path, encrypted);
            RestrictPermissions(path);
            _token = token;
            State = LinkState.Linked;
            OnLinked?.Invoke();
        }
        catch
        {
            _token = null;
            State = LinkState.Unlinked;
        }
    }

    private static void RestrictPermissions(string path)
    {
        try
        {
            if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            }
        }
        catch
        {
            // ignore
        }
    }

    public void Clear(string? reason = null)
    {
        try
        {
            var path = Path.Combine(_pluginInterface.ConfigDirectory.FullName, TokenFileName);
            if (File.Exists(path))
                File.Delete(path);
        }
        catch
        {
            // ignore
        }
        _token = null;
        State = LinkState.Unlinked;
        OnUnlinked?.Invoke(reason);
    }

    public void RegisterWatcher(Action start, Action stop)
    {
        OnLinked += start;
        OnUnlinked += _ => stop();
        if (IsReady())
        {
            start();
        }
    }
}

