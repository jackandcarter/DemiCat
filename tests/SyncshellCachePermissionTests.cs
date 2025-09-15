using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.Serialization;
using DemiCatPlugin;
using Dalamud.Plugin.Services;
using Xunit;

public class SyncshellCachePermissionTests
{
    private class TestLog : IPluginLog
    {
        public List<string> Errors { get; } = new();
        public void Verbose(string message) { }
        public void Verbose(string message, Exception exception) { }
        public void Debug(string message) { }
        public void Debug(string message, Exception exception) { }
        public void Info(string message) { }
        public void Info(string message, Exception exception) { }
        public void Warning(string message) { }
        public void Warning(string message, Exception exception) { }
        public void Error(string message) => Errors.Add(message);
        public void Error(Exception exception, string message) => Errors.Add(message);
        public void Fatal(string message) { }
        public void Fatal(string message, Exception exception) { }
    }

    [Fact]
    public void ClearCaches_ReadOnlyFile_LogsErrorAndContinues()
    {
        var ps = new PluginServices();
        var log = new TestLog();
        typeof(PluginServices).GetProperty("Log", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(ps, log);

        var window = (SyncshellWindow)FormatterServices.GetUninitializedObject(typeof(SyncshellWindow));
        var assetType = typeof(SyncshellWindow).GetNestedType("Asset", BindingFlags.NonPublic)!;
        var installType = typeof(SyncshellWindow).GetNestedType("Installation", BindingFlags.NonPublic)!;
        var assets = Activator.CreateInstance(typeof(List<>).MakeGenericType(assetType))!;
        var installs = Activator.CreateInstance(typeof(Dictionary<,>).MakeGenericType(typeof(string), installType))!;
        typeof(SyncshellWindow).GetField("_assets", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(window, assets);
        typeof(SyncshellWindow).GetField("_installations", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(window, installs);
        typeof(SyncshellWindow).GetField("_updatesAvailable", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(window, new HashSet<string>());
        typeof(SyncshellWindow).GetField("_seenAssetIds", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(window, new HashSet<string>());
        typeof(SyncshellWindow).GetField("_assetsFile", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(window, "/proc/1/status");
        typeof(SyncshellWindow).GetField("_installedFile", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(window, "/proc/1/status");
        typeof(SyncshellWindow).GetField("_needsRefresh", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(window, false);

        window.ClearCaches();

        Assert.Contains(log.Errors, m => m.Contains("Failed to clear caches"));
        var needsRefresh = (bool)typeof(SyncshellWindow).GetField("_needsRefresh", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!;
        Assert.True(needsRefresh);
    }
}
