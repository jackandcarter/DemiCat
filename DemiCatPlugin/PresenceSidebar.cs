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
    private static readonly ImGuiMouseCursor ResizeEwCursor = ResolveResizeEwCursor();
    private static readonly HashSet<string> SyntheticRoleIds = new(StringComparer.OrdinalIgnoreCase)
    {
        "premium_subscriber"
    };

    private static readonly HashSet<string> SyntheticRoleNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "Server Booster"
    };

    private static readonly HashSet<string> OfflineStatuses = new(StringComparer.OrdinalIgnoreCase)
    {
        "offline",
        "invisible",
    };

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
            .Where(p => !IsOffline(p.Status))
            .ToList();
        var offline = presences
            .Where(p => IsOffline(p.Status))
            .OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var roleById = new Dictionary<string, RoleDto>(StringComparer.Ordinal);
        foreach (var role in RoleCache.Roles)
        {
            if (string.IsNullOrEmpty(role.Id))
                continue;
            roleById[role.Id] = role;
        }

        var hoistedOrder = RoleCache.Roles
            .Where(r => !string.IsNullOrEmpty(r.Id) && IsHoistedRole(r))
            .OrderByDescending(r => r.Position)
            .Select(r => r.Id)
            .ToList();

        var roleGroups = new Dictionary<string, RoleGroup>(StringComparer.Ordinal);
        foreach (var roleId in hoistedOrder)
        {
            if (!roleById.TryGetValue(roleId, out var role))
                continue;

            var label = string.IsNullOrEmpty(role.Name) ? roleId : role.Name;
            roleGroups[roleId] = new RoleGroup(roleId, label);
        }

        var ungroupedOnline = new List<PresenceDto>();
        foreach (var presence in online)
        {
            var primaryRoleId = GetHighestHoistedRoleId(presence, roleById);
            if (!string.IsNullOrEmpty(primaryRoleId) && roleGroups.TryGetValue(primaryRoleId, out var group))
            {
                group.Members.Add(presence);
            }
            else
            {
                ungroupedOnline.Add(presence);
            }
        }

        var anyOnline = false;
        foreach (var roleId in hoistedOrder)
        {
            if (!roleGroups.TryGetValue(roleId, out var group) || group.Members.Count == 0)
                continue;

            anyOnline = true;
            DrawRoleGroup(group);
        }

        if (ungroupedOnline.Count > 0)
        {
            anyOnline = true;
            ungroupedOnline.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
            ImGui.TextUnformatted($"ONLINE — {ungroupedOnline.Count}");
            foreach (var presence in ungroupedOnline)
            {
                DrawPresence(presence);
            }
            ImGui.Spacing();
        }

        if (!anyOnline)
        {
            ImGui.TextUnformatted("No one is online.");
            ImGui.Spacing();
        }

        if (offline.Count > 0)
        {
            if (anyOnline)
            {
                ImGui.Spacing();
            }
            ImGui.TextUnformatted($"OFFLINE — {offline.Count}");
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
            ImGui.SetMouseCursor(ResizeEwCursor);
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

    private static bool IsOffline(string? status)
    {
        if (string.IsNullOrEmpty(status))
        {
            return true;
        }

        return OfflineStatuses.Contains(status);
    }

    private static string? GetHighestHoistedRoleId(
        PresenceDto presence,
        IReadOnlyDictionary<string, RoleDto> roleById)
    {
        string? bestRoleId = null;
        RoleDto? bestRole = null;

        foreach (var roleId in presence.Roles)
        {
            if (string.IsNullOrEmpty(roleId) || !roleById.TryGetValue(roleId, out var role))
            {
                continue;
            }

            if (!IsHoistedRole(role))
            {
                continue;
            }

            if (bestRole == null || role.Position > bestRole.Position ||
                (role.Position == bestRole.Position && string.Compare(role.Name, bestRole.Name, StringComparison.OrdinalIgnoreCase) < 0))
            {
                bestRoleId = roleId;
                bestRole = role;
            }
        }

        return bestRoleId;
    }

    private static bool IsHoistedRole(RoleDto role)
        => role.Hoist || IsBoosterRole(role);

    private static bool IsBoosterRole(RoleDto role)
    {
        if (role.IsPremiumSubscriber)
        {
            return true;
        }

        if (role.Tags?.PremiumSubscriber == true)
        {
            return true;
        }

        if (!string.IsNullOrEmpty(role.Id) && SyntheticRoleIds.Contains(role.Id))
        {
            return true;
        }

        return SyntheticRoleNames.Contains(role.Name);
    }

    private void DrawRoleGroup(RoleGroup group)
    {
        group.Members.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
        var label = string.IsNullOrEmpty(group.Name) ? group.Id : group.Name;
        ImGui.TextUnformatted($"{label} — {group.Members.Count}");
        foreach (var member in group.Members)
        {
            DrawPresence(member);
        }
        ImGui.Spacing();
    }

    private static ImGuiMouseCursor ResolveResizeEwCursor()
    {
        if (Enum.TryParse("ResizeEW", ignoreCase: true, out ImGuiMouseCursor cursor))
        {
            return cursor;
        }

        return ImGuiMouseCursor.ResizeAll;
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

