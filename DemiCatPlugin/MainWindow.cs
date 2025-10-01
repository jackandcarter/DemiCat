using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Textures;
using Dalamud.Interface.Textures.TextureWraps;
using DemiCatPlugin.Emoji;

namespace DemiCatPlugin;

public class MainWindow : IDisposable
{
    private const float BaseIconSize = 42f;
    private const float BasePadding = 10f;
    private const float BaseSpacing = 8f;
    private const float IndicatorRadius = 4f;
    private const string DockWindowTitle = "DemiCat Dock";
    private const string DockDragPayloadType = "DemiCatDockItem";

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
    private readonly HttpClient _httpClient;
    private readonly List<DockItem> _dockItems = new();
    private string? _draggingDockItemId;
    private readonly HashSet<string> _autoShownDockItems = new();

    private readonly EventsDockableWindow _eventsWindowHost;
    private readonly EventCreateDockableWindow _eventCreateWindowHost;
    private readonly TemplatesDockableWindow _templatesWindowHost;
    private readonly NotePadDockableWindow _notePadWindowHost;
    private readonly RequestBoardDockableWindow _requestBoardWindowHost;
    private readonly OfficerChatDockableWindow _officerWindowHost;
    private readonly List<DockableWindow> _windowHosts = new();
    private readonly ChatDockableWindow? _chatWindowHost;
    private SyncshellDockableWindow? _syncshellWindowHost;

    private ISharedImmediateTexture? _dockIconTexture;
    private SyncshellWindow? _syncshell;
    private bool _syncshellEnabled;
    private bool _styleNeedsUpdate = true;
    private bool _hasOfficerAccess;
    private bool _isOpen;

    public bool HasOfficerAccess
    {
        get => _hasOfficerAccess;
        set
        {
            if (_hasOfficerAccess == value)
                return;

            _hasOfficerAccess = value;
            if (!value)
            {
                _officerWindowHost.IsOpen = false;
            }

            BuildDockItems();
        }
    }

    public bool IsOpen
    {
        get => _isOpen;
        set
        {
            if (_isOpen == value)
                return;

            _isOpen = value;
            if (_config.DockVisible != value)
            {
                _config.DockVisible = value;
                SaveConfig();
            }

            if (!value)
            {
                CloseAllFeatureWindows();
            }
        }
    }

    public UiRenderer Ui => _ui;
    public EventCreateWindow EventCreateWindow => _create;
    public TemplatesWindow TemplatesWindow => _templates;
    public NotePadWindow NotePadWindow => _notePad;
    public EventsDockableWindow EventsWindowHost => _eventsWindowHost;
    public EventCreateDockableWindow EventCreateWindowHost => _eventCreateWindowHost;
    public TemplatesDockableWindow TemplatesWindowHost => _templatesWindowHost;
    public NotePadDockableWindow NotePadWindowHost => _notePadWindowHost;
    public RequestBoardDockableWindow RequestBoardWindowHost => _requestBoardWindowHost;
    public ChatDockableWindow? ChatWindowHost => _chatWindowHost;
    public OfficerChatDockableWindow OfficerWindowHost => _officerWindowHost;
    public SyncshellDockableWindow? SyncshellWindowHost => _syncshellWindowHost;
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
        _syncshellEnabled = config.FCSyncShell;
        _syncshell = _syncshellEnabled ? new SyncshellWindow(config, httpClient) : null;
        _isOpen = config.DockVisible;

        _eventsWindowHost = new EventsDockableWindow(config, ui, IsLinked);
        _eventCreateWindowHost = new EventCreateDockableWindow(config, _create, IsLinked);
        _templatesWindowHost = new TemplatesDockableWindow(config, _templates, IsLinked);
        _notePadWindowHost = new NotePadDockableWindow(config, _notePad);
        _requestBoardWindowHost = new RequestBoardDockableWindow(config, _requestBoard, IsLinked);
        _officerWindowHost = new OfficerChatDockableWindow(config, _officer, IsLinked, () => HasOfficerAccess);

        _windowHosts.Add(_eventsWindowHost);
        _windowHosts.Add(_eventCreateWindowHost);
        _windowHosts.Add(_templatesWindowHost);
        _windowHosts.Add(_notePadWindowHost);
        _windowHosts.Add(_requestBoardWindowHost);
        _windowHosts.Add(_officerWindowHost);

        if (_chat != null)
        {
            var chatTitle = _chat is FcChatWindow ? "DemiCat FC Chat" : "DemiCat Chat";
            var linkPrompt = _chat is FcChatWindow ? "Link DemiCat to use FC chat." : "Link DemiCat to use chat.";
            Func<float> opacityProvider = _chat is FcChatWindow
                ? () => _config.FcChatOpacity
                : () => 1f;

            _chatWindowHost = new ChatDockableWindow(
                _config,
                chatTitle,
                _chat,
                IsLinked,
                opacityProvider,
                linkPrompt);
            _windowHosts.Add(_chatWindowHost);
        }

        if (_syncshell != null)
        {
            _syncshellWindowHost = new SyncshellDockableWindow(_config, _syncshell, IsLinked);
            _windowHosts.Add(_syncshellWindowHost);
        }

        Instance = this;

        EnsureDockIconTexture();
        BuildDockItems();
    }

    public void SetNotePadReadOnly(bool isReadOnly)
    {
        _notePad.IsReadOnly = isReadOnly;
    }

    internal void UpdateSyncshell()
    {
        if (_syncshellEnabled == _config.FCSyncShell)
            return;

        _syncshellEnabled = _config.FCSyncShell;
        if (_syncshellEnabled)
        {
            _syncshell?.Dispose();
            _syncshell = new SyncshellWindow(_config, _httpClient);
            _syncshellWindowHost = new SyncshellDockableWindow(_config, _syncshell, IsLinked);
            _windowHosts.Add(_syncshellWindowHost);
            BuildDockItems();
            PluginServices.Instance?.Framework.RunOnTick(() => { });
        }
        else
        {
            _syncshell?.Dispose();
            _syncshell = null;
            if (_syncshellWindowHost != null)
            {
                _syncshellWindowHost.IsOpen = false;
                _windowHosts.Remove(_syncshellWindowHost);
                _syncshellWindowHost = null;
            }

            BuildDockItems();
        }
    }

    public void Draw()
    {
        if (!IsLinked())
        {
            CloseAllFeatureWindows();
            return;
        }

        UpdateSyncshell();

        if (_styleNeedsUpdate)
        {
            ApplyAccentColors();
            _styleNeedsUpdate = false;
        }

        if (!IsOpen)
        {
            if (_config.DockVisible)
            {
                _config.DockVisible = false;
                SaveConfig();
            }

            return;
        }

        if (!_config.DockVisible)
        {
            _config.DockVisible = true;
            SaveConfig();
        }

        var visibleItems = _dockItems.Where(i => i.IsVisible()).ToList();
        if (visibleItems.Count == 0)
        {
            return;
        }

        var iconScale = GetIconScale();
        var padding = GetPadding(iconScale);
        var spacing = GetSpacing(iconScale);
        var iconSize = new Vector2(BaseIconSize * iconScale, BaseIconSize * iconScale);
        var indicatorHeight = IndicatorRadius * 2f * iconScale + spacing * 0.75f;
        var iconAreaWidth = iconSize.X * visibleItems.Count + spacing * Math.Max(0, visibleItems.Count - 1);
        var iconAreaHeight = iconSize.Y;
        var totalWidth = iconAreaWidth + padding * 2f;
        var totalHeight = iconAreaHeight + padding * 2f + indicatorHeight;
        var dockSize = new Vector2(totalWidth, totalHeight);

        var dockPosition = GetDockPosition(dockSize);

        ImGui.SetNextWindowPos(dockPosition, ImGuiCond.Appearing);
        ImGui.SetNextWindowSize(dockSize, ImGuiCond.Appearing);

        var windowFlags = ImGuiWindowFlags.NoDecoration
            | ImGuiWindowFlags.NoScrollbar
            | ImGuiWindowFlags.NoCollapse
            | ImGuiWindowFlags.NoSavedSettings
            | ImGuiWindowFlags.AlwaysAutoResize;

        if (_config.DockLocked)
        {
            windowFlags |= ImGuiWindowFlags.NoMove;
        }

        var dockBgColor = GetDockBackgroundColor();
        var stripColor = GetDockStripColor(dockBgColor);
        var accentColor = Config.SanitizeColor(_config.SecondaryAccentColor, Config.DefaultSecondaryAccentColor);

        ImGui.PushStyleVar(ImGuiStyleVar.WindowRounding, MathF.Max(14f, padding * 1.5f));
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new Vector2(padding, padding));
        ImGui.PushStyleColor(ImGuiCol.WindowBg, dockBgColor);
        ImGui.PushStyleColor(ImGuiCol.Border, Vector4.Zero);

        var open = true;
        var reordered = false;
        if (ImGui.Begin(DockWindowTitle, ref open, windowFlags))
        {
            reordered = DrawDockStrip(visibleItems, iconSize, spacing, indicatorHeight, stripColor, accentColor);
            DrawDockContextMenu();
        }
        var windowPos = ImGui.GetWindowPos();
        ImGui.End();

        ImGui.PopStyleColor(2);
        ImGui.PopStyleVar(2);

        if (!open)
        {
            IsOpen = false;
        }

        if (_config.DockRememberPosition && !_config.DockLocked && !ImGui.IsMouseDown(ImGuiMouseButton.Left))
        {
            if (!_config.DockPositionInitialized || Vector2.Distance(windowPos, _config.DockPosition) > 0.5f)
            {
                _config.DockPosition = windowPos;
                _config.DockPositionInitialized = true;
                SaveConfig();
            }
        }

        if (reordered)
        {
            visibleItems = _dockItems.Where(i => i.IsVisible()).ToList();
        }

        DrawFeatureWindows();
    }

    public void OnAppearanceSettingsChanged()
    {
        _styleNeedsUpdate = true;
        BuildDockItems();
        foreach (var host in _windowHosts)
        {
            host.OnAppearanceSettingsChanged();
        }
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
        foreach (var window in _windowHosts)
        {
            if (window.SupportsFade)
            {
                window.ResetFadeTimer();
            }
        }
    }

    public void RefreshDockAutoShow()
    {
        _autoShownDockItems.Clear();
    }

    public void ResetDockPosition()
    {
        _config.DockPositionInitialized = false;
        _config.DockPosition = Vector2.Zero;
        SaveConfig();
    }

    public void Dispose()
    {
        if (Instance == this)
        {
            Instance = null;
        }

        try
        {
            (_dockIconTexture?.GetWrapOrEmpty() as IDisposable)?.Dispose();
        }
        catch
        {
            // Suppress disposal failures; Dalamud will clean these up on exit.
        }

        _dockIconTexture = null;
        _syncshell?.Dispose();
    }

    private void CloseAllFeatureWindows()
    {
        foreach (var window in _windowHosts)
        {
            window.IsOpen = false;
        }
    }

    private bool DrawDockStrip(
        IReadOnlyList<DockItem> items,
        Vector2 iconSize,
        float spacing,
        float indicatorHeight,
        Vector4 stripColor,
        Vector4 indicatorColor)
    {
        var drawList = ImGui.GetWindowDrawList();
        var iconStart = ImGui.GetCursorScreenPos();
        var iconAreaSize = new Vector2(
            iconSize.X * items.Count + spacing * Math.Max(0, items.Count - 1),
            iconSize.Y);

        var stripMin = iconStart - new Vector2(spacing * 0.75f, spacing * 0.75f);
        var stripMax = iconStart + iconAreaSize + new Vector2(spacing * 0.75f, indicatorHeight + spacing * 0.75f);
        drawList.AddRectFilled(stripMin, stripMax, ImGui.ColorConvertFloat4ToU32(stripColor), MathF.Max(spacing * 2f, 18f));

        ImGui.SetCursorScreenPos(iconStart);

        var reordered = false;
        for (var i = 0; i < items.Count; i++)
        {
            if (i > 0)
            {
                ImGui.SameLine(0f, spacing);
            }

            var item = items[i];
            var enabled = item.IsEnabled();

            ImGui.PushID(item.Id);

            if (!enabled)
            {
                ImGui.BeginDisabled();
            }

            var clicked = DrawDockButton(item, iconSize);

            if (!enabled)
            {
                ImGui.EndDisabled();
            }

            if (clicked && enabled)
            {
                item.SetIsOpen(!item.GetIsOpen());
            }

            if (ImGui.IsItemHovered())
            {
                ImGui.SetTooltip(item.Tooltip);
            }

            if (ImGui.BeginDragDropSource())
            {
                _draggingDockItemId = item.Id;
                ImGui.SetDragDropPayload(DockDragPayloadType, ReadOnlySpan<byte>.Empty);
                ImGui.TextUnformatted(item.Tooltip);
                ImGui.EndDragDropSource();
            }

            if (ImGui.BeginDragDropTarget())
            {
                var payload = ImGui.AcceptDragDropPayload(DockDragPayloadType);
                if (!payload.Equals(default(ImGuiPayloadPtr)))
                {
                    if (!string.IsNullOrEmpty(_draggingDockItemId) && !string.Equals(_draggingDockItemId, item.Id, StringComparison.Ordinal))
                    {
                        var sourceIndex = _dockItems.FindIndex(d => string.Equals(d.Id, _draggingDockItemId, StringComparison.Ordinal));
                        var targetIndex = _dockItems.FindIndex(d => string.Equals(d.Id, item.Id, StringComparison.Ordinal));

                        if (sourceIndex >= 0 && targetIndex >= 0)
                        {
                            var movedItem = _dockItems[sourceIndex];
                            _dockItems.RemoveAt(sourceIndex);
                            if (sourceIndex < targetIndex)
                            {
                                targetIndex--;
                            }
                            _dockItems.Insert(targetIndex, movedItem);

                            _config.DockOrder = _dockItems.Select(d => d.Id).ToList();
                            SaveConfig();
                            reordered = true;
                        }
                    }

                    _draggingDockItemId = null;
                }

                ImGui.EndDragDropTarget();
            }

            if (item.GetIsOpen())
            {
                var rectMin = ImGui.GetItemRectMin();
                var rectMax = ImGui.GetItemRectMax();
                var center = new Vector2((rectMin.X + rectMax.X) * 0.5f, rectMax.Y + IndicatorRadius * ImGui.GetIO().FontGlobalScale * 0.5f);
                var radius = IndicatorRadius * MathF.Max(1f, iconSize.X / BaseIconSize);
                center.Y += radius + spacing * 0.1f;
                drawList.AddCircleFilled(center, radius, ImGui.ColorConvertFloat4ToU32(indicatorColor));
            }

            ImGui.PopID();
        }

        ImGui.Dummy(new Vector2(0f, indicatorHeight));
        return reordered;
    }

    private bool DrawDockButton(DockItem item, Vector2 iconSize)
    {
        if (item.Icon != null)
        {
            var wrap = item.Icon.GetWrapOrEmpty();
            return ImGui.ImageButton(
                wrap.Handle,
                iconSize,
                Vector2.Zero,
                Vector2.One,
                0,
                Vector4.Zero,
                item.IconTint);
        }

        return ImGui.Button(item.Tooltip, iconSize);
    }

    private void DrawDockContextMenu()
    {
        if (!ImGui.BeginPopupContextWindow("DockContext", ImGuiPopupFlags.MouseButtonRight | ImGuiPopupFlags.NoOpenOverItems))
        {
            return;
        }

        var locked = _config.DockLocked;
        if (ImGui.MenuItem("Lock Position", default, locked))
        {
            _config.DockLocked = !_config.DockLocked;
            SaveConfig();
        }

        var remember = _config.DockRememberPosition;
        if (ImGui.MenuItem("Remember Position", default, remember))
        {
            var newValue = !_config.DockRememberPosition;
            _config.DockRememberPosition = newValue;
            if (newValue)
            {
                ResetDockPosition();
            }
            else
            {
                _config.DockPositionInitialized = false;
                _config.DockPosition = Vector2.Zero;
                SaveConfig();
            }
        }

        var iconScaleValue = _config.DockIconScale;
        if (ImGui.SliderFloat("Icon Scale", ref iconScaleValue, Config.MinDockIconScale, Config.MaxDockIconScale, "%.2f"))
        {
            _config.DockIconScale = Config.SanitizeDockIconScale(iconScaleValue);
            SaveConfig();
        }

        var dockColor = _config.DockBackgroundColor;
        if (ImGui.ColorEdit4("Background Color", ref dockColor, ImGuiColorEditFlags.NoInputs | ImGuiColorEditFlags.AlphaBar))
        {
            var sanitized = Config.SanitizeDockBackgroundColor(dockColor);
            if (!ColorsAlmostEqual(sanitized, _config.DockBackgroundColor))
            {
                _config.DockBackgroundColor = sanitized;
                _config.DockBackgroundAlpha = sanitized.W;
                SaveConfig();
            }
        }

        if (ImGui.MenuItem("Show Settings", default, _settings.IsOpen))
        {
            _settings.IsOpen = !_settings.IsOpen;
        }

        ImGui.EndPopup();
    }

    private bool IsLinked()
        => TokenManager.Instance?.State == LinkState.Linked;

    private float GetIconScale()
    {
        var scale = Config.SanitizeDockIconScale(_config.DockIconScale);
        if (Math.Abs(scale - _config.DockIconScale) > float.Epsilon)
        {
            _config.DockIconScale = scale;
            SaveConfig();
        }

        return scale;
    }

    private float GetPadding(float iconScale)
        => BasePadding * iconScale;

    private float GetSpacing(float iconScale)
        => BaseSpacing * iconScale;

    private Vector2 GetDockPosition(Vector2 dockSize)
    {
        if (_config.DockRememberPosition)
        {
            EnsureDockPositionInitialized(dockSize);
            return _config.DockPosition;
        }

        return CalculateDefaultDockPosition(dockSize);
    }

    private Vector4 GetDockBackgroundColor()
    {
        var sanitized = Config.SanitizeDockBackgroundColor(_config.DockBackgroundColor);
        var changed = false;
        if (!ColorsAlmostEqual(sanitized, _config.DockBackgroundColor))
        {
            _config.DockBackgroundColor = sanitized;
            changed = true;
        }

        if (Math.Abs(sanitized.W - _config.DockBackgroundAlpha) > 0.0001f)
        {
            _config.DockBackgroundAlpha = sanitized.W;
            changed = true;
        }

        if (changed)
        {
            SaveConfig();
        }

        return sanitized;
    }

    private Vector4 GetDockStripColor(Vector4 background)
    {
        var strip = DockableWindow.AdjustBrightness(background, 1.1f);
        strip.W = background.W;
        return strip;
    }

    private static bool ColorsAlmostEqual(Vector4 a, Vector4 b)
        => Vector4.DistanceSquared(a, b) <= 0.0001f;

    private void EnsureDockIconTexture()
    {
        if (_dockIconTexture != null)
            return;

        IDalamudTextureWrap? wrap = null;
        try
        {
            var provider = PluginServices.Instance?.TextureProvider;
            if (provider == null)
                return;

            var pixel = new byte[] { 255, 255, 255, 255 };
            var createdWrap = provider.CreateFromRaw(RawImageSpecification.Rgba32(1, 1), pixel);
            wrap = createdWrap;
            _dockIconTexture = new ForwardingSharedImmediateTexture(createdWrap);
            wrap = null;
        }
        catch
        {
            _dockIconTexture = null;
            wrap?.Dispose();
        }
    }

    private void BuildDockItems()
    {
        _dockItems.Clear();
        var defaultDockOrder = new List<string>();

        void AddDockItem(DockItem item)
        {
            _dockItems.Add(item);
            defaultDockOrder.Add(item.Id);
        }
        var accent = Config.SanitizeColor(_config.SecondaryAccentColor, Config.DefaultSecondaryAccentColor);
        var mutedAccent = DockableWindow.AdjustBrightness(accent, 0.85f);
        var positive = new Vector4(0.35f, 0.75f, 1f, 1f);
        var warning = new Vector4(1f, 0.62f, 0.2f, 1f);
        var neutral = new Vector4(0.8f, 0.8f, 0.8f, 1f);

        AddDockItem(new DockItem(
            "events",
            _dockIconTexture,
            accent,
            "Events",
            () => _config.Events,
            () => IsLinked(),
            () => _eventsWindowHost.IsOpen,
            v => _eventsWindowHost.IsOpen = v,
            _eventsWindowHost.Draw));

        AddDockItem(new DockItem(
            "create",
            _dockIconTexture,
            positive,
            "Create Event",
            () => _config.Events,
            () => IsLinked(),
            () => _eventCreateWindowHost.IsOpen,
            v => _eventCreateWindowHost.IsOpen = v,
            _eventCreateWindowHost.Draw));

        AddDockItem(new DockItem(
            "templates",
            _dockIconTexture,
            mutedAccent,
            "Templates",
            () => _config.Templates,
            () => IsLinked(),
            () => _templatesWindowHost.IsOpen,
            v => _templatesWindowHost.IsOpen = v,
            _templatesWindowHost.Draw));

        AddDockItem(new DockItem(
            "notepad",
            _dockIconTexture,
            neutral,
            "NotePad",
            () => true,
            () => _config.NotePadEnabled,
            () => _notePadWindowHost.IsOpen,
            v => _notePadWindowHost.IsOpen = v,
            _notePadWindowHost.Draw));

        AddDockItem(new DockItem(
            "requests",
            _dockIconTexture,
            warning,
            "Request Board",
            () => _config.Requests,
            () => IsLinked(),
            () => _requestBoardWindowHost.IsOpen,
            v => _requestBoardWindowHost.IsOpen = v,
            _requestBoardWindowHost.Draw));

        var chat = _chat;
        if (_chatWindowHost != null && chat != null)
        {
            AddDockItem(new DockItem(
                "chat",
                _dockIconTexture,
                accent,
                chat is FcChatWindow ? "FC Chat" : "Chat",
                () => true,
                () => IsLinked(),
                () => _chatWindowHost.IsOpen,
                v => _chatWindowHost.IsOpen = v,
                _chatWindowHost.Draw));
        }

        AddDockItem(new DockItem(
            "officer",
            _dockIconTexture,
            positive,
            "Officer Chat",
            () => HasOfficerAccess,
            () => HasOfficerAccess && IsLinked(),
            () => _officerWindowHost.IsOpen,
            v => _officerWindowHost.IsOpen = v,
            _officerWindowHost.Draw));

        AddDockItem(new DockItem(
            "syncshell",
            _dockIconTexture,
            accent,
            "Syncshell",
            () => _config.FCSyncShell && _syncshellWindowHost != null,
            () => _config.FCSyncShell && _syncshellWindowHost != null && IsLinked(),
            () => _syncshellWindowHost?.IsOpen ?? false,
            v =>
            {
                if (_syncshellWindowHost != null)
                {
                    _syncshellWindowHost.IsOpen = v;
                }
            },
            () => _syncshellWindowHost?.Draw()));

        AddDockItem(new DockItem(
            "settings",
            _dockIconTexture,
            neutral,
            "Settings",
            () => true,
            () => true,
            () => _settings.IsOpen,
            v => _settings.IsOpen = v,
            () => { }));

        var storedOrder = _config.DockOrder ?? new List<string>();
        var knownIds = new HashSet<string>(defaultDockOrder);
        var seen = new HashSet<string>();
        var sanitizedOrder = new List<string>();

        foreach (var id in storedOrder)
        {
            if (string.IsNullOrWhiteSpace(id))
            {
                continue;
            }

            var trimmed = id.Trim();
            if (!knownIds.Contains(trimmed))
            {
                continue;
            }

            if (seen.Add(trimmed))
            {
                sanitizedOrder.Add(trimmed);
            }
        }

        foreach (var id in defaultDockOrder)
        {
            if (seen.Add(id))
            {
                sanitizedOrder.Add(id);
            }
        }

        if (_config.DockOrder == null || !_config.DockOrder.SequenceEqual(sanitizedOrder))
        {
            _config.DockOrder = sanitizedOrder;
            SaveConfig();
        }

        var orderLookup = sanitizedOrder
            .Select((id, index) => (id, index))
            .ToDictionary(pair => pair.id, pair => pair.index);

        _dockItems.Sort((left, right) =>
        {
            var leftIndex = orderLookup.TryGetValue(left.Id, out var li) ? li : int.MaxValue;
            var rightIndex = orderLookup.TryGetValue(right.Id, out var ri) ? ri : int.MaxValue;
            return leftIndex.CompareTo(rightIndex);
        });

        _autoShownDockItems.RemoveWhere(id => _dockItems.All(item => item.Id != id));
    }

    private void DrawFeatureWindows()
    {
        EnsureAutoShownWindows();
        foreach (var item in _dockItems)
        {
            if (!item.IsVisible() && item.GetIsOpen())
            {
                item.SetIsOpen(false);
                continue;
            }

            if (item.GetIsOpen())
            {
                item.DrawWindow();
            }
        }
    }

    private void EnsureAutoShownWindows()
    {
        foreach (var item in _dockItems)
        {
            if (!_config.GetDockAutoShow(item.Id))
            {
                continue;
            }

            if (_autoShownDockItems.Contains(item.Id))
            {
                continue;
            }

            if (!item.IsVisible() || !item.IsEnabled())
            {
                continue;
            }

            item.SetIsOpen(true);
            _autoShownDockItems.Add(item.Id);
        }
    }

    private void EnsureDockPositionInitialized(Vector2 dockSize)
    {
        if (!_config.DockRememberPosition || _config.DockPositionInitialized)
        {
            return;
        }

        _config.DockPosition = CalculateDefaultDockPosition(dockSize);
        _config.DockPositionInitialized = true;
        SaveConfig();
    }

    private Vector2 CalculateDefaultDockPosition(Vector2 dockSize)
    {
        var viewport = ImGui.GetMainViewport();
        var available = viewport.WorkSize;
        return new Vector2(
            viewport.WorkPos.X + (available.X - dockSize.X) * 0.5f,
            viewport.WorkPos.Y + available.Y - dockSize.Y - 30f);
    }

    private void SaveConfig()
    {
        PluginServices.Instance!.PluginInterface.SavePluginConfig(_config);
    }

    private void ApplyAccentColors()
    {
        var style = ImGui.GetStyle();
        var accent = Config.SanitizeColor(_config.SecondaryAccentColor, Config.DefaultSecondaryAccentColor);
        var accentHovered = DockableWindow.AdjustBrightness(accent, 1.1f);
        var accentActive = DockableWindow.AdjustBrightness(accent, 1.2f);

        style.Colors[(int)ImGuiCol.ScrollbarGrab] = accent;
        style.Colors[(int)ImGuiCol.ScrollbarGrabHovered] = accentHovered;
        style.Colors[(int)ImGuiCol.ScrollbarGrabActive] = accentActive;
        style.Colors[(int)ImGuiCol.Separator] = accent;
        style.Colors[(int)ImGuiCol.SeparatorHovered] = accentHovered;
        style.Colors[(int)ImGuiCol.SeparatorActive] = accentActive;
    }
}
