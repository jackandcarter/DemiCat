using System;
using System.Collections.Generic;
using System.Numerics;
using System.Threading.Tasks;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility;
using Dalamud.Plugin.Services;
using DemiCatPlugin.SyncShell;

namespace DemiCatPlugin;

public sealed class SyncshellWindow : IDisposable
{
    public static SyncshellWindow? Instance { get; private set; }

    private readonly Config _config;
    private readonly ISyncShellService? _service;
    private readonly IPluginLog? _log;
    private readonly List<string> _statusHistory = new();
    private bool _disposed;
    private string _latestStatus = string.Empty;

    public SyncshellWindow(Config config, ISyncShellService? service = null)
    {
        _config = config ?? throw new ArgumentNullException(nameof(config));
        _service = service ?? PluginServices.Instance?.SyncShellService;
        _log = PluginServices.Instance?.Log;

        if (!_config.EnableSyncShell)
        {
            throw new InvalidOperationException("SyncShell disabled");
        }

        Instance?.Dispose();
        Instance = this;

        if (_service != null)
        {
            _latestStatus = _service.Status;
            _statusHistory.Add(_latestStatus);
            _service.StatusChanged += HandleStatusChanged;
        }
    }

    public void Draw()
    {
        var service = _service;
        if (service == null)
        {
            ImGui.TextUnformatted("SyncShell service is unavailable.");
            return;
        }

        ImGui.TextUnformatted($"Status: {_latestStatus}");
        ImGui.SameLine();
        ImGui.TextDisabled($"Nearby users: {service.NearbyUserCount}");
        ImGui.Spacing();

        ImGui.TextUnformatted("Status history");
        ImGui.Indent();
        foreach (var entry in _statusHistory)
        {
            ImGui.BulletText(entry);
        }
        if (_statusHistory.Count == 0)
        {
            ImGui.TextDisabled("No status changes recorded yet.");
        }
        ImGui.Unindent();

        ImGui.Separator();
        ImGui.TextUnformatted("Allowed Discord IDs");
        ImGui.Indent();
        if (_config.SyncshellAllowedDiscordIds.Count == 0)
        {
            ImGui.TextDisabled("No custom IDs configured.");
        }
        else
        {
            foreach (var id in _config.SyncshellAllowedDiscordIds)
            {
                ImGui.BulletText(id);
            }
        }
        ImGui.Unindent();

        ImGui.Separator();
        var buttonWidth = 150f * ImGuiHelpers.GlobalScale;
        if (ImGui.Button("Sync now", new Vector2(buttonWidth, 0)))
        {
            var syncService = service;
            _ = Task.Run(async () => await syncService.TriggerPublishAsync().ConfigureAwait(false));
        }
        ImGui.SameLine();
        if (ImGui.Button(service.IsPaused ? "Resume" : "Pause", new Vector2(buttonWidth, 0)))
        {
            if (service.IsPaused)
            {
                service.Resume();
            }
            else
            {
                service.Pause();
            }
        }
        ImGui.SameLine();
        if (ImGui.Button("Clear cache", new Vector2(buttonWidth, 0)))
        {
            try
            {
                service.ClearCache();
                _ = Task.Run(async () => await service.EnforceCacheLimitAsync().ConfigureAwait(false));
            }
            catch (Exception ex)
            {
                _log?.Warning(ex, "Failed to clear SyncShell cache");
            }
        }
    }

    public void ClearCaches()
    {
        _service?.ClearCache();
    }

    private void HandleStatusChanged(object? sender, EventArgs e)
    {
        if (_service == null)
        {
            return;
        }

        _latestStatus = _service.Status;
        if (_statusHistory.Count == 0 || !string.Equals(_statusHistory[0], _latestStatus, StringComparison.Ordinal))
        {
            _statusHistory.Insert(0, _latestStatus);
            if (_statusHistory.Count > 10)
            {
                _statusHistory.RemoveAt(_statusHistory.Count - 1);
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
        if (_service != null)
        {
            _service.StatusChanged -= HandleStatusChanged;
        }

        if (Instance == this)
        {
            Instance = null;
        }
    }
}
