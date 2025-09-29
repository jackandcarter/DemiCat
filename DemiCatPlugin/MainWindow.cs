using System;
using System.Net.Http;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using DemiCatPlugin.Emoji;

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
    private SyncshellWindow? _syncshell;
    private bool _syncshellEnabled;
    private bool _templatesTabActive;
    private bool _fcChatTabActive;
    private bool _officerTabActive;
    private readonly HttpClient _httpClient;
    private const float FadeAlphaTolerance = 0.001f;
    private const float MinimumFadeDuration = 0.001f;
    private float _timeSinceLastInteraction;
    private float _fadeAlpha = 1f;
    private bool _styleNeedsUpdate = true;
    private bool _closeSyncshellTab;

    public bool IsOpen;
    public bool HasOfficerAccess { get; set; }
    public UiRenderer Ui => _ui;
    public EventCreateWindow EventCreateWindow => _create;
    public TemplatesWindow TemplatesWindow => _templates;
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
        EmojiManager emojiManager)
    {
        _config = config;
        _ui = ui;
        _chat = chat;
        _officer = officer;
        _settings = settings;
        _httpClient = httpClient;
        _create = new EventCreateWindow(config, httpClient, channelService, channelSelection, emojiManager);
        _templates = new TemplatesWindow(config, httpClient, channelService, channelSelection, emojiManager);
        _requestBoard = new RequestBoardWindow(config, httpClient);
        _syncshellEnabled = config.FCSyncShell;
        _syncshell = _syncshellEnabled ? new SyncshellWindow(config, httpClient) : null;
        Instance = this;
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
            _closeSyncshellTab = true;
        }
    }

    public void Draw()
    {
        UpdateSyncshell();

        if (!IsOpen)
        {
            ResetFadeTimer();
            return;
        }

        var io = ImGui.GetIO();
        var deltaTime = Math.Max(0f, io.DeltaTime);
        var fadeAlpha = GetCurrentFadeAlpha();
        var fadeActive = _config.ChatFadeOutEnabled && fadeAlpha < 1f - FadeAlphaTolerance;

        var fcTabPreviouslyActive = _fcChatTabActive && _chat is FcChatWindow;
        var officerTabPreviouslyActive = _officerTabActive && HasOfficerAccess;

        var primaryColor = Config.SanitizeColor(_config.PrimaryWindowColor, Config.DefaultPrimaryWindowColor);
        var childBaseColor = AdjustBrightness(primaryColor, 0.9f);
        var accentColor = Config.SanitizeColor(_config.SecondaryAccentColor, Config.DefaultSecondaryAccentColor);
        var tabBaseColor = AdjustBrightness(primaryColor, 1.05f);
        var tabActiveBaseColor = accentColor;
        var tabHoveredBaseColor = AdjustBrightness(accentColor, 1.1f);

        var previousOpacityOverrideActive = !_config.ChatFadeOutEnabled && (fcTabPreviouslyActive || officerTabPreviouslyActive);

        var styleAlpha = fadeAlpha;
        if (!_config.ChatFadeOutEnabled)
        {
            styleAlpha = 1f;
            if (fcTabPreviouslyActive)
            {
                styleAlpha = Math.Clamp(_config.FcChatOpacity, 0f, 1f);
            }
            else if (officerTabPreviouslyActive)
            {
                styleAlpha = Math.Clamp(_config.OfficerChatOpacity, 0f, 1f);
            }
        }

        _fcChatTabActive = false;
        _officerTabActive = false;

        if (_styleNeedsUpdate)
        {
            ApplyAccentColors();
            _styleNeedsUpdate = false;
        }

        ImGui.SetNextWindowSize(new Vector2(800, 600), ImGuiCond.FirstUseEver);
        ImGui.SetNextWindowSizeConstraints(new Vector2(600, 400), new Vector2(float.MaxValue, float.MaxValue));
        PushWindowOpacityStyles(primaryColor, childBaseColor, tabBaseColor, tabActiveBaseColor, tabHoveredBaseColor, styleAlpha);

        var styleAlphaPushed = false;
        if (fadeActive)
        {
            ImGui.PushStyleVar(ImGuiStyleVar.Alpha, fadeAlpha);
            styleAlphaPushed = true;
        }

        var windowOpen = ImGui.Begin("DemiCat", ref IsOpen, ImGuiWindowFlags.NoCollapse);
        var interacted = windowOpen && HasWindowInteraction();

        if (windowOpen)
        {
            var linked = TokenManager.Instance?.State == LinkState.Linked;
            if (!linked)
            {
                ImGui.TextColored(new Vector4(1f, 0.85f, 0f, 1f), "Link DemiCat: run `/demibot embed` in Discord and paste the key.");
                ImGui.SameLine();
                if (ImGui.Button("Open Settings"))
                {
                    _settings.IsOpen = true;
                }
                ImGui.Separator();
            }

            var padding = ImGui.GetStyle().FramePadding;
            ImGui.Dummy(new Vector2(0f, padding.Y));
            var buttonSize = ImGui.GetFrameHeight();
            var cursor = ImGui.GetCursorPos();
            ImGui.SetCursorPos(new Vector2(ImGui.GetWindowContentRegionMax().X - buttonSize - padding.X, cursor.Y));
            if (ImGui.Button("\u2699\uFE0F##dc_settings", new Vector2(buttonSize, buttonSize)))
            {
                _settings.IsOpen = true;
            }
            ImGui.SetCursorPos(cursor);

            if (ImGui.BeginTabBar("MainTabs"))
            {
                if (_closeSyncshellTab)
                {
                    ImGui.SetTabItemClosed("Syncshell");
                    _closeSyncshellTab = false;
                }

                if (!linked)
                {
                    ImGui.BeginDisabled();
                    ImGui.TabItemButton("Events");
                    ImGui.EndDisabled();
                    if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
                        ImGui.SetTooltip("Link DemiCat to view events.");
                }
                else if (ImGui.BeginTabItem("Events"))
                {
                    var alphaOverrideState = TryPushWindowOpacityOverride(
                        previousOpacityOverrideActive,
                        primaryColor,
                        childBaseColor,
                        1f);

                    ImGui.BeginChild("##eventsArea", ImGui.GetContentRegionAvail(), false);
                    _ui.Draw();
                    ImGui.EndChild();
                    ImGui.EndTabItem();

                    PopWindowAlphaOnly(alphaOverrideState);
                }

                if (!linked)
                {
                    ImGui.BeginDisabled();
                    ImGui.TabItemButton("Create");
                    ImGui.EndDisabled();
                    if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
                        ImGui.SetTooltip("Link DemiCat to create events.");
                }
                else if (ImGui.BeginTabItem("Create"))
                {
                    var alphaOverrideState = TryPushWindowOpacityOverride(
                        previousOpacityOverrideActive,
                        primaryColor,
                        childBaseColor,
                        1f);

                    _create.Draw();
                    ImGui.EndTabItem();

                    PopWindowAlphaOnly(alphaOverrideState);
                }

                if (!linked)
                {
                    _templatesTabActive = false;
                    ImGui.BeginDisabled();
                    ImGui.TabItemButton("Templates");
                    ImGui.EndDisabled();
                    if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
                        ImGui.SetTooltip("Link DemiCat to use templates.");
                }
                else
                {
                    var templatesOpen = ImGui.BeginTabItem("Templates");
                    if (templatesOpen)
                    {
                        var alphaOverrideState = TryPushWindowOpacityOverride(
                            previousOpacityOverrideActive,
                            primaryColor,
                            childBaseColor,
                            1f);

                        if (!_templatesTabActive)
                        {
                            _templates.OnTabActivated();
                        }
                        _templatesTabActive = true;
                        _templates.Draw();
                        ImGui.EndTabItem();

                        PopWindowAlphaOnly(alphaOverrideState);
                    }
                    else
                    {
                        _templatesTabActive = false;
                    }
                }

                if (!linked)
                {
                    ImGui.BeginDisabled();
                    ImGui.TabItemButton("Request Board");
                    ImGui.EndDisabled();
                    if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
                        ImGui.SetTooltip("Link DemiCat to use request board.");
                }
                else if (ImGui.BeginTabItem("Request Board"))
                {
                    var alphaOverrideState = TryPushWindowOpacityOverride(
                        previousOpacityOverrideActive,
                        primaryColor,
                        childBaseColor,
                        1f);

                    _requestBoard.Draw();
                    ImGui.EndTabItem();

                    PopWindowAlphaOnly(alphaOverrideState);
                }

                if (!linked)
                {
                    if (_config.FCSyncShell)
                    {
                        ImGui.BeginDisabled();
                        ImGui.TabItemButton("Syncshell");
                        ImGui.EndDisabled();
                        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
                            ImGui.SetTooltip("Link DemiCat to use syncshell.");
                    }
                }
                else if (_config.FCSyncShell && _syncshell != null && ImGui.BeginTabItem("Syncshell"))
                {
                    var alphaOverrideState = TryPushWindowOpacityOverride(
                        previousOpacityOverrideActive,
                        primaryColor,
                        childBaseColor,
                        1f);

                    _syncshell.Draw();
                    ImGui.EndTabItem();

                    PopWindowAlphaOnly(alphaOverrideState);
                }

                if (_chat != null)
                {
                    var chatLabel = _chat is FcChatWindow ? "FC Chat" : "Chat";
                    var chatTooltip = _chat is FcChatWindow ? "Link DemiCat to use FC chat." : "Link DemiCat to use chat.";
                    if (!linked)
                    {
                        ImGui.BeginDisabled();
                        ImGui.TabItemButton(chatLabel);
                        ImGui.EndDisabled();
                        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
                            ImGui.SetTooltip(chatTooltip);
                    }
                    else if (ImGui.BeginTabItem(chatLabel))
                    {
                        var alphaOverrideState = AlphaOverrideState.None;
                        if (_chat is FcChatWindow)
                        {
                            _fcChatTabActive = true;

                            if (_config.ChatFadeOutEnabled)
                            {
                                alphaOverrideState = TryPushWindowOpacityOverride(
                                    previousOpacityOverrideActive,
                                    primaryColor,
                                    childBaseColor,
                                    Math.Clamp(_config.FcChatOpacity, 0f, 1f));
                            }
                            else
                            {
                                alphaOverrideState = PushWindowAlphaOnly(
                                    Math.Clamp(_config.FcChatOpacity, 0f, 1f),
                                    primaryColor,
                                    childBaseColor,
                                    useStyleVar: false);
                            }
                        }
                        else
                        {
                            alphaOverrideState = TryPushWindowOpacityOverride(
                                previousOpacityOverrideActive,
                                primaryColor,
                                childBaseColor,
                                1f);
                        }

                        ImGui.BeginChild("##chatArea", ImGui.GetContentRegionAvail(), false);
                        _chat.Draw();
                        ImGui.EndChild();
                        ImGui.EndTabItem();

                        PopWindowAlphaOnly(alphaOverrideState);
                    }
                }

                if (HasOfficerAccess)
                {
                    if (!linked)
                    {
                        ImGui.BeginDisabled();
                        ImGui.TabItemButton("Officer");
                        ImGui.EndDisabled();
                        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
                            ImGui.SetTooltip("Link DemiCat to use officer chat.");
                    }
                    else
                    {
                        var officerOpen = ImGui.BeginTabItem("Officer");
                        if (officerOpen)
                        {
                            _officerTabActive = true;

                            var alphaOverrideState = AlphaOverrideState.None;
                            if (_config.ChatFadeOutEnabled)
                            {
                                alphaOverrideState = TryPushWindowOpacityOverride(
                                    previousOpacityOverrideActive,
                                    primaryColor,
                                    childBaseColor,
                                    Math.Clamp(_config.OfficerChatOpacity, 0f, 1f));
                            }
                            else
                            {
                                alphaOverrideState = PushWindowAlphaOnly(
                                    Math.Clamp(_config.OfficerChatOpacity, 0f, 1f),
                                    primaryColor,
                                    childBaseColor,
                                    useStyleVar: false);
                            }

                            ImGui.BeginChild("##officerChatArea", ImGui.GetContentRegionAvail(), false);
                            _officer.Draw();
                            ImGui.EndChild();
                            ImGui.EndTabItem();

                            PopWindowAlphaOnly(alphaOverrideState);
                        }
                    }
                }

                ImGui.EndTabBar();
            }
        }

        ImGui.End();

        if (styleAlphaPushed)
        {
            ImGui.PopStyleVar();
        }

        ImGui.PopStyleColor(5);

        UpdateFadeState(interacted, deltaTime);
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

    private static void PushWindowOpacityStyles(
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
    }

    private static AlphaOverrideState PushWindowAlphaOnly(
        float alpha,
        Vector4 primaryColor,
        Vector4 childBaseColor,
        bool useStyleVar)
    {
        if (alpha >= 1f - FadeAlphaTolerance)
        {
            return AlphaOverrideState.None;
        }

        if (useStyleVar)
        {
            ImGui.PushStyleVar(ImGuiStyleVar.Alpha, alpha);
            return AlphaOverrideState.StyleVar;
        }

        ImGui.PushStyleColor(ImGuiCol.WindowBg, WithAlpha(primaryColor, alpha));
        ImGui.PushStyleColor(ImGuiCol.ChildBg, WithAlpha(childBaseColor, alpha));
        return AlphaOverrideState.BackgroundColors;
    }

    private static void PopWindowAlphaOnly(AlphaOverrideState state)
    {
        switch (state)
        {
            case AlphaOverrideState.StyleVar:
                ImGui.PopStyleVar();
                break;
            case AlphaOverrideState.BackgroundColors:
                ImGui.PopStyleColor(2);
                break;
        }
    }

    private static AlphaOverrideState TryPushWindowOpacityOverride(
        bool condition,
        Vector4 primaryColor,
        Vector4 childBaseColor,
        float alpha,
        bool useStyleVar = true)
    {
        if (!condition)
        {
            return AlphaOverrideState.None;
        }

        return PushWindowAlphaOnly(alpha, primaryColor, childBaseColor, useStyleVar);
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

    private enum AlphaOverrideState
    {
        None,
        StyleVar,
        BackgroundColors,
    }
}
