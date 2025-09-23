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
    private readonly HttpClient _httpClient;
    private const float FadeAlphaTolerance = 0.001f;
    private const float MinimumFadeDuration = 0.001f;
    private float _timeSinceLastInteraction;
    private float _fadeAlpha = 1f;

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
            PluginServices.Instance?.Framework.RunOnTick(() => ImGui.SetTabItemClosed("Syncshell"));
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

        ImGui.SetNextWindowSize(new Vector2(800, 600), ImGuiCond.FirstUseEver);
        ImGui.SetNextWindowSizeConstraints(new Vector2(600, 400), new Vector2(float.MaxValue, float.MaxValue));
        ImGui.PushStyleColor(ImGuiCol.WindowBg, new Vector4(0.11f, 0.11f, 0.12f, fadeAlpha));
        ImGui.PushStyleColor(ImGuiCol.ChildBg, new Vector4(0.09f, 0.09f, 0.1f, fadeAlpha));
        ImGui.PushStyleColor(ImGuiCol.Tab, new Vector4(0.15f, 0.15f, 0.16f, fadeAlpha));
        ImGui.PushStyleColor(ImGuiCol.TabActive, new Vector4(0.2f, 0.2f, 0.21f, fadeAlpha));
        ImGui.PushStyleColor(ImGuiCol.TabHovered, new Vector4(0.25f, 0.25f, 0.26f, fadeAlpha));

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
                    ImGui.BeginChild("##eventsArea", ImGui.GetContentRegionAvail(), false);
                    _ui.Draw();
                    ImGui.EndChild();
                    ImGui.EndTabItem();
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
                    _create.Draw();
                    ImGui.EndTabItem();
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
                        if (!_templatesTabActive)
                        {
                            _templates.OnTabActivated();
                        }
                        _templatesTabActive = true;
                        _templates.Draw();
                        ImGui.EndTabItem();
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
                    _requestBoard.Draw();
                    ImGui.EndTabItem();
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
                    _syncshell.Draw();
                    ImGui.EndTabItem();
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
                        var applyChatOpacity = !_config.ChatFadeOutEnabled && _chat is FcChatWindow;
                        if (applyChatOpacity)
                        {
                            var tabAlpha = Math.Clamp(_config.FcChatOpacity, 0f, 1f);
                            ImGui.PushStyleVar(ImGuiStyleVar.Alpha, tabAlpha);
                        }

                        ImGui.BeginChild("##chatArea", ImGui.GetContentRegionAvail(), false);
                        _chat.Draw();
                        ImGui.EndChild();
                        ImGui.EndTabItem();

                        if (applyChatOpacity)
                        {
                            ImGui.PopStyleVar();
                        }
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
                            var applyOfficerOpacity = !_config.ChatFadeOutEnabled;
                            if (applyOfficerOpacity)
                            {
                                var officerAlpha = Math.Clamp(_config.OfficerChatOpacity, 0f, 1f);
                                ImGui.PushStyleVar(ImGuiStyleVar.Alpha, officerAlpha);
                            }

                            ImGui.BeginChild("##officerChatArea", ImGui.GetContentRegionAvail(), false);
                            _officer.Draw();
                            ImGui.EndChild();
                            ImGui.EndTabItem();

                            if (applyOfficerOpacity)
                            {
                                ImGui.PopStyleVar();
                            }
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
        var focused = ImGui.IsWindowFocused(ImGuiFocusedFlags.RootAndChildWindows);
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
}
