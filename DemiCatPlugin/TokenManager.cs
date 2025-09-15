using System;
using System.IO;
using System.Text;
using System.Security.Cryptography;
using System.Runtime.InteropServices;
using System.Diagnostics;
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
    private const string StoreService = "DemiCat";
    private const string StoreAccount = "Token";
    private readonly IDalamudPluginInterface _pluginInterface;
    private string? _token;
    public LinkState State { get; private set; } = LinkState.Unlinked;

    public static TokenManager? Instance { get; private set; }

    public event Action? OnLinked;
    public event Action<string?>? OnUnlinked;

#if TEST
    internal TokenManager()
    {
        _pluginInterface = null!;
        _token = "test";
        State = LinkState.Linked;
        Instance = this;
    }
#endif

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
            string? token = null;

            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                token = LoadDpapiToken();
            }
            else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            {
                if (!TryReadKeychain(out token))
                {
                    token = LoadLegacyFile(TryWriteKeychain);
                }
            }
            else
            {
                if (!TryReadLibSecret(out token))
                {
                    token = LoadLegacyFile(TryWriteLibSecret);
                }
            }

            _token = token;
            State = string.IsNullOrEmpty(token) ? LinkState.Unlinked : LinkState.Linked;
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
            bool stored = false;

            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                stored = SaveDpapiToken(token);
            }
            else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            {
                stored = TryWriteKeychain(token);
            }
            else
            {
                stored = TryWriteLibSecret(token);
            }

            if (!stored)
            {
                SavePlainFile(token);
            }

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

    private string? LoadDpapiToken()
    {
        var path = Path.Combine(_pluginInterface.ConfigDirectory.FullName, TokenFileName);
        if (!File.Exists(path))
            return null;

        var encrypted = File.ReadAllBytes(path);
        try
        {
            var bytes = ProtectedData.Unprotect(encrypted, null, DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(bytes);
        }
        catch (CryptographicException)
        {
            // migration from plain text
            var text = Encoding.UTF8.GetString(encrypted);
            var token = string.IsNullOrEmpty(text) ? null : text;
            if (token != null)
            {
                SaveDpapiToken(token);
            }
            return token;
        }
    }

    private bool SaveDpapiToken(string token)
    {
        try
        {
            var path = Path.Combine(_pluginInterface.ConfigDirectory.FullName, TokenFileName);
            var bytes = Encoding.UTF8.GetBytes(token);
            var cipher = ProtectedData.Protect(bytes, null, DataProtectionScope.CurrentUser);
            File.WriteAllBytes(path, cipher);
            RestrictPermissions(path);
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private string? LoadLegacyFile(Func<string, bool> migrate)
    {
        var path = Path.Combine(_pluginInterface.ConfigDirectory.FullName, TokenFileName);
        if (!File.Exists(path))
            return null;

        var text = File.ReadAllText(path, Encoding.UTF8);
        if (string.IsNullOrEmpty(text))
            return null;

        if (migrate(text))
        {
            try { File.Delete(path); } catch { }
        }
        else
        {
            RestrictPermissions(path);
        }

        return text;
    }

    private void SavePlainFile(string token)
    {
        var path = Path.Combine(_pluginInterface.ConfigDirectory.FullName, TokenFileName);
        File.WriteAllText(path, token, Encoding.UTF8);
        RestrictPermissions(path);
    }

    private static bool TryReadKeychain(out string? token)
    {
        token = null;
        try
        {
            var psi = new ProcessStartInfo("security", $"-q find-generic-password -s {StoreService} -a {StoreAccount} -w")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            using var proc = Process.Start(psi);
            proc!.WaitForExit();
            if (proc.ExitCode == 0)
            {
                token = proc.StandardOutput.ReadToEnd().Trim();
                if (token.Length == 0) token = null;
                return token != null;
            }
        }
        catch { }
        return false;
    }

    private static bool TryWriteKeychain(string token)
    {
        try
        {
            var psi = new ProcessStartInfo("security", $"-q add-generic-password -s {StoreService} -a {StoreAccount} -w {token} -U")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            using var proc = Process.Start(psi);
            proc!.WaitForExit();
            return proc.ExitCode == 0;
        }
        catch { }
        return false;
    }

    private static void TryDeleteKeychain()
    {
        try
        {
            var psi = new ProcessStartInfo("security", $"-q delete-generic-password -s {StoreService} -a {StoreAccount}")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            using var proc = Process.Start(psi);
            proc?.WaitForExit();
        }
        catch { }
    }

    private static bool TryReadLibSecret(out string? token)
    {
        token = null;
        try
        {
            var psi = new ProcessStartInfo("secret-tool", $"lookup service {StoreService} account {StoreAccount}")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            using var proc = Process.Start(psi);
            proc!.WaitForExit();
            if (proc.ExitCode == 0)
            {
                token = proc.StandardOutput.ReadToEnd().Trim();
                if (token.Length == 0) token = null;
                return token != null;
            }
        }
        catch { }
        return false;
    }

    private static bool TryWriteLibSecret(string token)
    {
        try
        {
            var psi = new ProcessStartInfo("secret-tool", $"store --label=\"DemiCat Token\" service {StoreService} account {StoreAccount}")
            {
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            using var proc = Process.Start(psi);
            if (proc == null) return false;
            proc.StandardInput.Write(token);
            proc.StandardInput.Close();
            proc.WaitForExit();
            return proc.ExitCode == 0;
        }
        catch { }
        return false;
    }

    private static void TryDeleteLibSecret()
    {
        try
        {
            var psi = new ProcessStartInfo("secret-tool", $"clear service {StoreService} account {StoreAccount}")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            using var proc = Process.Start(psi);
            proc?.WaitForExit();
        }
        catch { }
    }

    public void Clear(string? reason = null)
    {
        try
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                var path = Path.Combine(_pluginInterface.ConfigDirectory.FullName, TokenFileName);
                if (File.Exists(path))
                    File.Delete(path);
            }
            else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            {
                TryDeleteKeychain();
            }
            else
            {
                TryDeleteLibSecret();
                var path = Path.Combine(_pluginInterface.ConfigDirectory.FullName, TokenFileName);
                if (File.Exists(path))
                    File.Delete(path);
            }
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

