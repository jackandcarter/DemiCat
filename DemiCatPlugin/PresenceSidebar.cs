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
        var roles = RoleCache.Roles;

        var online = presences.Where(p => p.Status != "offline").ToList();
        var offline = presences.Where(p => p.Status == "offline").OrderBy(p => p.Name).ToList();

        // Map online presences to their roles
        var roleGroups = new Dictionary<string, List<PresenceDto>>();
        foreach (var role in roles)
        {
            roleGroups[role.Id] = new List<PresenceDto>();
        }
        var noRole = new List<PresenceDto>();

        foreach (var p in online)
        {
            var mapped = false;
            foreach (var roleId in p.Roles)
            {
                if (roleGroups.TryGetValue(roleId, out var list))
                {
                    list.Add(p);
                    mapped = true;
                }
            }
            if (!mapped)
            {
                noRole.Add(p);
            }
        }

        var anyOnline = false;
        foreach (var role in roles)
        {
            var members = roleGroups[role.Id].OrderBy(p => p.Name).ToList();
            if (members.Count == 0)
                continue;
            anyOnline = true;
            ImGui.TextUnformatted($"{role.Name} - {members.Count}");
            foreach (var p in members)
            {
                DrawPresence(p);
            }
            ImGui.Spacing();
        }

        if (noRole.Count > 0)
        {
            anyOnline = true;
            ImGui.TextUnformatted($"No Role - {noRole.Count}");
            foreach (var p in noRole.OrderBy(p => p.Name))
            {
                DrawPresence(p);
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
            foreach (var p in offline)
            {
                DrawPresence(p);
            }
        }

        ImGui.EndChild();

        // Draw a draggable handle to allow the sidebar to be resized
        ImGui.SameLine();
        ImGui.InvisibleButton("##presence_resize", new Vector2(4, -1));
        if (ImGui.IsItemActive())
        {
            width += ImGui.GetIO().MouseDelta.X;
            if (width < 100) width = 100; // minimum width
        }
    }

    private void DrawPresence(PresenceDto p)
    {
        var color = p.Status == "online" ? new Vector4(0f, 1f, 0f, 1f) : new Vector4(0.5f, 0.5f, 0.5f, 1f);
        ImGui.PushStyleColor(ImGuiCol.Text, color);
        ImGui.TextUnformatted("●");
        ImGui.PopStyleColor();
        ImGui.SameLine();

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
        ImGui.SameLine();
        ImGui.TextUnformatted(p.Name);
    }

    public void Dispose()
    {
        // No resources to dispose; the underlying service is disposed separately.
    }
}

