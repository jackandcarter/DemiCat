using System;
using System.Linq;
using System.Collections.Generic;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Textures;
using ImGuiMouseCursor = Dalamud.Bindings.ImGui.ImGuiMouseCursor;

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

    private const string PresenceUnavailableMessage = "Presence unavailable";

    public Action<string?, Action<ISharedImmediateTexture?>>? TextureLoader { get; set; }

    public PresenceSidebar(DiscordPresenceService service)
    {
        _service = service;
    }

    public void Draw(ref float width)
    {
        var shouldReturn = false;

        void ShowStatus(string? message)
        {
            var text = string.IsNullOrEmpty(message) ? PresenceUnavailableMessage : message!;
            ImGui.TextUnformatted(text);
            ImGui.Spacing();
            shouldReturn = true;
        }

        ImGui.BeginChild("##presence", new Vector2(width, 0), true);

        try
        {
            if (!_service.IsPresenceReady)
            {
                ShowStatus(null);
            }
            else if (!_service.Loaded)
            {
                _ = _service.Refresh();
                ShowStatus(_service.StatusMessage);
            }
            else
            {
                var presencesSource = _service.Presences;
                if (presencesSource == null || presencesSource.Count == 0)
                {
                    ShowStatus(_service.StatusMessage);
                }
                else if (!RoleCache.IsLoaded)
                {
                    ShowStatus(null);
                }
                else
                {
                    var presences = presencesSource.ToList();
                    if (presences.Count == 0)
                    {
                        ShowStatus(_service.StatusMessage);
                    }
                    else
                    {
                        if (!string.IsNullOrEmpty(_service.StatusMessage))
                        {
                            ImGui.TextUnformatted(_service.StatusMessage);
                            ImGui.Spacing();
                        }

                        var roles = RoleCache.Roles;
                        if (roles == null)
                        {
                            ShowStatus(null);
                        }
                        else
                        {
                            var roleList = roles.ToList();
                            var online = presences
                                .Where(p => !IsOffline(p.Status))
                                .ToList();
                            var offline = presences
                                .Where(p => IsOffline(p.Status))
                                .OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
                                .ToList();

                            var roleById = new Dictionary<string, RoleDto>(StringComparer.Ordinal);
                            foreach (var role in roleList)
                            {
                                if (role == null || string.IsNullOrEmpty(role.Id))
                                {
                                    continue;
                                }

                                roleById[role.Id] = role;
                            }

                            var hoistedOrder = roleList
                                .Where(r => r != null && !string.IsNullOrEmpty(r.Id) && IsHoistedRole(r))
                                .OrderByDescending(r => r.Position)
                                .Select(r => r.Id!)
                                .ToList();

                            var roleGroups = new Dictionary<string, RoleGroup>(StringComparer.Ordinal);
                            foreach (var roleId in hoistedOrder)
                            {
                                if (string.IsNullOrEmpty(roleId))
                                {
                                    continue;
                                }

                                if (!roleById.TryGetValue(roleId, out var role))
                                {
                                    continue;
                                }

                                var label = string.IsNullOrEmpty(role.Name) ? roleId : role.Name;
                                roleGroups[roleId] = new RoleGroup(roleId, label);
                            }

                            var ungroupedOnline = new List<PresenceDto>();
                            foreach (var presence in online)
                            {
                                var primaryRoleId = GetHighestHoistedRoleId(presence, roleById);
                                if (!string.IsNullOrEmpty(primaryRoleId) &&
                                    roleGroups.TryGetValue(primaryRoleId, out var group))
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
                                {
                                    continue;
                                }

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
                        }
                    }
                }
            }
        }
        catch (Exception ex)
        {
            PluginServices.Instance?.Log.Error(ex, "Failed to draw presence sidebar.");
            shouldReturn = true;
        }
        finally
        {
            ImGui.EndChild();

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

        if (shouldReturn)
        {
            return;
        }
    }

    private void DrawPresence(PresenceDto p)
    {
        ImGui.PushID(p.Id);
        var drawList = ImGui.GetWindowDrawList();

        const float rowHeight = 40f;
        var backgroundMin = ImGui.GetCursorScreenPos();
        var availableWidth = MathF.Max(0f, ImGui.GetContentRegionAvail().X);
        var backgroundMax = new Vector2(backgroundMin.X + availableWidth, backgroundMin.Y + rowHeight);
        DrawPresenceBackground(drawList, backgroundMin, backgroundMax, p);

        ImGui.BeginGroup();

        var avatarSize = new Vector2(36f, 36f);
        var color = GetStatusColor(p.Status);

        var groupStartY = ImGui.GetCursorPosY();
        var avatarOffsetY = MathF.Max(0f, (rowHeight - avatarSize.Y) * 0.5f);
        ImGui.SetCursorPosY(groupStartY + avatarOffsetY);

        if (TextureLoader != null && !string.IsNullOrEmpty(p.AvatarUrl) && p.AvatarTexture == null)
        {
            TextureLoader(p.AvatarUrl, t => p.AvatarTexture = t);
        }

        var avatarPos = ImGui.GetCursorScreenPos();
        var drewAvatar = false;
        if (p.AvatarTexture != null)
        {
            try
            {
                var wrap = p.AvatarTexture.GetWrapOrEmpty();
                ImGui.Image(wrap.Handle, avatarSize);
                drewAvatar = true;
            }
            catch (ObjectDisposedException)
            {
                p.AvatarTexture = null;
                if (TextureLoader != null && !string.IsNullOrEmpty(p.AvatarUrl))
                {
                    TextureLoader(p.AvatarUrl, t => p.AvatarTexture = t);
                }
            }
        }

        if (!drewAvatar)
        {
            ImGui.Dummy(avatarSize);
        }

        var indicatorRadius = 5f;
        var indicatorCenter = avatarPos + avatarSize - new Vector2(indicatorRadius + 2f, indicatorRadius + 2f);
        var windowBgColor = ImGui.ColorConvertFloat4ToU32(ImGui.GetStyle().Colors[(int)ImGuiCol.WindowBg]);
        var borderColor = ImGui.ColorConvertFloat4ToU32(new Vector4(0f, 0f, 0f, 0.75f));
        drawList.AddCircleFilled(indicatorCenter, indicatorRadius + 2.5f, windowBgColor, 16);
        drawList.AddCircleFilled(indicatorCenter, indicatorRadius + 1.5f, borderColor, 16);
        drawList.AddCircleFilled(indicatorCenter, indicatorRadius, ImGui.ColorConvertFloat4ToU32(color), 16);

        ImGui.SetCursorPosY(groupStartY);
        ImGui.SameLine(0f, 8f);
        ImGui.BeginGroup();

        var nameColor = color;
        nameColor.W = 1f;
        ImGui.PushStyleColor(ImGuiCol.Text, nameColor);
        ImGui.TextUnformatted(p.Name);
        ImGui.PopStyleColor();

        var statusText = p.StatusText;
        if (!string.IsNullOrWhiteSpace(statusText))
        {
            ImGui.PushStyleColor(ImGuiCol.Text, StatusTextColor);
            var wrapWidth = MathF.Max(0f, ImGui.GetContentRegionAvail().X);
            if (wrapWidth > 0f)
            {
                ImGui.PushTextWrapPos(ImGui.GetCursorPosX() + wrapWidth);
            }
            ImGui.TextUnformatted(statusText);
            if (wrapWidth > 0f)
            {
                ImGui.PopTextWrapPos();
            }
            ImGui.PopStyleColor();
        }


        if (p.RoleDetails.Count > 0)
        {
            var firstBadge = true;
            for (var i = 0; i < p.RoleDetails.Count; i++)
            {
                var role = p.RoleDetails[i];
                if (role == null || string.IsNullOrWhiteSpace(role.Name))
                {
                    continue;
                }

                if (firstBadge)
                {
                    ImGui.Spacing();
                    firstBadge = false;
                }
                else
                {
                    ImGui.SameLine(0f, 4f);
                }

                ImGui.PushID(i);
                DrawRoleBadge(drawList, role.Name);
                ImGui.PopID();
            }
        }

        ImGui.EndGroup();

        var usedHeight = ImGui.GetCursorPosY() - groupStartY;
        if (usedHeight < rowHeight)
        {
            ImGui.Dummy(new Vector2(0f, rowHeight - usedHeight));
        }

        ImGui.EndGroup();
        ImGui.PopID();
    }

    protected virtual void DrawPresenceBackground(ImDrawListPtr drawList, Vector2 min, Vector2 max, PresenceDto presence)
    {
        if (max.X <= min.X || max.Y <= min.Y)
        {
            if (TextureLoader != null && !string.IsNullOrEmpty(presence.BannerUrl) && presence.BannerTexture == null)
            {
                TextureLoader(presence.BannerUrl, t => presence.BannerTexture = t);
            }
            return;
        }

        var bannerDrawn = false;
        if (presence.BannerTexture != null)
        {
            try
            {
                var wrap = presence.BannerTexture.GetWrapOrEmpty();
                drawList.AddImage(wrap.Handle, min, max);
                bannerDrawn = true;
            }
            catch (ObjectDisposedException)
            {
                presence.BannerTexture = null;
            }
        }

        if (!bannerDrawn && TextureLoader != null && !string.IsNullOrEmpty(presence.BannerUrl) && presence.BannerTexture == null)
        {
            TextureLoader(presence.BannerUrl, t => presence.BannerTexture = t);
        }

        if (!bannerDrawn)
        {
            var gradient = presence.AccentColorValue.HasValue
                ? ComputeAccentGradient(presence.AccentColorValue.Value)
                : null;
            if (gradient.HasValue)
            {
                var (top, bottom) = gradient.Value;
                drawList.AddRectFilledMultiColor(min, max, top, top, bottom, bottom);
            }
        }
    }

    internal static (uint Top, uint Bottom)? ComputeAccentGradient(uint accentRgb)
    {
        var topRgb = ColorUtils.MixRgb(accentRgb, 0xFFFFFF, 0.35f);
        var bottomRgb = ColorUtils.MixRgb(accentRgb, 0x000000, 0.45f);
        var topColor = ColorUtils.RgbToImGui(topRgb);
        var bottomColor = ColorUtils.RgbToImGui(bottomRgb);
        if (topColor == bottomColor)
        {
            bottomColor = ColorUtils.RgbToImGui(ColorUtils.MixRgb(accentRgb, 0x000000, 0.6f));
        }
        return (topColor, bottomColor);
    }

    private static void DrawRoleBadge(ImDrawListPtr drawList, string text)
    {
        var style = ImGui.GetStyle();
        var padding = new Vector2(MathF.Max(4f, style.FramePadding.X), MathF.Max(2f, style.FramePadding.Y * 0.75f));
        var textSize = ImGui.CalcTextSize(text);
        var totalSize = textSize + padding * 2f;
        var cursor = ImGui.GetCursorScreenPos();
        var rounding = MathF.Max(4f, style.FrameRounding);

        var bgColor = ImGui.ColorConvertFloat4ToU32(new Vector4(0.4f, 0.4f, 0.4f, 0.35f));
        var outlineColor = ImGui.ColorConvertFloat4ToU32(new Vector4(1f, 1f, 1f, 0.18f));
        var textColor = ImGui.ColorConvertFloat4ToU32(new Vector4(0.95f, 0.95f, 0.95f, 1f));

        drawList.AddRectFilled(cursor, cursor + totalSize, bgColor, rounding);
        drawList.AddRect(cursor, cursor + totalSize, outlineColor, rounding);

        ImGui.InvisibleButton("##badge", totalSize);
        drawList.AddText(cursor + padding, textColor, text);
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

