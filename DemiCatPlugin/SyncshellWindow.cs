using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
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
    private static readonly Vector4 ActiveMemberColor = new(0.45f, 0.86f, 0.55f, 1f);

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
        DrawModeControls(service.Members);

        ImGui.Separator();
        DrawActiveMembers(service.ActiveMembers);

        ImGui.Separator();
        DrawMemberRoster(service.Members);

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

    private void DrawModeControls(IReadOnlyList<SyncshellMemberStatus> members)
    {
        var autoMode = _config.SyncAutoMode;
        if (ImGui.RadioButton("Auto mode (sync all linked members)", autoMode))
        {
            if (!_config.SyncAutoMode)
            {
                _config.SyncAutoMode = true;
                SaveConfig();
            }
        }
        ImGui.SameLine();
        if (ImGui.RadioButton("Manual mode (use allow list)", !autoMode))
        {
            if (_config.SyncAutoMode)
            {
                _config.SyncAutoMode = false;
                SaveConfig();
            }
        }

        if (autoMode)
        {
            ImGui.TextDisabled("Allow list is ignored while auto mode is enabled.");
        }
        else
        {
            ImGui.TextDisabled("Only allow-listed Discord IDs will be synced.");
        }

        ImGui.Spacing();
        ImGui.TextUnformatted("Manual allow list");
        ImGui.Indent();
        if (_config.ManualAutoList.Count == 0)
        {
            ImGui.TextDisabled("No Discord IDs selected.");
        }
        else
        {
            foreach (var id in _config.ManualAutoList.OrderBy(id => id))
            {
                var idString = id.ToString(CultureInfo.InvariantCulture);
                var name = ResolveDisplayName(members, idString);
                var label = string.IsNullOrEmpty(name) ? idString : $"{name} ({idString})";
                ImGui.BulletText(label);
            }
        }
        ImGui.Unindent();

        ImGui.Spacing();
        var onlyVisible = _config.OnlySyncVisible;
        if (ImGui.Checkbox("Only apply to visible players##syncshell-only-visible", ref onlyVisible))
        {
            _config.OnlySyncVisible = onlyVisible;
            SaveConfig();
        }

        var backgroundPrefetch = _config.BackgroundPrefetch;
        if (ImGui.Checkbox("Background prefetch (download in advance)##syncshell-bg-prefetch", ref backgroundPrefetch))
        {
            _config.BackgroundPrefetch = backgroundPrefetch;
            SaveConfig();
        }
        ImGui.TextDisabled("Prefetch downloads assets even when players are not currently visible.");
    }

    private void DrawActiveMembers(IReadOnlyList<SyncshellMemberStatus> activeMembers)
    {
        ImGui.TextUnformatted("Currently syncing");
        ImGui.Indent();
        if (activeMembers.Count == 0)
        {
            ImGui.TextDisabled("No active members detected.");
        }
        else
        {
            foreach (var member in activeMembers)
            {
                DrawMemberLine(member, highlight: true, manualMode: false);
                if (member.SyncedAt != null)
                {
                    ImGui.Indent();
                    ImGui.TextDisabled($"Synced at {FormatTimestamp(member.SyncedAt)}");
                    ImGui.Unindent();
                }
            }
        }
        ImGui.Unindent();
    }

    private void DrawMemberRoster(IReadOnlyList<SyncshellMemberStatus> members)
    {
        ImGui.TextUnformatted("Member roster");
        ImGui.Indent();
        if (members.Count == 0)
        {
            ImGui.TextDisabled("No linked members yet.");
        }
        else
        {
            var manualMode = !_config.SyncAutoMode;
            foreach (var member in members.OrderBy(m => m.DisplayName, StringComparer.OrdinalIgnoreCase))
            {
                DrawMemberLine(member, highlight: member.IsActive, manualMode);
                if (member.LastSeen != null)
                {
                    ImGui.Indent();
                    ImGui.TextDisabled($"Last seen {FormatTimestamp(member.LastSeen)}");
                    ImGui.Unindent();
                }
            }
        }
        ImGui.Unindent();
    }

    private void DrawMemberLine(SyncshellMemberStatus member, bool highlight, bool manualMode)
    {
        var presenceLabel = string.IsNullOrEmpty(member.SyncStatus)
            ? member.Presence
            : $"{member.Presence}, {member.SyncStatus}";
        var linkedSuffix = member.TokenLinked ? " • linked" : string.Empty;
        var label = $"{member.DisplayName} [{presenceLabel}]{linkedSuffix}";

        ImGui.Bullet();
        ImGui.SameLine();

        if (manualMode)
        {
            var allowListed = IsAllowListed(member.Id);
            var buttonLabel = allowListed ? "-" : "+";
            if (ImGui.SmallButton($"{buttonLabel}##allow-{member.Id}"))
            {
                ToggleAllowList(member.Id, !allowListed);
            }
            if (ImGui.IsItemHovered())
            {
                ImGui.SetTooltip(allowListed ? "Remove from allow list" : "Add to allow list");
            }
            ImGui.SameLine();
        }

        if (highlight)
        {
            ImGui.PushStyleColor(ImGuiCol.Text, ActiveMemberColor);
        }

        ImGui.TextUnformatted(label);

        if (highlight)
        {
            ImGui.PopStyleColor();
        }

        DrawStageBadge(member.Id);
    }

    private static string FormatTimestamp(DateTimeOffset? timestamp)
    {
        if (timestamp == null)
        {
            return string.Empty;
        }

        var local = timestamp.Value.ToLocalTime();
        return local.ToString("g", CultureInfo.CurrentCulture);
    }

    public void ClearCaches()
    {
        _service?.ClearCache();
    }

    private bool IsAllowListed(string id)
    {
        if (!ulong.TryParse(id, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
        {
            return false;
        }

        return _config.ManualAutoList.Contains(parsed);
    }

    private void ToggleAllowList(string id, bool allow)
    {
        if (!ulong.TryParse(id, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
        {
            return;
        }

        var changed = allow
            ? _config.ManualAutoList.Add(parsed)
            : _config.ManualAutoList.Remove(parsed);

        if (changed)
        {
            SaveConfig();
        }
    }

    private void DrawStageBadge(string memberId)
    {
        if (_service == null)
        {
            return;
        }

        var stage = _service.GetStage(memberId);
        if (stage == SyncshellTargetStage.Unknown)
        {
            return;
        }

        var label = GetStageLabel(stage);
        if (string.IsNullOrEmpty(label))
        {
            return;
        }

        var color = GetStageColor(stage);
        ImGui.SameLine();
        ImGui.PushStyleColor(ImGuiCol.Text, color);
        ImGui.TextUnformatted($"[{label}]");
        ImGui.PopStyleColor();
    }

    private static string GetStageLabel(SyncshellTargetStage stage)
        => stage switch
        {
            SyncshellTargetStage.Queued => "queued",
            SyncshellTargetStage.Prefetching => "prefetching",
            SyncshellTargetStage.Prefetched => "prefetched",
            SyncshellTargetStage.Applying => "applying",
            SyncshellTargetStage.Applied => "applied",
            SyncshellTargetStage.Failed => "failed",
            _ => string.Empty,
        };

    private static Vector4 GetStageColor(SyncshellTargetStage stage)
        => stage switch
        {
            SyncshellTargetStage.Queued => new Vector4(0.65f, 0.65f, 0.68f, 1f),
            SyncshellTargetStage.Prefetching => new Vector4(0.9f, 0.78f, 0.32f, 1f),
            SyncshellTargetStage.Prefetched => new Vector4(0.45f, 0.73f, 1f, 1f),
            SyncshellTargetStage.Applying => new Vector4(0.95f, 0.65f, 0.35f, 1f),
            SyncshellTargetStage.Applied => ActiveMemberColor,
            SyncshellTargetStage.Failed => new Vector4(0.9f, 0.35f, 0.3f, 1f),
            _ => new Vector4(0.7f, 0.7f, 0.7f, 1f),
        };

    private static string ResolveDisplayName(IReadOnlyList<SyncshellMemberStatus> members, string id)
    {
        if (members == null)
        {
            return string.Empty;
        }

        foreach (var member in members)
        {
            if (string.Equals(member.Id, id, StringComparison.OrdinalIgnoreCase))
            {
                return member.DisplayName;
            }
        }

        return string.Empty;
    }

    private void SaveConfig()
    {
        var pluginInterface = PluginServices.Instance?.PluginInterface;
        pluginInterface?.SavePluginConfig(_config);
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
