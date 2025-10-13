using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.ManagedFontAtlas;
using Dalamud.Interface.Textures;
using Dalamud.Interface.Utility;
using DemiCatPlugin.Emoji;
using StbImageSharp;

namespace DemiCatPlugin;

public class MainWindow : IDisposable
{
    private readonly Config _config;
    private readonly UiRenderer _ui;
    private readonly ChatWindow? _chat;
    private readonly OfficerChatWindow _officer;
    private readonly SettingsWindow _settings;
    private readonly EventCreateWindow _create;
    private readonly TemplatesWindow _templates;
    private readonly RequestBoardWindow _requestBoard;
    private readonly NotePadWindow _notePad;
    private readonly EmojiManager _emojiManager;
    private SyncshellWindow? _syncshell;
    private bool _syncshellEnabled;
    private readonly HttpClient _httpClient;
    private const float FadeAlphaTolerance = 0.001f;
    private const float MinimumFadeDuration = 0.001f;
    private float _timeSinceLastInteraction;
    private float _fadeAlpha = 1f;
    private bool _styleNeedsUpdate = true;
    private readonly Dictionary<string, bool> _windowStates = new();
    private readonly List<DockItem> _dockItems = new();
    private readonly Dictionary<string, DockItem> _dockItemMap = new(StringComparer.Ordinal);
    private readonly string? _dockIconDirectory;
    private string? _draggedDockId;
    private bool _dockOrderDirty;
    private bool _linkNotificationShown;
    private bool _dockWasVisible;

    private const float DockIconSize = 48f;
    private const float DockIconSpacing = 12f;
    private const float DockIndicatorRadius = 4f;
    private const float DockIconRounding = 12f;
    private const float DockWindowRounding = 20f;
    private const string DockDragPayloadType = "DEMICAT_DOCK_ITEM";
    private static readonly string[] ManagedWindowIds =
    {
        DockIds.Events,
        DockIds.Create,
        DockIds.Templates,
        DockIds.NotePad,
        DockIds.Requests,
        DockIds.Syncshell,
        DockIds.Chat,
        DockIds.Officer
    };

    public bool IsOpen;
    private bool _hasOfficerAccess;
    public bool HasOfficerAccess
    {
        get => _hasOfficerAccess;
        set
        {
            _hasOfficerAccess = value;
            if (!_hasOfficerAccess)
            {
                ForceWindowClosed(DockIds.Officer);
            }
        }
    }
    public void SetNotePadReadOnly(bool isReadOnly)
    {
        _notePad.IsReadOnly = isReadOnly;
    }
    public UiRenderer Ui => _ui;
    public EventCreateWindow EventCreateWindow => _create;
    public TemplatesWindow TemplatesWindow => _templates;
    public NotePadWindow NotePadWindow => _notePad;
    internal static MainWindow? Instance { get; private set; }

    public MainWindow(
        Config config,
        UiRenderer ui,
        ChatWindow? chat,
        OfficerChatWindow officer,
        SettingsWindow settings,
        HttpClient httpClient,
        ChannelService channelService,
        ChannelSelectionService channelSelection,
        EmojiManager emojiManager,
        NotePadWindow notePad)
    {
        _config = config;
        _ui = ui;
        _chat = chat;
        _officer = officer;
        _settings = settings;
        _httpClient = httpClient;
        _emojiManager = emojiManager;
        _create = new EventCreateWindow(config, httpClient, channelService, channelSelection, emojiManager);
        _templates = new TemplatesWindow(config, httpClient, channelService, channelSelection, emojiManager);
        _requestBoard = new RequestBoardWindow(config, httpClient);
        _notePad = notePad;
        _syncshellEnabled = false;
        _syncshell = null;
        _dockIconDirectory = PluginServices.Instance?.PluginInterface.AssemblyLocation.DirectoryName is { } dir
            ? Path.Combine(dir, "Dock")
            : null;

        InitializeDockItems();
        EnsureDockOrder();
        UpdateSyncshell();
        Instance = this;
    }

    internal void UpdateSyncshell()
    {
        var shouldEnable = _config.EnableSyncShell && IsLinked();
        if (_syncshellEnabled == shouldEnable)
            return;

        _syncshellEnabled = shouldEnable;
        if (_syncshellEnabled)
        {
            _syncshell = new SyncshellWindow(_config);
            PluginServices.Instance?.Framework.RunOnTick(() => { });
        }
        else
        {
            _syncshell?.Dispose();
            _syncshell = null;
            ForceWindowClosed(DockIds.Syncshell);
        }
    }

    public void Draw()
    {
        UpdateSyncshell();

        var linked = IsLinked();

        if (!linked)
        {
            EnforceWindowAvailability(false);
            if (IsOpen)
            {
                NotifyLinkRequired();
                IsOpen = false;
            }

            ResetFadeTimer();
            _dockWasVisible = false;
            return;
        }

        ResetLinkNotification();

        if (!IsOpen)
        {
            ResetFadeTimer();
            _dockWasVisible = false;
            return;
        }

        if (!_dockWasVisible)
        {
            ApplyDockAutoOpen();
        }
        _dockWasVisible = true;

        var io = ImGui.GetIO();
        var deltaTime = Math.Max(0f, io.DeltaTime);
        var fadeAlpha = GetCurrentFadeAlpha();

        EnforceWindowAvailability(true);

        var primaryColor = Config.SanitizeColor(_config.PrimaryWindowColor, Config.DefaultPrimaryWindowColor);
        var childBaseColor = AdjustBrightness(primaryColor, 0.9f);
        var accentColor = Config.SanitizeColor(_config.SecondaryAccentColor, Config.DefaultSecondaryAccentColor);
        var dockBorderColor = Config.SanitizeColor(_config.DockBorderColor, Config.DefaultDockBorderColor);
        var tabBaseColor = AdjustBrightness(primaryColor, 1.05f);
        var tabActiveBaseColor = accentColor;
        var tabHoveredBaseColor = AdjustBrightness(accentColor, 1.1f);

        if (_styleNeedsUpdate)
        {
            ApplyAccentColors();
            _styleNeedsUpdate = false;
        }

        var styleAlpha = _config.ChatFadeOutEnabled ? fadeAlpha : 1f;
        var dockFadeAlpha = _config.ChatFadeOutEnabled && _config.DockAutoFadeEnabled ? fadeAlpha : 1f;

        var interactedWithDock = DrawDock(dockBorderColor, dockFadeAlpha);
        var interactedWithWindows = DrawManagedWindows(primaryColor, childBaseColor, tabBaseColor, tabActiveBaseColor, tabHoveredBaseColor, styleAlpha, fadeAlpha);

        if (_dockOrderDirty)
        {
            _dockOrderDirty = false;
            SaveConfig();
        }

        UpdateFadeState(interactedWithDock || interactedWithWindows, deltaTime);
    }

    private void InitializeDockItems()
    {
        _dockItems.Clear();
        _dockItemMap.Clear();

        AddDockItem(new DockItem(
            DockIds.Events,
            "Events",
            "Events.png",
            () => _config.Events && IsLinked(),
            () => _config.Events && IsLinked(),
            () => GetWindowOpen(DockIds.Events),
            () => ToggleWindow(DockIds.Events)));

        AddDockItem(new DockItem(
            DockIds.Create,
            "Create",
            "Create.png",
            () => _config.Events && IsLinked(),
            () => _config.Events && IsLinked(),
            () => GetWindowOpen(DockIds.Create),
            () => ToggleWindow(DockIds.Create)));

        AddDockItem(new DockItem(
            DockIds.Templates,
            "Templates",
            "Templates.png",
            () => _config.Templates && IsLinked(),
            () => _config.Templates && IsLinked(),
            () => GetWindowOpen(DockIds.Templates),
            () => ToggleWindow(DockIds.Templates)));

        AddDockItem(new DockItem(
            DockIds.NotePad,
            "NotePad",
            "Notepad.png",
            () => _config.NotePadEnabled && IsLinked(),
            () => _config.NotePadEnabled && IsLinked(),
            () => GetWindowOpen(DockIds.NotePad),
            () => ToggleWindow(DockIds.NotePad)));

        AddDockItem(new DockItem(
            DockIds.Requests,
            "Requests",
            "Request.png",
            () => _config.Requests && IsLinked(),
            () => _config.Requests && IsLinked(),
            () => GetWindowOpen(DockIds.Requests),
            () => ToggleWindow(DockIds.Requests)));

        AddDockItem(new DockItem(
            DockIds.Syncshell,
            "Syncshell",
            "SyncShell.png",
            () => _config.EnableSyncShell && _syncshell != null && IsLinked(),
            () => _config.EnableSyncShell && _syncshell != null && IsLinked(),
            () => GetWindowOpen(DockIds.Syncshell),
            () => ToggleWindow(DockIds.Syncshell)));

        AddDockItem(new DockItem(
            DockIds.Chat,
            _chat is FcChatWindow ? "FC Chat" : "Chat",
            "FCChat.png",
            () => _chat != null && IsLinked(),
            () => _chat != null && _config.EnableFcChat && _config.SyncedChat && IsLinked(),
            () => GetWindowOpen(DockIds.Chat),
            () => ToggleWindow(DockIds.Chat),
            () => _chat != null && (!_config.EnableFcChat || !_config.SyncedChat)
                ? "FC Chat is unavailable for your account."
                : null));

        AddDockItem(new DockItem(
            DockIds.Officer,
            "Officer",
            "Officer.png",
            () => HasOfficerAccess && IsLinked(),
            () => HasOfficerAccess && IsLinked(),
            () => GetWindowOpen(DockIds.Officer),
            () => ToggleWindow(DockIds.Officer)));

        AddDockItem(new DockItem(
            DockIds.Settings,
            "Settings",
            "settingscog.png",
            () => true,
            () => true,
            () => _settings.IsOpen,
            () => _settings.IsOpen = !_settings.IsOpen));

        foreach (var id in ManagedWindowIds)
        {
            if (!_windowStates.ContainsKey(id))
            {
                _windowStates[id] = false;
            }
        }
    }

    private void AddDockItem(DockItem item)
    {
        _dockItems.Add(item);
        _dockItemMap[item.Id] = item;
    }

    private void EnsureDockOrder()
    {
        _config.DockOrder ??= new List<string>();

        var known = new HashSet<string>(_dockItems.Select(i => i.Id), StringComparer.Ordinal);
        var order = _config.DockOrder.Where(known.Contains).ToList();

        if (order.Count != _config.DockOrder.Count)
        {
            _dockOrderDirty = true;
        }

        foreach (var id in _dockItems.Select(i => i.Id))
        {
            if (!order.Contains(id))
            {
                order.Add(id);
                _dockOrderDirty = true;
            }
        }

        _config.DockOrder = order;
    }

    private void EnforceWindowAvailability(bool linked)
    {
        if (!linked || !_config.Events)
        {
            ForceWindowClosed(DockIds.Events);
            ForceWindowClosed(DockIds.Create);
        }

        if (!linked || !_config.Templates)
        {
            ForceWindowClosed(DockIds.Templates);
        }

        if (!linked || !_config.NotePadEnabled)
        {
            ForceWindowClosed(DockIds.NotePad);
        }

        if (!linked || !_config.Requests)
        {
            ForceWindowClosed(DockIds.Requests);
        }

        if (!linked || !_config.EnableSyncShell || _syncshell == null)
        {
            ForceWindowClosed(DockIds.Syncshell);
        }

        if (!linked || _chat == null || !_config.EnableFcChat || !_config.SyncedChat)
        {
            ForceWindowClosed(DockIds.Chat);
        }

        if (!linked || !HasOfficerAccess)
        {
            ForceWindowClosed(DockIds.Officer);
        }
    }

    private bool DrawDock(Vector4 borderColor, float fadeAlpha)
    {
        var background = Config.SanitizeColor(_config.DockBackgroundColor, Config.DefaultDockBackgroundColor);
        var gradientStartColor = Config.SanitizeColor(_config.DockGradientStartColor, Config.DefaultDockBackgroundColor);
        var gradientEndColor = Config.SanitizeColor(_config.DockGradientEndColor, Config.DefaultDockBackgroundColor);
        var opacity = Math.Clamp(_config.DockOpacity, 0f, 1f);
        var fade = Math.Clamp(fadeAlpha, 0f, 1f);

        var dockColor = new Vector4(
            background.X,
            background.Y,
            background.Z,
            Math.Clamp(background.W * opacity * fade, 0f, 1f));
        var gradientStart = new Vector4(
            gradientStartColor.X,
            gradientStartColor.Y,
            gradientStartColor.Z,
            Math.Clamp(gradientStartColor.W * opacity * fade, 0f, 1f));
        var gradientEnd = new Vector4(
            gradientEndColor.X,
            gradientEndColor.Y,
            gradientEndColor.Z,
            Math.Clamp(gradientEndColor.W * opacity * fade, 0f, 1f));

        var useGradient = _config.DockGradientEnabled && (gradientStart.W > 0f || gradientEnd.W > 0f);
        var borderColorWithFade = WithAlpha(borderColor, fade);

        ImGui.PushStyleColor(ImGuiCol.WindowBg, useGradient ? Vector4.Zero : dockColor);
        ImGui.PushStyleColor(ImGuiCol.Border, borderColorWithFade);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new Vector2(16f, 12f));
        ImGui.PushStyleVar(ImGuiStyleVar.WindowRounding, DockWindowRounding);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowBorderSize, 2f);
        ImGui.PushStyleVar(ImGuiStyleVar.ItemSpacing, new Vector2(DockIconSpacing, 8f));

        var interacted = false;
        var flags = ImGuiWindowFlags.NoTitleBar | ImGuiWindowFlags.NoResize | ImGuiWindowFlags.NoCollapse |
                    ImGuiWindowFlags.AlwaysAutoResize | ImGuiWindowFlags.NoScrollbar;

        if (ImGui.Begin("DemiCat Dock", flags))
        {
            interacted = HasWindowInteraction();

            if (useGradient)
            {
                var style = ImGui.GetStyle();
                var rounding = style.WindowRounding;
                var borderSize = Math.Max(1f, style.WindowBorderSize);
                var drawList = ImGui.GetWindowDrawList();
                var winPos = ImGui.GetWindowPos();
                var winSize = ImGui.GetWindowSize();
                var min = winPos;
                var max = winPos + winSize;
                if (max.X > min.X && max.Y > min.Y)
                {
                    DrawRoundedVerticalGradient(drawList, min, max, gradientStart, gradientEnd, rounding);
                    var borderColorU32 = ImGui.GetColorU32(style.Colors[(int)ImGuiCol.Border]);
                    drawList.AddRect(min, max, borderColorU32, rounding, ImDrawFlags.RoundCornersAll, borderSize);
                }
            }

            var first = true;
            foreach (var id in _config.DockOrder ?? Enumerable.Empty<string>())
            {
                if (!_dockItemMap.TryGetValue(id, out var item))
                {
                    continue;
                }

                if (!item.IsVisible())
                {
                    continue;
                }

                if (!first)
                {
                    ImGui.SameLine();
                }
                first = false;

                EnsureDockIcon(item);
                DrawDockIcon(item, borderColor, fade);
            }
        }
        ImGui.End();

        if (!ImGui.IsMouseDown(ImGuiMouseButton.Left))
        {
            _draggedDockId = null;
        }

        ImGui.PopStyleVar(4);
        ImGui.PopStyleColor(2);

        return interacted;
    }

    private void EnsureDockIcon(DockItem item)
    {
        if (item.Texture != null || item.TextureRequested)
        {
            return;
        }

        item.TextureRequested = true;

        if (string.IsNullOrEmpty(_dockIconDirectory))
        {
            return;
        }

        var path = Path.Combine(_dockIconDirectory, item.IconFileName);
        if (!File.Exists(path))
        {
            return;
        }

        byte[] payload;
        try
        {
            payload = File.ReadAllBytes(path);
        }
        catch (Exception ex)
        {
            PluginServices.Instance?.Log.Warning(ex, "Failed to read dock icon {Icon}", item.IconFileName);
            return;
        }

        ImageResult image;
        try
        {
            image = ImageResult.FromMemory(payload, ColorComponents.RedGreenBlueAlpha);
        }
        catch (Exception ex)
        {
            PluginServices.Instance?.Log.Warning(ex, "Failed to decode dock icon {Icon}", item.IconFileName);
            return;
        }

        var width = image.Width;
        var height = image.Height;
        var pixels = image.Data.ToArray();

        var services = PluginServices.Instance;
        if (services?.Framework == null)
        {
            return;
        }

        _ = services.Framework.RunOnTick(() =>
        {
            try
            {
                var wrap = services.TextureProvider.CreateFromRaw(
                    RawImageSpecification.Rgba32(width, height),
                    pixels);
                item.Texture = new ForwardingSharedImmediateTexture(wrap);
            }
            catch (Exception ex)
            {
                services.Log.Warning(ex, "Failed to create texture for dock icon {Icon}", item.IconFileName);
            }
        });
    }

    private void DrawDockIcon(DockItem item, Vector4 borderColor, float fadeAlpha)
    {
        ImGui.PushID(item.Id);

        var enabled = item.IsEnabled();
        var iconSize = DockIconSize;
        var totalHeight = iconSize + DockIndicatorRadius * 2f + 6f;
        var buttonSize = new Vector2(iconSize, totalHeight);
        var cursor = ImGui.GetCursorScreenPos();

        if (!enabled)
        {
            ImGui.BeginDisabled();
        }

        var clicked = ImGui.InvisibleButton($"{item.Id}##dock_button", buttonSize);
        if (clicked)
        {
            item.OnClick();
        }

        if (ImGui.BeginDragDropSource())
        {
            _draggedDockId = item.Id;
            ImGui.SetDragDropPayload(DockDragPayloadType, ReadOnlySpan<byte>.Empty);
            ImGui.TextUnformatted(item.DisplayName);
            ImGui.EndDragDropSource();
        }

        if (ImGui.BeginDragDropTarget())
        {
            var payload = ImGui.AcceptDragDropPayload(DockDragPayloadType);
            if (!payload.Equals(default) && !string.IsNullOrEmpty(_draggedDockId) && !string.Equals(_draggedDockId, item.Id, StringComparison.Ordinal))
            {
                ReorderDockItems(_draggedDockId!, item.Id);
                _draggedDockId = null;
            }
            ImGui.EndDragDropTarget();
        }

        if (!enabled)
        {
            ImGui.EndDisabled();
        }

        var hovered = ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled);
        var active = item.IsActive();
        var drawList = ImGui.GetWindowDrawList();
        var iconMin = cursor;
        var iconMax = iconMin + new Vector2(iconSize, iconSize);
        var tint = enabled ? Vector4.One : new Vector4(1f, 1f, 1f, 0.35f);

        if (item.Texture?.GetWrapOrEmpty() is { } wrap)
        {
            drawList.AddImageRounded(wrap.Handle, iconMin, iconMax, Vector2.Zero, Vector2.One, ImGui.GetColorU32(tint), DockIconRounding);
        }
        else
        {
            var fallback = enabled ? new Vector4(0.3f, 0.3f, 0.3f, 1f) : new Vector4(0.3f, 0.3f, 0.3f, 0.35f);
            drawList.AddRectFilled(iconMin, iconMax, ImGui.GetColorU32(fallback), DockIconRounding);
            var labelSize = ImGui.CalcTextSize(item.DisplayName);
            var labelPos = iconMin + new Vector2((iconSize - labelSize.X) * 0.5f, (iconSize - labelSize.Y) * 0.5f);
            drawList.AddText(labelPos, ImGui.GetColorU32(Vector4.One), item.DisplayName);
        }

        var fade = Math.Clamp(fadeAlpha, 0f, 1f);
        var baseColor = borderColor;
        var hoveredColor = AdjustBrightness(borderColor, 1.1f);
        var activeColor = AdjustBrightness(borderColor, 1.2f);
        var stateColor = active ? activeColor : hovered ? hoveredColor : baseColor;
        var drawColor = WithAlpha(stateColor, fade);
        drawColor.W = Math.Clamp(drawColor.W * (hovered || active ? 1f : 0.6f), 0f, 1f);
        drawList.AddRect(iconMin, iconMax, ImGui.GetColorU32(drawColor), DockIconRounding, ImDrawFlags.None, hovered || active ? 2f : 1f);

        if (active)
        {
            var center = new Vector2(iconMin.X + iconSize * 0.5f, iconMax.Y + DockIndicatorRadius + 2f);
            var indicatorColor = WithAlpha(activeColor, fade);
            drawList.AddCircleFilled(center, DockIndicatorRadius, ImGui.GetColorU32(indicatorColor));
        }

        if (hovered)
        {
            var tooltip = item.Tooltip ?? item.DisplayName;
            ImGui.SetTooltip(tooltip);
        }

        ImGui.PopID();
    }

    private void ReorderDockItems(string sourceId, string targetId)
    {
        if (_config.DockOrder == null)
        {
            return;
        }

        var order = _config.DockOrder;
        var sourceIndex = order.IndexOf(sourceId);
        var targetIndex = order.IndexOf(targetId);

        if (sourceIndex < 0 || targetIndex < 0 || sourceIndex == targetIndex)
        {
            return;
        }

        order.RemoveAt(sourceIndex);
        if (sourceIndex < targetIndex)
        {
            targetIndex--;
        }

        order.Insert(targetIndex, sourceId);
        _dockOrderDirty = true;
    }

    private bool GetWindowOpen(string id)
    {
        return _windowStates.TryGetValue(id, out var value) && value;
    }

    private void ToggleWindow(string id)
    {
        if (!IsLinked() && id != DockIds.Settings)
        {
            NotifyLinkRequired();
            return;
        }

        var desired = !GetWindowOpen(id);
        SetWindowOpen(id, desired);
    }

    private void SetWindowOpen(string id, bool open)
    {
        if (!_windowStates.ContainsKey(id))
        {
            return;
        }

        if (_windowStates[id] == open)
        {
            return;
        }

        if (open && !IsLinked() && id != DockIds.Settings)
        {
            NotifyLinkRequired();
            return;
        }

        _windowStates[id] = open;

        if (id == DockIds.Templates && open)
        {
            _templates.OnTabActivated();
        }

        if (!open && (id == DockIds.Chat || id == DockIds.Officer))
        {
            ResetFadeTimer();
        }
    }

    private void ApplyDockAutoOpen()
    {
        var targets = _config.DockAutoOpenWindows;
        if (targets == null || targets.Count == 0)
        {
            return;
        }

        foreach (var id in targets)
        {
            if (string.IsNullOrEmpty(id))
            {
                continue;
            }

            if (!_dockItemMap.TryGetValue(id, out var item))
            {
                continue;
            }

            if (!item.IsVisible() || !item.IsEnabled())
            {
                continue;
            }

            SetWindowOpen(id, true);
        }
    }

    private void ForceWindowClosed(string id)
    {
        if (!_windowStates.ContainsKey(id))
        {
            return;
        }

        if (!_windowStates[id])
        {
            return;
        }

        _windowStates[id] = false;

        if (id == DockIds.Chat || id == DockIds.Officer)
        {
            ResetFadeTimer();
        }
    }

    private static bool IsLinked()
        => TokenManager.Instance?.State == LinkState.Linked;

    internal void NotifyLinkRequired()
    {
        if (_linkNotificationShown)
        {
            return;
        }

        var services = PluginServices.Instance;
        services?.ToastGui.ShowError("Enter your DemiCat sync key in settings to connect.");
        _linkNotificationShown = true;
    }

    internal void ResetLinkNotification()
    {
        _linkNotificationShown = false;
    }

    private sealed class DockItem
    {
        private readonly Func<bool> _isVisible;
        private readonly Func<bool> _isEnabled;
        private readonly Func<bool> _isActive;
        private readonly Action _onClick;
        private readonly Func<string?>? _tooltip;

        public DockItem(
            string id,
            string displayName,
            string iconFileName,
            Func<bool> isVisible,
            Func<bool> isEnabled,
            Func<bool> isActive,
            Action onClick,
            Func<string?>? tooltip = null)
        {
            Id = id;
            DisplayName = displayName;
            IconFileName = iconFileName;
            _isVisible = isVisible;
            _isEnabled = isEnabled;
            _isActive = isActive;
            _onClick = onClick;
            _tooltip = tooltip;
        }

        public string Id { get; }
        public string DisplayName { get; }
        public string IconFileName { get; }
        public ISharedImmediateTexture? Texture { get; set; }
        public bool TextureRequested { get; set; }
        public string? Tooltip => _tooltip?.Invoke();

        public bool IsVisible() => _isVisible();

        public bool IsEnabled() => _isEnabled();

        public bool IsActive() => _isActive();

        public void OnClick() => _onClick();
    }

    internal static class DockIds
    {
        public const string Events = "events";
        public const string Create = "create";
        public const string Templates = "templates";
        public const string NotePad = "notepad";
        public const string Requests = "requests";
        public const string Syncshell = "syncshell";
        public const string Chat = "chat";
        public const string Officer = "officer";
        public const string Settings = "settings";
    }

    private bool DrawManagedWindows(
        Vector4 primaryColor,
        Vector4 childBaseColor,
        Vector4 tabBaseColor,
        Vector4 tabActiveBaseColor,
        Vector4 tabHoveredBaseColor,
        float styleAlpha,
        float fadeAlpha)
    {
        var interacted = false;

        if (GetWindowOpen(DockIds.Events))
        {
            interacted |= DrawContentWindow(
                DockIds.Events,
                "Events",
                () =>
                {
                    ImGui.BeginChild("##eventsArea", ImGui.GetContentRegionAvail(), false);
                    _ui.Draw();
                    ImGui.EndChild();
                },
                styleAlpha,
                primaryColor,
                childBaseColor,
                tabBaseColor,
                tabActiveBaseColor,
                tabHoveredBaseColor);
        }

        if (GetWindowOpen(DockIds.Create))
        {
            interacted |= DrawContentWindow(
                DockIds.Create,
                "Create",
                () => _create.Draw(),
                styleAlpha,
                primaryColor,
                childBaseColor,
                tabBaseColor,
                tabActiveBaseColor,
                tabHoveredBaseColor);
        }

        if (GetWindowOpen(DockIds.Templates))
        {
            interacted |= DrawContentWindow(
                DockIds.Templates,
                "Templates",
                () => _templates.Draw(),
                styleAlpha,
                primaryColor,
                childBaseColor,
                tabBaseColor,
                tabActiveBaseColor,
                tabHoveredBaseColor);
        }

        if (GetWindowOpen(DockIds.NotePad))
        {
            interacted |= DrawContentWindow(
                DockIds.NotePad,
                "NotePad",
                () => _notePad.Draw(),
                styleAlpha,
                primaryColor,
                childBaseColor,
                tabBaseColor,
                tabActiveBaseColor,
                tabHoveredBaseColor);
        }

        if (GetWindowOpen(DockIds.Requests))
        {
            interacted |= DrawContentWindow(
                DockIds.Requests,
                "Request Board",
                () => _requestBoard.Draw(),
                styleAlpha,
                primaryColor,
                childBaseColor,
                tabBaseColor,
                tabActiveBaseColor,
                tabHoveredBaseColor);
        }

        if (_syncshell != null && GetWindowOpen(DockIds.Syncshell))
        {
            interacted |= DrawContentWindow(
                DockIds.Syncshell,
                "Syncshell",
                () => _syncshell.Draw(),
                styleAlpha,
                primaryColor,
                childBaseColor,
                tabBaseColor,
                tabActiveBaseColor,
                tabHoveredBaseColor);
        }

        if (_chat != null && GetWindowOpen(DockIds.Chat))
        {
            var chatAlpha = _config.ChatFadeOutEnabled ? fadeAlpha : Math.Clamp(_config.FcChatOpacity, 0f, 1f);
            interacted |= DrawContentWindow(
                DockIds.Chat,
                _chat is FcChatWindow ? "FC Chat" : "Chat",
                () =>
                {
                    ImGui.BeginChild("##chatArea", ImGui.GetContentRegionAvail(), false);
                    _chat.Draw();
                    ImGui.EndChild();
                },
                chatAlpha,
                primaryColor,
                childBaseColor,
                tabBaseColor,
                tabActiveBaseColor,
                tabHoveredBaseColor);
        }

        if (HasOfficerAccess && GetWindowOpen(DockIds.Officer))
        {
            var officerAlpha = _config.ChatFadeOutEnabled ? fadeAlpha : Math.Clamp(_config.OfficerChatOpacity, 0f, 1f);
            interacted |= DrawContentWindow(
                DockIds.Officer,
                "Officer",
                () =>
                {
                    ImGui.BeginChild("##officerChatArea", ImGui.GetContentRegionAvail(), false);
                    _officer.Draw();
                    ImGui.EndChild();
                },
                officerAlpha,
                primaryColor,
                childBaseColor,
                tabBaseColor,
                tabActiveBaseColor,
                tabHoveredBaseColor);
        }

        return interacted;
    }

    private bool DrawContentWindow(
        string id,
        string title,
        Action drawContent,
        float alpha,
        Vector4 primaryColor,
        Vector4 childBaseColor,
        Vector4 tabBaseColor,
        Vector4 tabActiveBaseColor,
        Vector4 tabHoveredBaseColor)
    {
        if (!_windowStates.TryGetValue(id, out var open) || !open)
        {
            return false;
        }

        var styleVarPushed = false;
        if (alpha < 1f - FadeAlphaTolerance)
        {
            ImGui.PushStyleVar(ImGuiStyleVar.Alpha, alpha);
            styleVarPushed = true;
        }

        using var themeScope = new UiStyleScope(_config);
        var pushedColors = PushWindowOpacityStyles(primaryColor, childBaseColor, tabBaseColor, tabActiveBaseColor, tabHoveredBaseColor, alpha);

        ImGui.SetNextWindowSize(new Vector2(800f, 600f), ImGuiCond.FirstUseEver);
        ImGui.SetNextWindowSizeConstraints(new Vector2(600f, 400f), new Vector2(float.MaxValue, float.MaxValue));

        var openRef = open;
        var windowInteracted = false;
        var closeRequested = false;
        var windowFlags = ImGuiWindowFlags.NoCollapse | ImGuiWindowFlags.NoTitleBar;
        ImGui.PushStyleVar(ImGuiStyleVar.WindowBorderSize, 0f);
        var began = ImGui.Begin($"{title}##dc_{id}", ref openRef, windowFlags);
        if (began)
        {
            UiTheme.DrawWindowChrome(_config, title, () =>
            {
                openRef = false;
                closeRequested = true;
            });

            var style = ImGui.GetStyle();
            var chromeHeight = Math.Max(14f * ImGuiHelpers.GlobalScale, ImGui.GetFrameHeight());
            ImGui.SetCursorPos(new Vector2(style.WindowPadding.X, style.WindowPadding.Y));
            var dragSize = new Vector2(ImGui.GetContentRegionAvail().X, chromeHeight);
            ImGui.InvisibleButton($"##drag_zone_dc_{id}", dragSize);
            if (ImGui.IsItemActive() && ImGui.IsMouseDragging(0))
            {
                var io = ImGui.GetIO();
                ImGui.SetWindowPos(ImGui.GetWindowPos() + io.MouseDelta);
            }

            ImGui.Dummy(new Vector2(1f, style.FramePadding.Y + chromeHeight));
            drawContent();
            windowInteracted = HasWindowInteraction();
        }
        ImGui.End();
        ImGui.PopStyleVar();

        if (pushedColors > 0)
        {
            ImGui.PopStyleColor(pushedColors);
        }
        if (styleVarPushed)
        {
            ImGui.PopStyleVar();
        }

        if (!openRef || closeRequested || UiTheme.RequestCloseThisFrame)
        {
            ForceWindowClosed(id);
        }

        return windowInteracted;
    }

    public void OnAppearanceSettingsChanged()
    {
        _styleNeedsUpdate = true;
    }

    public void ResetEventCreateRoles()
    {
        _create.ResetRoles();
    }

    public void ReloadSignupPresets()
    {
        SignupPresetService.Reset();
        _ = SignupPresetService.EnsureLoaded(_httpClient, _config);
    }

    public void ResetFadeTimer()
    {
        _timeSinceLastInteraction = 0f;
        _fadeAlpha = 1f;
    }

    private float GetCurrentFadeAlpha()
    {
        if (!_config.ChatFadeOutEnabled)
        {
            return 1f;
        }

        var minAlpha = Math.Clamp(_config.ChatFadeOutMinimumAlpha, 0f, 1f);
        return Math.Clamp(_fadeAlpha, minAlpha, 1f);
    }

    private void UpdateFadeState(bool interacted, float deltaTime)
    {
        if (!_config.ChatFadeOutEnabled)
        {
            ResetFadeTimer();
            return;
        }

        if (interacted)
        {
            ResetFadeTimer();
            return;
        }

        if (deltaTime <= 0f)
        {
            return;
        }

        var minAlpha = Math.Clamp(_config.ChatFadeOutMinimumAlpha, 0f, 1f);
        if (minAlpha >= 1f - FadeAlphaTolerance)
        {
            ResetFadeTimer();
            return;
        }

        var duration = Math.Max(MinimumFadeDuration, (float)_config.ChatFadeOutDelaySeconds);
        _timeSinceLastInteraction = Math.Clamp(_timeSinceLastInteraction + deltaTime, 0f, duration);
        var progress = Math.Clamp(_timeSinceLastInteraction / duration, 0f, 1f);
        var targetAlpha = 1f - (1f - minAlpha) * progress;
        _fadeAlpha = Math.Clamp(targetAlpha, minAlpha, 1f);
    }

    private static bool HasWindowInteraction()
    {
        var hovered = ImGui.IsWindowHovered(ImGuiHoveredFlags.RootAndChildWindows | ImGuiHoveredFlags.AllowWhenBlockedByActiveItem | ImGuiHoveredFlags.AllowWhenBlockedByPopup);
        var hasActiveItem = ImGui.IsAnyItemActive() || ImGui.IsAnyItemFocused();
        var focused = hasActiveItem && ImGui.IsWindowFocused(ImGuiFocusedFlags.RootAndChildWindows);
        return hovered || focused;
    }

    private void SaveConfig()
    {
        PluginServices.Instance!.PluginInterface.SavePluginConfig(_config);
    }

    public void Dispose()
    {
        if (Instance == this)
        {
            Instance = null;
        }

        foreach (var item in _dockItems)
        {
            if (item.Texture?.GetWrapOrEmpty() is IDisposable wrap)
            {
                try
                {
                    wrap.Dispose();
                }
                catch
                {
                    // Ignore texture disposal failures.
                }
            }

            item.Texture = null;
        }
    }

    private void ApplyAccentColors()
    {
        var style = ImGui.GetStyle();
        var accent = Config.SanitizeColor(_config.SecondaryAccentColor, Config.DefaultSecondaryAccentColor);
        var accentHovered = AdjustBrightness(accent, 1.1f);
        var accentActive = AdjustBrightness(accent, 1.2f);

        style.Colors[(int)ImGuiCol.ScrollbarGrab] = accent;
        style.Colors[(int)ImGuiCol.ScrollbarGrabHovered] = accentHovered;
        style.Colors[(int)ImGuiCol.ScrollbarGrabActive] = accentActive;
        style.Colors[(int)ImGuiCol.Separator] = accent;
        style.Colors[(int)ImGuiCol.SeparatorHovered] = accentHovered;
        style.Colors[(int)ImGuiCol.SeparatorActive] = accentActive;
    }

    private static int PushWindowOpacityStyles(
        Vector4 primaryColor,
        Vector4 childBaseColor,
        Vector4 tabBaseColor,
        Vector4 tabActiveBaseColor,
        Vector4 tabHoveredBaseColor,
        float alpha)
    {
        ImGui.PushStyleColor(ImGuiCol.WindowBg, WithAlpha(primaryColor, alpha));
        ImGui.PushStyleColor(ImGuiCol.ChildBg, WithAlpha(childBaseColor, alpha));
        ImGui.PushStyleColor(ImGuiCol.Tab, WithAlpha(tabBaseColor, alpha));
        ImGui.PushStyleColor(ImGuiCol.TabActive, WithAlpha(tabActiveBaseColor, alpha));
        ImGui.PushStyleColor(ImGuiCol.TabHovered, WithAlpha(tabHoveredBaseColor, alpha));
        return 5;
    }

    private static Vector4 AdjustBrightness(Vector4 color, float factor)
    {
        return new Vector4(
            Math.Clamp(color.X * factor, 0f, 1f),
            Math.Clamp(color.Y * factor, 0f, 1f),
            Math.Clamp(color.Z * factor, 0f, 1f),
            color.W);
    }

    private static Vector4 WithAlpha(Vector4 color, float alphaMultiplier)
    {
        return new Vector4(color.X, color.Y, color.Z, Math.Clamp(color.W * alphaMultiplier, 0f, 1f));
    }

    private static void DrawRoundedVerticalGradient(
        ImDrawListPtr drawList,
        Vector2 min,
        Vector2 max,
        Vector4 topColor,
        Vector4 bottomColor,
        float rounding)
    {
        if (max.X <= min.X || max.Y <= min.Y)
        {
            return;
        }

        var height = max.Y - min.Y;
        var segments = Math.Clamp((int)MathF.Ceiling(height / 4f), 4, 64);
        for (var i = 0; i < segments; i++)
        {
            var t0 = (float)i / segments;
            var t1 = (float)(i + 1) / segments;
            var y0 = min.Y + height * t0;
            var y1 = min.Y + height * t1;
            var segmentMin = new Vector2(min.X, y0);
            var segmentMax = new Vector2(max.X, i == segments - 1 ? max.Y : y1);
            var color = Vector4.Lerp(topColor, bottomColor, (t0 + t1) * 0.5f);
            var colorU32 = ImGui.GetColorU32(color);

            var segmentRounding = 0f;
            var flags = ImDrawFlags.RoundCornersNone;
            if (i == 0 && rounding > 0f)
            {
                segmentRounding = rounding;
                flags = ImDrawFlags.RoundCornersTop;
            }
            else if (i == segments - 1 && rounding > 0f)
            {
                segmentRounding = rounding;
                flags = ImDrawFlags.RoundCornersBottom;
            }

            drawList.AddRectFilled(segmentMin, segmentMax, colorU32, segmentRounding, flags);
        }
    }

}
