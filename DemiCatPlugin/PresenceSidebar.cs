using System;
using System.Linq;
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

    public void Draw()
    {
        ImGui.BeginChild("##presence", new Vector2(150, 0), true);

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
        var online = presences.Where(p => p.Status != "offline").OrderBy(p => p.Name).ToList();
        var offline = presences.Where(p => p.Status == "offline").OrderBy(p => p.Name).ToList();

        ImGui.TextUnformatted($"Online - {online.Count}");
        foreach (var p in online)
        {
            DrawPresence(p);
        }

        ImGui.Spacing();
        ImGui.TextUnformatted($"Offline - {offline.Count}");
        foreach (var p in offline)
        {
            DrawPresence(p);
        }

        ImGui.EndChild();
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

