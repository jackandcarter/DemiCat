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
    private readonly List<PresenceCard> _items = new();
    private readonly Dictionary<string, PresenceCard> _cardById = new(StringComparer.Ordinal);
    private PresenceConnectionState _lastConn = PresenceConnectionState.Disconnected;
    private bool _pendingRebuild = true;
    private long _lastRoleVersion = long.MinValue;

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
                    var cards = presencesSource;
                    if (cards.Count == 0)
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
                            var roleById = new Dictionary<string, RoleDto>(StringComparer.Ordinal);
                            foreach (var role in roleList)
                            {
                                if (role == null || string.IsNullOrEmpty(role.Id))
                                {
                                    continue;
                                }

                                roleById[role.Id] = role;
                            }

                            var roleVersion = RoleCache.Version;
                            if (_lastRoleVersion != roleVersion)
                            {
                                foreach (var card in cards)
                                {
                                    card.InvalidateMetadata();
                                }
                                _lastRoleVersion = roleVersion;
                            }

                            foreach (var card in cards)
                            {
                                card.EnsureMetadata(roleById);
                            }

                            var online = cards
                                .Where(p => !IsOffline(p.Presence.Status))
                                .ToList();
                            var offline = cards
                                .Where(p => IsOffline(p.Presence.Status))
                                .OrderByDescending(p => p.PrimaryRolePosition)
                                .ThenBy(p => p.Presence.Name, StringComparer.OrdinalIgnoreCase)
                                .ToList();

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

                            var ungroupedOnline = new List<PresenceCard>();
                            foreach (var presence in online)
                            {
                                var primaryRoleId = presence.PrimaryRoleId;
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
                                ungroupedOnline.Sort((a, b) => string.Compare(a.Presence.Name, b.Presence.Name, StringComparison.OrdinalIgnoreCase));
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

    private void DrawPresence(PresenceCard card)
    {
        var presence = card.Presence;
        ImGui.PushID(presence.Id);
        var drawList = ImGui.GetWindowDrawList();

        const float rowHeight = 44f;
        var backgroundMin = ImGui.GetCursorScreenPos();
        var availableWidth = MathF.Max(0f, ImGui.GetContentRegionAvail().X);
        var backgroundMax = new Vector2(backgroundMin.X + availableWidth, backgroundMin.Y + rowHeight);
        DrawPresenceBackground(drawList, backgroundMin, backgroundMax, presence);

        var hovered = ImGui.IsMouseHoveringRect(backgroundMin, backgroundMax);
        if (hovered)
        {
            var style = ImGui.GetStyle();
            var highlight = style.Colors[(int)ImGuiCol.ButtonHovered];
            highlight.W = MathF.Max(0.15f, highlight.W);
            drawList.AddRectFilled(backgroundMin, backgroundMax, ImGui.ColorConvertFloat4ToU32(highlight));
            ImGui.SetMouseCursor(ImGuiMouseCursor.Hand);
        }

        ImGui.BeginGroup();

        var avatarSize = new Vector2(36f, 36f);
        var statusColor = GetStatusColor(presence.Status);

        var groupStartY = ImGui.GetCursorPosY();
        var avatarOffsetY = MathF.Max(0f, (rowHeight - avatarSize.Y) * 0.5f);
        ImGui.SetCursorPosY(groupStartY + avatarOffsetY);

        if (TextureLoader != null && !string.IsNullOrEmpty(presence.AvatarUrl) &&
            presence.AvatarTexture == null && !presence.AvatarLoadRequested && !presence.AvatarLoadFailed)
        {
            presence.AvatarLoadRequested = true;
            TextureLoader(presence.AvatarUrl, t =>
            {
                presence.AvatarTexture = t;
                presence.AvatarLoadRequested = false;
                presence.AvatarLoadFailed = t == null;
            });
        }

        var avatarPos = ImGui.GetCursorScreenPos();
        var drewAvatar = false;
        if (presence.AvatarTexture != null)
        {
            try
            {
                var wrap = presence.AvatarTexture.GetWrapOrEmpty();
                if (wrap.Width > 0 && wrap.Height > 0)
                {
                    ImGui.Image(wrap.Handle, avatarSize);
                    TextureTouch?.Invoke(presence.AvatarUrl);
                    drewAvatar = true;
                }
            }
            catch (ObjectDisposedException)
            {
                presence.AvatarTexture = null;
                presence.AvatarLoadRequested = false;
                presence.AvatarLoadFailed = false;
                if (TextureLoader != null && !string.IsNullOrEmpty(presence.AvatarUrl))
                {
                    presence.AvatarLoadRequested = true;
                    TextureLoader(presence.AvatarUrl, t =>
                    {
                        presence.AvatarTexture = t;
                        presence.AvatarLoadRequested = false;
                        presence.AvatarLoadFailed = t == null;
                    });
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
        drawList.AddCircleFilled(indicatorCenter, indicatorRadius, ImGui.ColorConvertFloat4ToU32(statusColor), 16);

        ImGui.SetCursorPosY(groupStartY);
        ImGui.SameLine(0f, 8f);
        ImGui.BeginGroup();

        var nameColor = card.NameColor;
        nameColor.W = 1f;
        ImGui.PushStyleColor(ImGuiCol.Text, nameColor);
        ImGui.TextUnformatted(presence.Name);
        ImGui.PopStyleColor();

        var statusText = presence.StatusText;
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

        ImGui.EndGroup();

        var usedHeight = ImGui.GetCursorPosY() - groupStartY;
        if (usedHeight < rowHeight)
        {
            ImGui.Dummy(new Vector2(0f, rowHeight - usedHeight));
        }

        ImGui.EndGroup();
        ImGui.PopID();

        if (hovered)
        {
            DrawRolesTooltip(card);
        }
    }

    private void DrawRolesTooltip(PresenceCard card)
    {
        var roles = card.RoleNames;
        if (roles.Count == 0)
        {
            return;
        }

        if (!ImGui.BeginTooltip())
        {
            return;
        }

        try
        {
            var nameColor = card.NameColor;
            nameColor.W = 1f;
            ImGui.PushStyleColor(ImGuiCol.Text, nameColor);
            ImGui.TextUnformatted(card.Presence.Name);
            ImGui.PopStyleColor();

            if (!string.IsNullOrWhiteSpace(card.Presence.StatusText))
            {
                ImGui.PushStyleColor(ImGuiCol.Text, StatusTextColor);
                ImGui.TextUnformatted(card.Presence.StatusText);
                ImGui.PopStyleColor();
            }

            if (roles.Count > 0)
            {
                if (!string.IsNullOrWhiteSpace(card.Presence.StatusText))
                {
                    ImGui.Separator();
                }

                ImGui.TextUnformatted("Roles");
                ImGui.Separator();
                foreach (var role in roles)
                {
                    ImGui.TextUnformatted(role);
                }
            }
        }
        finally
        {
            ImGui.EndTooltip();
        }
    }

    private sealed class PresenceCard
    {
        private long _cachedRevision = long.MinValue;
        private int _cachedRoleVersion = -1;
        private readonly List<string> _roleNames = new();

        public PresenceCard(PresenceDto presence)
        {
            Presence = presence;
            NameColor = new Vector4(1f, 1f, 1f, 1f);
            PrimaryRolePosition = int.MinValue;
        }

        public PresenceDto Presence { get; private set; }
        public Vector4 NameColor { get; private set; }
        public string? PrimaryRoleId { get; private set; }
        public int PrimaryRolePosition { get; private set; }
        public IReadOnlyList<string> RoleNames => _roleNames;

        public void UpdatePresence(PresenceDto presence)
        {
            Presence = presence;
            InvalidateMetadata();
        }

        public void InvalidateMetadata()
        {
            _cachedRevision = long.MinValue;
            _cachedRoleVersion = -1;
        }

        public void EnsureMetadata(IReadOnlyDictionary<string, RoleDto> roleById)
        {
            var revision = Presence.Revision;
            var roleVersion = roleById?.Count ?? 0;
            if (_cachedRevision == revision && _cachedRoleVersion == roleVersion)
            {
                return;
            }

            _cachedRevision = revision;
            _cachedRoleVersion = roleVersion;

            PrimaryRoleId = null;
            PrimaryRolePosition = int.MinValue;
            _roleNames.Clear();

            var accent = Presence.AccentColor;
            if (accent.HasValue)
            {
                var color = accent.Value;
                if (color.W <= 0f)
                {
                    color.W = 1f;
                }
                NameColor = color;
            }
            else
            {
                var fallback = GetStatusColor(Presence.Status);
                fallback.W = 1f;
                NameColor = fallback;
            }

            if (roleById == null || roleById.Count == 0)
            {
                foreach (var detail in Presence.RoleDetails)
                {
                    if (detail != null && !string.IsNullOrWhiteSpace(detail.Name))
                    {
                        _roleNames.Add(detail.Name);
                    }
                }
                return;
            }

            PrimaryRoleId = GetHighestHoistedRoleId(Presence, roleById);
            if (!string.IsNullOrEmpty(PrimaryRoleId) && roleById.TryGetValue(PrimaryRoleId, out var primaryRole))
            {
                PrimaryRolePosition = primaryRole.Position;
            }

            var sorted = new List<(string Name, int Position)>();
            foreach (var detail in Presence.RoleDetails)
            {
                if (detail == null || string.IsNullOrWhiteSpace(detail.Name))
                {
                    continue;
                }

                var position = int.MinValue;
                if (!string.IsNullOrEmpty(detail.Id) && roleById.TryGetValue(detail.Id, out var role))
                {
                    position = role.Position;
                    if (PrimaryRolePosition == int.MinValue || position > PrimaryRolePosition)
                    {
                        PrimaryRolePosition = position;
                    }
                }

                sorted.Add((detail.Name, position));
            }

            sorted.Sort((a, b) =>
            {
                var cmp = b.Position.CompareTo(a.Position);
                return cmp != 0 ? cmp : string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase);
            });

            foreach (var (name, _) in sorted)
            {
                _roleNames.Add(name);
            }
        }
    }

    protected virtual void DrawPresenceBackground(ImDrawListPtr drawList, Vector2 min, Vector2 max, PresenceDto presence)
    {
        if (max.X <= min.X || max.Y <= min.Y)
        {
            if (TextureLoader != null && !string.IsNullOrEmpty(presence.BannerUrl) &&
                presence.BannerTexture == null && !presence.BannerLoadRequested && !presence.BannerLoadFailed)
            {
                presence.BannerLoadRequested = true;
                TextureLoader(presence.BannerUrl, t =>
                {
                    presence.BannerTexture = t;
                    presence.BannerLoadRequested = false;
                    presence.BannerLoadFailed = t == null;
                });
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
                presence.BannerLoadFailed = false;
            }
        }

        if (!bannerDrawn && TextureLoader != null && !string.IsNullOrEmpty(presence.BannerUrl) &&
            presence.BannerTexture == null && !presence.BannerLoadRequested && !presence.BannerLoadFailed)
        {
            presence.BannerLoadRequested = true;
            TextureLoader(presence.BannerUrl, t =>
            {
                presence.BannerTexture = t;
                presence.BannerLoadRequested = false;
                presence.BannerLoadFailed = t == null;
            });
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

    private void RebuildFrom(IReadOnlyList<PresenceDto>? snapshot)
    {
        _items.Clear();
        if (snapshot == null)
        {
            _cardById.Clear();
            return;
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var presence in snapshot)
        {
            if (presence == null || string.IsNullOrWhiteSpace(presence.Id))
            {
                continue;
            }

            if (!_cardById.TryGetValue(presence.Id, out var card))
            {
                card = new PresenceCard(presence);
                _cardById[presence.Id] = card;
            }
            else
            {
                card.UpdatePresence(presence);
            }

            _items.Add(card);
            seen.Add(presence.Id);
        }

        if (_cardById.Count == seen.Count)
        {
            return;
        }

        var removed = new List<string>();
        foreach (var kvp in _cardById)
        {
            if (!seen.Contains(kvp.Key))
            {
                removed.Add(kvp.Key);
            }
        }

        foreach (var key in removed)
        {
            _cardById.Remove(key);
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
        group.Members.Sort((a, b) => string.Compare(a.Presence.Name, b.Presence.Name, StringComparison.OrdinalIgnoreCase));
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
        public List<PresenceCard> Members { get; } = new();

        public RoleGroup(string id, string? name)
        {
            Id = id;
            Name = name ?? id;
        }
    }
}

