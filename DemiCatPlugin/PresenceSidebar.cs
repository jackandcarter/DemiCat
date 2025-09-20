using System;
using System.Linq;
using System.Collections.Generic;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Textures;
using System.Numerics;

namespace DemiCatPlugin;

/// <summary>
/// Simple UI component that renders the list of user presences provided by
/// <see cref="DiscordPresenceService"/>. All networking and data management is
/// delegated to the service so this class only concerns itself with drawing.
/// </summary>
public class PresenceSidebar : IDisposable
{
    private readonly DiscordPresenceService _service;
    private static readonly Vector4 OnlineColor = new(0.2f, 0.8f, 0.2f, 1f);
    private static readonly Vector4 IdleColor = new(0.95f, 0.75f, 0.2f, 1f);
    private static readonly Vector4 DndColor = new(0.9f, 0.3f, 0.3f, 1f);
    private static readonly Vector4 OfflineColor = new(0.5f, 0.5f, 0.5f, 1f);
    private static readonly Vector4 StatusTextColor = new(0.75f, 0.75f, 0.75f, 1f);

    public Action<string?, Action<ISharedImmediateTexture?>>? TextureLoader { get; set; }

    public PresenceSidebar(DiscordPresenceService service)
    {
        _service = service;
    }

    public void Draw(ref float width)
    {
        // Main presence list
        ImGui.BeginChild("##presence", new Vector2(width, 0), true);

        if (!_service.Loaded)
        {
            _ = _service.Refresh();
        }

        if (!string.IsNullOrEmpty(_service.StatusMessage))
        {
            ImGui.TextUnformatted(_service.StatusMessage);
            ImGui.Spacing();
        }

        var presences = _service.Presences;
        var online = presences
            .Where(p => !string.Equals(p.Status, "offline", StringComparison.OrdinalIgnoreCase))
            .ToList();
        var offline = presences
            .Where(p => string.Equals(p.Status, "offline", StringComparison.OrdinalIgnoreCase))
            .OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var roleGroups = new Dictionary<string, RoleGroup>(StringComparer.Ordinal);
        var orderedRoleIds = new List<string>();
        foreach (var role in RoleCache.Roles)
        {
            if (!roleGroups.ContainsKey(role.Id))
            {
                roleGroups[role.Id] = new RoleGroup(role.Id, role.Name);
                orderedRoleIds.Add(role.Id);
            }
        }

        foreach (var presence in presences)
        {
            foreach (var detail in presence.RoleDetails)
            {
                if (string.IsNullOrEmpty(detail.Id))
                    continue;
                if (!roleGroups.TryGetValue(detail.Id, out var group))
                {
                    group = new RoleGroup(detail.Id, detail.Name);
                    roleGroups[detail.Id] = group;
                }
                else if (string.IsNullOrEmpty(group.Name) && !string.IsNullOrEmpty(detail.Name))
                {
                    group.Name = detail.Name;
                }
            }
        }

        var noRole = new List<PresenceDto>();
        foreach (var presence in online)
        {
            var mapped = false;
            foreach (var roleId in presence.Roles)
            {
                if (string.IsNullOrEmpty(roleId))
                    continue;
                if (!roleGroups.TryGetValue(roleId, out var group))
                {
                    var display = presence.RoleDetails
                        .FirstOrDefault(r => string.Equals(r.Id, roleId, StringComparison.Ordinal))?.Name
                        ?? roleId;
                    group = new RoleGroup(roleId, display);
                    roleGroups[roleId] = group;
                }
                group.Members.Add(presence);
                mapped = true;
            }
            if (!mapped)
            {
                noRole.Add(presence);
            }
        }

        var anyOnline = false;
        var orderedSet = new HashSet<string>(orderedRoleIds, StringComparer.Ordinal);
        foreach (var roleId in orderedRoleIds)
        {
            if (!roleGroups.TryGetValue(roleId, out var group) || group.Members.Count == 0)
                continue;
            anyOnline = true;
            DrawRoleGroup(group);
        }

        var extraGroups = roleGroups.Values
            .Where(g => !orderedSet.Contains(g.Id) && g.Members.Count > 0)
            .OrderBy(g => g.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
        foreach (var group in extraGroups)
        {
            anyOnline = true;
            DrawRoleGroup(group);
        }

        if (noRole.Count > 0)
        {
            anyOnline = true;
            ImGui.TextUnformatted($"No Role - {noRole.Count}");
            foreach (var presence in noRole.OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase))
            {
                DrawPresence(presence);
            }
            ImGui.Spacing();
        }

        if (offline.Count > 0)
        {
            if (anyOnline)
            {
                ImGui.Spacing();
            }
            ImGui.TextUnformatted($"Offline - {offline.Count}");
            foreach (var presence in offline)
            {
                DrawPresence(presence);
            }
        }

        ImGui.EndChild();

        // Draw a draggable handle to allow the sidebar to be resized
        ImGui.SameLine();
        ImGui.InvisibleButton("##presence_resize", new Vector2(6, -1));
        if (ImGui.IsItemActive())
        {
            width += ImGui.GetIO().MouseDelta.X;
        }
        if (ImGui.IsItemHovered())
        {
            ImGui.SetMouseCursor(ImGuiMouseCursor.ResizeEW);
        }
        width = Math.Clamp(width, 140f, 600f);
    }

    private void DrawPresence(PresenceDto p)
    {
        ImGui.PushID(p.Id);
        var color = GetStatusColor(p.Status);
        ImGui.AlignTextToFramePadding();
        ImGui.PushStyleColor(ImGuiCol.Text, color);
        ImGui.TextUnformatted("●");
        ImGui.PopStyleColor();
        ImGui.SameLine(0f, 6f);

        if (TextureLoader != null && !string.IsNullOrEmpty(p.AvatarUrl) && p.AvatarTexture == null)
        {
            TextureLoader(p.AvatarUrl, t => p.AvatarTexture = t);
        }
        if (p.AvatarTexture != null)
        {
            var wrap = p.AvatarTexture.GetWrapOrEmpty();
            ImGui.Image(wrap.Handle, new Vector2(24, 24));
        }
        else
        {
            ImGui.Dummy(new Vector2(24, 24));
        }
        ImGui.SameLine(0f, 6f);
        ImGui.AlignTextToFramePadding();
        ImGui.TextUnformatted(p.Name);
        if (!string.IsNullOrWhiteSpace(p.StatusText))
        {
            ImGui.SameLine(0f, 4f);
            ImGui.PushStyleColor(ImGuiCol.Text, StatusTextColor);
            ImGui.TextUnformatted($"— {p.StatusText}");
            ImGui.PopStyleColor();
        }
        ImGui.PopID();
    }

    public void Dispose()
    {
        // No resources to dispose; the underlying service is disposed separately.
    }

    private void DrawRoleGroup(RoleGroup group)
    {
        group.Members.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
        var label = string.IsNullOrEmpty(group.Name) ? group.Id : group.Name;
        ImGui.TextUnformatted($"{label} - {group.Members.Count}");
        foreach (var member in group.Members)
        {
            DrawPresence(member);
        }
        ImGui.Spacing();
    }

    private static Vector4 GetStatusColor(string? status)
        => status?.ToLowerInvariant() switch
        {
            "online" => OnlineColor,
            "idle" => IdleColor,
            "dnd" => DndColor,
            "do_not_disturb" => DndColor,
            _ => OfflineColor
        };

    private sealed class RoleGroup
    {
        public string Id { get; }
        public string Name { get; set; }
        public List<PresenceDto> Members { get; } = new();

        public RoleGroup(string id, string? name)
        {
            Id = id;
            Name = name ?? id;
        }
    }
}

