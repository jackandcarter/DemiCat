using System;
using System.Linq;
using System.Collections.Generic;
using System.Net.Http;
using System.Numerics;
using System.Threading.Tasks;
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
    private readonly Config _config;
    private readonly HttpClient _httpClient;
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
    private static readonly TimeSpan RefreshCooldown = TimeSpan.FromSeconds(1);
    private Task? _refreshTask;
    private bool _refreshInFlight;
    private DateTime _nextRefreshAllowed = DateTime.MinValue;
    private readonly List<PresenceDto> _items = new();
    private PresenceConnectionState _lastConn = PresenceConnectionState.Disconnected;
    private bool _pendingRebuild = true;

    public Action<string?, Action<ISharedImmediateTexture?>>? TextureLoader { get; set; }
    public Action<string?>? TextureTouch { get; set; }

    public PresenceSidebar(DiscordPresenceService service, Config config, HttpClient httpClient)
    {
        _service = service;
        _config = config;
        _httpClient = httpClient;
        _service.PresencesChanged += HandlePresencesChanged;
    }

    public void Draw(ref float width)
    {
        DrawContent(new Vector2(width, 0));

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

    public void DrawStandalone()
    {
        var size = ImGui.GetContentRegionAvail();
        if (size.X < 0f)
        {
            size.X = 0f;
        }

        if (size.Y < 0f)
        {
            size.Y = 0f;
        }

        DrawContent(size);
    }

    private void DrawContent(Vector2 size)
    {
        var shouldReturn = false;

        void ShowStatus(string? message)
        {
            var text = string.IsNullOrEmpty(message) ? PresenceUnavailableMessage : message!;
            ImGui.TextUnformatted(text);
            ImGui.Spacing();
            shouldReturn = true;
        }

        EnsureRolesLoaded();

        var connection = _service.ConnectionState;
        if (_lastConn != connection && connection == PresenceConnectionState.Connected)
        {
            _items.Clear();
            _pendingRebuild = true;
        }
        _lastConn = connection;

        if (_pendingRebuild)
        {
            RebuildFrom(_service.Presences);
            _pendingRebuild = false;
        }

        var hasSnapshot = _items.Count > 0;

        var clampedSize = new Vector2(MathF.Max(0f, size.X), MathF.Max(0f, size.Y));
        ImGui.BeginChild("##presence", clampedSize, true);

        try
        {
            if (!_service.IsPresenceReady && !hasSnapshot)
            {
                ShowStatus(null);
            }
            else if (!_service.Loaded && !hasSnapshot)
            {
                TryRequestRefresh(force: false);
                ShowStatus(_service.StatusMessage);
            }
            else
            {
                if (!_service.IsPresenceReady && hasSnapshot)
                {
                    var status = _service.StatusMessage;
                    if (!string.IsNullOrEmpty(status))
                    {
                        ImGui.TextUnformatted(status);
                        ImGui.Spacing();
                    }
                }

                var presencesSource = _items;
                if (presencesSource == null || presencesSource.Count == 0)
                {
                    MaybeRequestRecoveryRefresh(connection);
                    ShowStatus(string.IsNullOrWhiteSpace(_service.StatusMessage) ? null : _service.StatusMessage);
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
                        MaybeRequestRecoveryRefresh(connection);
                        ShowStatus(string.IsNullOrWhiteSpace(_service.StatusMessage) ? null : _service.StatusMessage);
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
            if (!p.AvatarLoadRequested)
            {
                p.AvatarLoadRequested = true;
                TextureLoader(p.AvatarUrl, t =>
                {
                    p.AvatarTexture = t;
                    p.AvatarLoadRequested = false;
                });
            }
        }

        var avatarPos = ImGui.GetCursorScreenPos();
        var drewAvatar = false;
        if (p.AvatarTexture != null)
        {
            try
            {
                var wrap = p.AvatarTexture.GetWrapOrEmpty();
                if (wrap.Width > 0 && wrap.Height > 0)
                {
                    ImGui.Image(wrap.Handle, avatarSize);
                    TextureTouch?.Invoke(p.AvatarUrl);
                    drewAvatar = true;
                }
            }
            catch (ObjectDisposedException)
            {
                p.AvatarTexture = null;
                p.AvatarLoadRequested = false;
                if (TextureLoader != null && !string.IsNullOrEmpty(p.AvatarUrl))
                {
                    if (!p.AvatarLoadRequested)
                    {
                        p.AvatarLoadRequested = true;
                        TextureLoader(p.AvatarUrl, t =>
                        {
                            p.AvatarTexture = t;
                            p.AvatarLoadRequested = false;
                        });
                    }
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
                if (!presence.BannerLoadRequested)
                {
                    presence.BannerLoadRequested = true;
                    TextureLoader(presence.BannerUrl, t =>
                    {
                        presence.BannerTexture = t;
                        presence.BannerLoadRequested = false;
                    });
                }
            }
            return;
        }

        var bannerDrawn = false;
        if (presence.BannerTexture != null)
        {
            try
            {
                var wrap = presence.BannerTexture.GetWrapOrEmpty();
                if (wrap.Width > 0 && wrap.Height > 0)
                {
                    drawList.AddImage(wrap.Handle, min, max);
                    TextureTouch?.Invoke(presence.BannerUrl);
                    bannerDrawn = true;
                }
            }
            catch (ObjectDisposedException)
            {
                presence.BannerTexture = null;
                presence.BannerLoadRequested = false;
            }
        }

        if (!bannerDrawn && TextureLoader != null && !string.IsNullOrEmpty(presence.BannerUrl) && presence.BannerTexture == null)
        {
            if (!presence.BannerLoadRequested)
            {
                presence.BannerLoadRequested = true;
                TextureLoader(presence.BannerUrl, t =>
                {
                    presence.BannerTexture = t;
                    presence.BannerLoadRequested = false;
                });
            }
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

    private void RebuildFrom(IReadOnlyList<PresenceDto>? snapshot)
    {
        _items.Clear();
        if (snapshot == null)
        {
            return;
        }

        foreach (var presence in snapshot)
        {
            if (presence == null || string.IsNullOrWhiteSpace(presence.Id))
            {
                continue;
            }

            _items.Add(presence);
        }
    }

    public void Dispose()
    {
        _service.PresencesChanged -= HandlePresencesChanged;
    }

    private void HandlePresencesChanged(object? sender, EventArgs e)
    {
        _pendingRebuild = true;
    }

    private void TryRequestRefresh(bool force)
    {
        if (_refreshInFlight)
        {
            return;
        }

        if (!force && DateTime.UtcNow < _nextRefreshAllowed)
        {
            return;
        }

        var refreshTask = _service.Refresh(force: force);
        _refreshTask = refreshTask;
        _refreshInFlight = true;

        void CompleteRefresh()
        {
            _nextRefreshAllowed = DateTime.UtcNow + RefreshCooldown;
            if (ReferenceEquals(_refreshTask, refreshTask))
            {
                _refreshTask = null;
            }

            _refreshInFlight = false;
        }

        if (!refreshTask.IsCompleted)
        {
            _ = refreshTask.ContinueWith(_ => CompleteRefresh(), TaskScheduler.Default);
        }
        else
        {
            CompleteRefresh();
        }
    }

    private void MaybeRequestRecoveryRefresh(PresenceConnectionState connection)
    {
        if (connection == PresenceConnectionState.Connected && string.IsNullOrWhiteSpace(_service.StatusMessage))
        {
            return;
        }

        TryRequestRefresh(force: true);
    }

    private void EnsureRolesLoaded()
    {
        if (RoleCache.IsLoaded)
        {
            return;
        }

        var tokenManager = TokenManager.Instance;
        if (tokenManager?.IsReady() != true)
        {
            return;
        }

        _ = RoleCache.EnsureLoaded(_httpClient, _config);
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

