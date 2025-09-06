using System;
using System.IO;
using System.Text;
using System.Security.Cryptography;
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
    public event Action? OnUnlinked;

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
            var bytes = ProtectedData.Unprotect(encrypted, null, DataProtectionScope.CurrentUser);
            _token = Encoding.UTF8.GetString(bytes);
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
            var encrypted = ProtectedData.Protect(bytes, null, DataProtectionScope.CurrentUser);
            File.WriteAllBytes(path, encrypted);
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

    public void Clear()
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
        OnUnlinked?.Invoke();
    }

    public void RegisterWatcher(Action start, Action stop)
    {
        OnLinked += start;
        OnUnlinked += stop;
        if (IsReady())
        {
            start();
        }
    }
}

