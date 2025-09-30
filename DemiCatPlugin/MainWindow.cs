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

    private ISharedImmediateTexture? _dockIconTexture;
    private SyncshellWindow? _syncshell;
    private bool _syncshellEnabled;
    private bool _templatesActive;
    private bool _styleNeedsUpdate = true;
    private bool _hasOfficerAccess;
    private bool _isOpen;

    private bool _eventsOpen;
    private bool _createOpen;
    private bool _templatesOpen;
    private bool _notePadOpen;
    private bool _requestsOpen;
    private bool _chatOpen;
    private bool _officerOpen;
    private bool _syncshellOpen;

    public bool HasOfficerAccess
    {
        get => _hasOfficerAccess;
        set
        {
            if (_hasOfficerAccess == value)
                return;

            _hasOfficerAccess = value;
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
        _templatesActive = false;
        _isOpen = config.DockVisible;

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
            _syncshell = new SyncshellWindow(_config, _httpClient);
            PluginServices.Instance?.Framework.RunOnTick(() => { });
        }
        else
        {
            _syncshell?.Dispose();
            _syncshell = null;
            _syncshellOpen = false;
        }
    }

    public void Draw()
    {
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

        EnsureDockPositionInitialized(dockSize);

        ImGui.SetNextWindowPos(_config.DockPosition, ImGuiCond.Appearing);
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
        if (ImGui.Begin(DockWindowTitle, ref open, windowFlags))
        {
            DrawDockStrip(visibleItems, iconSize, spacing, indicatorHeight, stripColor, accentColor);
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

        if (!_config.DockLocked && !ImGui.IsMouseDown(ImGuiMouseButton.Left))
        {
            if (!_config.DockPositionInitialized || Vector2.Distance(windowPos, _config.DockPosition) > 0.5f)
            {
                _config.DockPosition = windowPos;
                _config.DockPositionInitialized = true;
                SaveConfig();
            }
        }

        DrawFeatureWindows();
    }

    public void OnAppearanceSettingsChanged()
    {
        _styleNeedsUpdate = true;
        BuildDockItems();
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
        // The dock UI no longer fades, but keep the method for compatibility with existing callers.
    }

    public void Dispose()
    {
        if (Instance == this)
        {
            Instance = null;
        }

        _dockIconTexture?.Dispose();
        _syncshell?.Dispose();
    }

    private void CloseAllFeatureWindows()
    {
        _eventsOpen = false;
        _createOpen = false;
        _templatesOpen = false;
        _notePadOpen = false;
        _requestsOpen = false;
        _chatOpen = false;
        _officerOpen = false;
        _syncshellOpen = false;
    }

    private void DrawDockStrip(
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

        for (var i = 0; i < items.Count; i++)
        {
            if (i > 0)
            {
                ImGui.SameLine(0f, spacing);
            }

            var item = items[i];
            var enabled = item.IsEnabled();

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

            if (item.GetIsOpen())
            {
                var rectMin = ImGui.GetItemRectMin();
                var rectMax = ImGui.GetItemRectMax();
                var center = new Vector2((rectMin.X + rectMax.X) * 0.5f, rectMax.Y + IndicatorRadius * ImGui.GetIO().FontGlobalScale * 0.5f);
                var radius = IndicatorRadius * MathF.Max(1f, iconSize.X / BaseIconSize);
                center.Y += radius + spacing * 0.1f;
                drawList.AddCircleFilled(center, radius, ImGui.ColorConvertFloat4ToU32(indicatorColor));
            }
        }

        ImGui.Dummy(new Vector2(0f, indicatorHeight));
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
        if (ImGui.MenuItem("Lock Position", null, locked))
        {
            _config.DockLocked = !_config.DockLocked;
            SaveConfig();
        }

        var iconScaleValue = _config.DockIconScale;
        if (ImGui.SliderFloat("Icon Scale", ref iconScaleValue, Config.MinDockIconScale, Config.MaxDockIconScale, "%.2f"))
        {
            _config.DockIconScale = Config.SanitizeDockIconScale(iconScaleValue);
            SaveConfig();
        }

        var alpha = _config.DockBackgroundAlpha;
        if (ImGui.SliderFloat("Background Alpha", ref alpha, 0.2f, 1f, "%.2f"))
        {
            _config.DockBackgroundAlpha = Math.Clamp(alpha, 0f, 1f);
            SaveConfig();
        }

        if (ImGui.MenuItem("Show Settings", null, _settings.IsOpen))
        {
            _settings.IsOpen = !_settings.IsOpen;
        }

        ImGui.EndPopup();
    }

    private void DrawEventsWindow()
    {
        if (!_eventsOpen)
            return;

        var open = _eventsOpen;
        var colorsPushed = PushContentWindowColors();
        if (ImGui.Begin("DemiCat Events", ref open, ImGuiWindowFlags.NoCollapse))
        {
            if (!IsLinked())
            {
                DrawLinkPrompt("Link DemiCat to view events.");
            }
            else
            {
                _ui.Draw();
            }
        }
        ImGui.End();
        ImGui.PopStyleColor(colorsPushed);
        _eventsOpen = open;
    }

    private void DrawCreateWindow()
    {
        if (!_createOpen)
            return;

        var open = _createOpen;
        var colorsPushed = PushContentWindowColors();
        if (ImGui.Begin("Create Event", ref open, ImGuiWindowFlags.NoCollapse))
        {
            if (!IsLinked())
            {
                DrawLinkPrompt("Link DemiCat to create events.");
            }
            else
            {
                _create.Draw();
            }
        }
        ImGui.End();
        ImGui.PopStyleColor(colorsPushed);
        _createOpen = open;
    }

    private void DrawTemplatesWindow()
    {
        if (!_templatesOpen)
            return;

        var open = _templatesOpen;
        var colorsPushed = PushContentWindowColors();
        if (ImGui.Begin("Templates", ref open, ImGuiWindowFlags.NoCollapse))
        {
            if (!IsLinked())
            {
                DrawLinkPrompt("Link DemiCat to use templates.");
                _templatesActive = false;
            }
            else
            {
                if (!_templatesActive)
                {
                    _templates.OnTabActivated();
                    _templatesActive = true;
                }

                _templates.Draw();
            }
        }
        ImGui.End();
        ImGui.PopStyleColor(colorsPushed);
        _templatesOpen = open;
        if (!open)
        {
            _templatesActive = false;
        }
    }

    private void DrawNotePadWindow()
    {
        if (!_notePadOpen)
            return;

        var open = _notePadOpen;
        var colorsPushed = PushContentWindowColors();
        if (ImGui.Begin("NotePad", ref open, ImGuiWindowFlags.NoCollapse))
        {
            if (!_config.NotePadEnabled)
            {
                ImGui.TextUnformatted("NotePad is disabled.");
            }
            else
            {
                _notePad.Draw();
            }
        }
        ImGui.End();
        ImGui.PopStyleColor(colorsPushed);
        _notePadOpen = open;
    }

    private void DrawRequestBoardWindow()
    {
        if (!_requestsOpen)
            return;

        var open = _requestsOpen;
        var colorsPushed = PushContentWindowColors();
        if (ImGui.Begin("Request Board", ref open, ImGuiWindowFlags.NoCollapse))
        {
            _requestBoard.Draw();
        }
        ImGui.End();
        ImGui.PopStyleColor(colorsPushed);
        _requestsOpen = open;
    }

    private void DrawChatWindow()
    {
        if (!_chatOpen || _chat == null)
            return;

        var open = _chatOpen;
        var colorsPushed = PushContentWindowColors();
        var label = _chat is FcChatWindow ? "FC Chat" : "Chat";
        if (ImGui.Begin($"DemiCat {label}", ref open, ImGuiWindowFlags.NoCollapse))
        {
            if (!IsLinked())
            {
                var tooltip = _chat is FcChatWindow ? "Link DemiCat to use FC chat." : "Link DemiCat to use chat.";
                DrawLinkPrompt(tooltip);
            }
            else
            {
                _chat.Draw();
            }
        }
        ImGui.End();
        ImGui.PopStyleColor(colorsPushed);
        _chatOpen = open;
    }

    private void DrawOfficerWindow()
    {
        if (!_officerOpen)
            return;

        var open = _officerOpen;
        var colorsPushed = PushContentWindowColors();
        if (ImGui.Begin("Officer Chat", ref open, ImGuiWindowFlags.NoCollapse))
        {
            if (!IsLinked())
            {
                DrawLinkPrompt("Link DemiCat to use officer chat.");
            }
            else if (!HasOfficerAccess)
            {
                ImGui.TextUnformatted("No officer access for this key.");
            }
            else
            {
                _officer.Draw();
            }
        }
        ImGui.End();
        ImGui.PopStyleColor(colorsPushed);
        _officerOpen = open;
    }

    private void DrawSyncshellWindow()
    {
        if (!_syncshellOpen)
            return;

        if (_syncshell == null)
        {
            _syncshellOpen = false;
            return;
        }

        var open = _syncshellOpen;
        var colorsPushed = PushContentWindowColors();
        if (ImGui.Begin("Syncshell", ref open, ImGuiWindowFlags.NoCollapse))
        {
            if (!IsLinked())
            {
                DrawLinkPrompt("Link DemiCat to use syncshell.");
            }
            else
            {
                _syncshell.Draw();
            }
        }
        ImGui.End();
        ImGui.PopStyleColor(colorsPushed);
        _syncshellOpen = open;
    }

    private void DrawLinkPrompt(string message)
    {
        ImGui.TextColored(new Vector4(1f, 0.85f, 0f, 1f), message);
        ImGui.SameLine();
        if (ImGui.Button("Open Settings"))
        {
            _settings.IsOpen = true;
        }
    }

    private bool IsLinked()
        => TokenManager.Instance?.State == LinkState.Linked;

    private int PushContentWindowColors()
    {
        var primary = Config.SanitizeColor(_config.PrimaryWindowColor, Config.DefaultPrimaryWindowColor);
        var child = AdjustBrightness(primary, 0.9f);
        ImGui.PushStyleColor(ImGuiCol.WindowBg, WithAlpha(primary, 1f));
        ImGui.PushStyleColor(ImGuiCol.ChildBg, WithAlpha(child, 1f));
        return 2;
    }

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

    private Vector4 GetDockBackgroundColor()
    {
        var color = Config.SanitizeColor(_config.PrimaryWindowColor, Config.DefaultPrimaryWindowColor);
        return WithAlpha(color, Math.Clamp(_config.DockBackgroundAlpha, 0.2f, 1f));
    }

    private Vector4 GetDockStripColor(Vector4 background)
    {
        var strip = AdjustBrightness(background, 1.1f);
        strip.W = background.W;
        return strip;
    }

    private void EnsureDockIconTexture()
    {
        if (_dockIconTexture != null)
            return;

        try
        {
            var provider = PluginServices.Instance?.TextureProvider;
            if (provider == null)
                return;

            var pixel = new byte[] { 255, 255, 255, 255 };
            _dockIconTexture = provider.CreateFromRaw(RawImageSpecification.Rgba32(1, 1), pixel);
        }
        catch
        {
            _dockIconTexture = null;
        }
    }

    private void BuildDockItems()
    {
        _dockItems.Clear();
        var accent = Config.SanitizeColor(_config.SecondaryAccentColor, Config.DefaultSecondaryAccentColor);
        var mutedAccent = AdjustBrightness(accent, 0.85f);
        var positive = new Vector4(0.35f, 0.75f, 1f, 1f);
        var warning = new Vector4(1f, 0.62f, 0.2f, 1f);
        var neutral = new Vector4(0.8f, 0.8f, 0.8f, 1f);

        _dockItems.Add(new DockItem(
            "events",
            _dockIconTexture,
            accent,
            "Events",
            () => _config.Events,
            () => IsLinked(),
            () => _eventsOpen,
            v => _eventsOpen = v,
            DrawEventsWindow));

        _dockItems.Add(new DockItem(
            "create",
            _dockIconTexture,
            positive,
            "Create Event",
            () => _config.Events,
            () => IsLinked(),
            () => _createOpen,
            v => _createOpen = v,
            DrawCreateWindow));

        _dockItems.Add(new DockItem(
            "templates",
            _dockIconTexture,
            mutedAccent,
            "Templates",
            () => _config.Templates,
            () => IsLinked(),
            () => _templatesOpen,
            v => _templatesOpen = v,
            DrawTemplatesWindow));

        _dockItems.Add(new DockItem(
            "notepad",
            _dockIconTexture,
            neutral,
            "NotePad",
            () => true,
            () => _config.NotePadEnabled,
            () => _notePadOpen,
            v => _notePadOpen = v,
            DrawNotePadWindow));

        _dockItems.Add(new DockItem(
            "requests",
            _dockIconTexture,
            warning,
            "Request Board",
            () => _config.Requests,
            () => IsLinked(),
            () => _requestsOpen,
            v => _requestsOpen = v,
            DrawRequestBoardWindow));

        if (_chat != null)
        {
            _dockItems.Add(new DockItem(
                "chat",
                _dockIconTexture,
                accent,
                _chat is FcChatWindow ? "FC Chat" : "Chat",
                () => true,
                () => IsLinked(),
                () => _chatOpen,
                v => _chatOpen = v,
                DrawChatWindow));
        }

        _dockItems.Add(new DockItem(
            "officer",
            _dockIconTexture,
            positive,
            "Officer Chat",
            () => HasOfficerAccess,
            () => HasOfficerAccess && IsLinked(),
            () => _officerOpen,
            v => _officerOpen = v,
            DrawOfficerWindow));

        _dockItems.Add(new DockItem(
            "syncshell",
            _dockIconTexture,
            accent,
            "Syncshell",
            () => _config.FCSyncShell && _syncshell != null,
            () => _config.FCSyncShell && _syncshell != null && IsLinked(),
            () => _syncshellOpen,
            v => _syncshellOpen = v,
            DrawSyncshellWindow));

        _dockItems.Add(new DockItem(
            "settings",
            _dockIconTexture,
            neutral,
            "Settings",
            () => true,
            () => true,
            () => _settings.IsOpen,
            v => _settings.IsOpen = v,
            () => { }));
    }

    private void DrawFeatureWindows()
    {
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

    private void EnsureDockPositionInitialized(Vector2 dockSize)
    {
        if (_config.DockPositionInitialized)
        {
            return;
        }

        var viewport = ImGui.GetMainViewport();
        var available = viewport.WorkSize;
        var desired = new Vector2(
            viewport.WorkPos.X + (available.X - dockSize.X) * 0.5f,
            viewport.WorkPos.Y + available.Y - dockSize.Y - 30f);

        _config.DockPosition = desired;
        _config.DockPositionInitialized = true;
        SaveConfig();
    }

    private void SaveConfig()
    {
        PluginServices.Instance!.PluginInterface.SavePluginConfig(_config);
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
}
