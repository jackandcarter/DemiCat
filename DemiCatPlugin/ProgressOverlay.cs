using System;
using System.Collections.Generic;
using System.Globalization;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility;

namespace DemiCatPlugin;

/// <summary>
/// Renders an overlay showing Syncshell transfer progress.
/// </summary>
public sealed class ProgressOverlay
{
    private sealed class Entry
    {
        public required string PeerId;
        public int Downloaded;
        public int Total;
        public DateTime LastUpdate;
        public DateTime? CompletedAt;
        public float FadeAlpha = 1f;
    }

    private readonly Dictionary<string, Entry> _entries = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<EntrySnapshot> _drawBuffer = new();
    private readonly List<string> _removeBuffer = new();
    private readonly object _lock = new();

    private static readonly TimeSpan CompletionLifetime = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan FadeDuration = TimeSpan.FromSeconds(1);

    /// <summary>
    /// Gets or sets a value indicating whether the overlay should be drawn.
    /// </summary>
    public bool IsVisible { get; set; } = true;

    /// <summary>
    /// Updates or inserts a progress entry for the specified peer.
    /// </summary>
    public void Update(string peerId, int downloaded, int total)
    {
        if (string.IsNullOrWhiteSpace(peerId))
        {
            return;
        }

        var now = DateTime.UtcNow;
        lock (_lock)
        {
            if (!_entries.TryGetValue(peerId, out var entry))
            {
                entry = new Entry
                {
                    PeerId = peerId,
                    LastUpdate = now,
                };
                _entries[peerId] = entry;
            }

            entry.Downloaded = Math.Max(downloaded, 0);
            entry.Total = Math.Max(total, 0);
            entry.LastUpdate = now;
            entry.FadeAlpha = 1f;

            if (entry.Total > 0 && entry.Downloaded >= entry.Total)
            {
                entry.CompletedAt ??= now;
            }
            else
            {
                entry.CompletedAt = null;
            }

            PurgeExpiredUnsafe(now);
        }
    }

    /// <summary>
    /// Draws the overlay if it is enabled and contains active entries.
    /// </summary>
    public void Draw()
    {
        if (!IsVisible)
        {
            return;
        }

        var now = DateTime.UtcNow;

        lock (_lock)
        {
            PurgeExpiredUnsafe(now);
            _drawBuffer.Clear();
            foreach (var entry in _entries.Values)
            {
                UpdateFade(entry, now);
                _drawBuffer.Add(new EntrySnapshot(
                    entry.PeerId,
                    entry.Downloaded,
                    entry.Total,
                    entry.FadeAlpha,
                    entry.CompletedAt.HasValue,
                    entry.LastUpdate));
            }
        }

        if (_drawBuffer.Count == 0)
        {
            return;
        }

        _drawBuffer.Sort(EntryComparison);

        var viewport = ImGuiHelpers.MainViewport;
        var scale = ImGuiHelpers.GlobalScale;
        var margin = new Vector2(16f * scale);
        var position = new Vector2(
            viewport.WorkPos.X + viewport.WorkSize.X - margin.X,
            viewport.WorkPos.Y + margin.Y);

        ImGui.SetNextWindowPos(position, ImGuiCond.Always, new Vector2(1f, 0f));
        ImGui.SetNextWindowBgAlpha(0.92f);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new Vector2(12f, 10f) * scale);

        const string windowId = "##dc_syncshell_progress";
        var flags = ImGuiWindowFlags.NoDecoration |
                    ImGuiWindowFlags.AlwaysAutoResize |
                    ImGuiWindowFlags.NoSavedSettings |
                    ImGuiWindowFlags.NoFocusOnAppearing |
                    ImGuiWindowFlags.NoNav |
                    ImGuiWindowFlags.NoMove |
                    ImGuiWindowFlags.NoInputs;

        if (ImGui.Begin(windowId, flags))
        {
            ImGui.TextUnformatted("Sync Progress");
            ImGui.Separator();

            var barWidth = 240f * scale;

            for (var i = 0; i < _drawBuffer.Count; i++)
            {
                var snapshot = _drawBuffer[i];
                if (snapshot.Alpha <= 0f)
                {
                    continue;
                }

                ImGui.PushStyleVar(ImGuiStyleVar.Alpha, snapshot.Alpha);
                ImGui.PushID(snapshot.PeerId);

                var peerLabel = FormatPeerId(snapshot.PeerId);
                ImGui.TextUnformatted(peerLabel);
                if (!string.Equals(peerLabel, snapshot.PeerId, StringComparison.Ordinal))
                {
                    if (ImGui.IsItemHovered())
                    {
                        ImGui.SetTooltip(snapshot.PeerId);
                    }
                }

                var progress = snapshot.Total > 0
                    ? Math.Clamp((float)snapshot.Downloaded / snapshot.Total, 0f, 1f)
                    : 0f;
                var progressLabel = snapshot.Total > 0
                    ? $"{FormatBytes(snapshot.Downloaded)} / {FormatBytes(snapshot.Total)}"
                    : $"{FormatBytes(snapshot.Downloaded)}";

                ImGui.ProgressBar(progress, new Vector2(barWidth, 0f), progressLabel);

                ImGui.PopID();
                ImGui.PopStyleVar();

                if (i < _drawBuffer.Count - 1)
                {
                    ImGui.Dummy(new Vector2(0f, 4f * scale));
                }
            }
        }

        ImGui.End();
        ImGui.PopStyleVar();
    }

    private void PurgeExpiredUnsafe(DateTime now)
    {
        _removeBuffer.Clear();

        foreach (var (peerId, entry) in _entries)
        {
            if (entry.CompletedAt is { } completed && now - completed >= CompletionLifetime)
            {
                _removeBuffer.Add(peerId);
            }
        }

        if (_removeBuffer.Count == 0)
        {
            return;
        }

        foreach (var peerId in _removeBuffer)
        {
            _entries.Remove(peerId);
        }

        _removeBuffer.Clear();
    }

    private static void UpdateFade(Entry entry, DateTime now)
    {
        if (entry.CompletedAt is not { } completed)
        {
            entry.FadeAlpha = 1f;
            return;
        }

        var elapsed = now - completed;
        if (elapsed <= TimeSpan.Zero)
        {
            entry.FadeAlpha = 1f;
            return;
        }

        if (elapsed >= CompletionLifetime)
        {
            entry.FadeAlpha = 0f;
            return;
        }

        var fadeStart = CompletionLifetime - FadeDuration;
        if (fadeStart < TimeSpan.Zero)
        {
            fadeStart = TimeSpan.Zero;
        }

        if (elapsed <= fadeStart)
        {
            entry.FadeAlpha = 1f;
            return;
        }

        var duration = FadeDuration <= TimeSpan.Zero
            ? TimeSpan.FromMilliseconds(1)
            : FadeDuration;
        var fadeProgress = (float)((elapsed - fadeStart).TotalSeconds / duration.TotalSeconds);
        entry.FadeAlpha = Math.Clamp(1f - fadeProgress, 0f, 1f);
    }

    private static string FormatBytes(long value)
    {
        var size = Math.Max(0L, value);
        string suffix;
        double readable;

        if (size >= 1_000_000_000)
        {
            suffix = "GB";
            readable = size / 1_000_000_000d;
        }
        else if (size >= 1_000_000)
        {
            suffix = "MB";
            readable = size / 1_000_000d;
        }
        else if (size >= 1_000)
        {
            suffix = "KB";
            readable = size / 1_000d;
        }
        else
        {
            suffix = "B";
            readable = size;
        }

        return string.Format(CultureInfo.InvariantCulture, "{0:0.#} {1}", readable, suffix);
    }

    private static string FormatPeerId(string peerId)
    {
        if (string.IsNullOrEmpty(peerId))
        {
            return "Unknown Peer";
        }

        const int maxLength = 24;
        if (peerId.Length <= maxLength)
        {
            return peerId;
        }

        return peerId[..(maxLength - 1)] + "\u2026";
    }

    private static int EntryComparison(EntrySnapshot left, EntrySnapshot right)
    {
        var stateCompare = (left.Completed ? 1 : 0).CompareTo(right.Completed ? 1 : 0);
        if (stateCompare != 0)
        {
            return stateCompare;
        }

        return right.LastUpdate.CompareTo(left.LastUpdate);
    }

    private readonly struct EntrySnapshot
    {
        public EntrySnapshot(string peerId, int downloaded, int total, float alpha, bool completed, DateTime lastUpdate)
        {
            PeerId = peerId;
            Downloaded = downloaded;
            Total = total;
            Alpha = alpha;
            Completed = completed;
            LastUpdate = lastUpdate;
        }

        public string PeerId { get; }
        public int Downloaded { get; }
        public int Total { get; }
        public float Alpha { get; }
        public bool Completed { get; }
        public DateTime LastUpdate { get; }
    }
}
