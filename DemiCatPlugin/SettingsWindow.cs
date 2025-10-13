using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Numerics;
using System.Threading;
using System.Threading.Tasks;
using System.Globalization;
using System.Linq;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.ImGuiFileDialog;
using Dalamud.Interface.Utility;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using DemiCatPlugin.SyncShell;

namespace DemiCatPlugin;

public class SettingsWindow : IDisposable
{
    private readonly Config _config;
    private readonly TokenManager _tokenManager;
    private readonly HttpClient _httpClient;
    private readonly Func<Task<bool>> _refreshRoles;
    private readonly Func<Task> _startNetworking;
    private readonly DeveloperWindow _devWindow;
    private readonly IPluginLog _log;
    private readonly FileDialogManager _fileDialog = new();

    private string _apiKey = string.Empty;
    private string _apiBaseUrl = string.Empty;
    private string _syncStatus = string.Empty;
    private bool _syncInProgress;
    private readonly Dictionary<string, bool> _categoryToggles = new();
    private readonly object _hardReloadLock = new();
    private Task? _hardReloadTask;
    private bool _requestMainWindowOpen;
    private bool _settingsLoaded;
    private bool _isLinked;
    private static readonly int[] FadeDurations = { 5, 10, 15, 20, 30 };
    private string _penumbraOverride = string.Empty;
    private string? _penumbraValidationMessage;
    private bool _penumbraValidationSuccess;
    private string _allowedDiscordIdsBuffer = string.Empty;

    public bool IsOpen;

    public MainWindow? MainWindow { get; set; }
    public ChatWindow? ChatWindow { get; set; }
    public OfficerChatWindow? OfficerChatWindow { get; set; }
    public ChannelWatcher? ChannelWatcher { get; set; }
    public RequestWatcher? RequestWatcher { get; set; }
    public NotePadService? NotePadService { get; set; }

    public SettingsWindow(Config config, TokenManager tokenManager, HttpClient httpClient, Func<Task<bool>> refreshRoles, Func<Task> startNetworking, IPluginLog log, IDalamudPluginInterface pluginInterface)
    {
        _config = config;
        _tokenManager = tokenManager;
        _httpClient = httpClient;
        _refreshRoles = refreshRoles;
        _startNetworking = startNetworking;
        _apiKey = tokenManager.Token ?? string.Empty;
        _apiBaseUrl = config.ApiBaseUrl;
        _devWindow = new DeveloperWindow(config, pluginInterface);
        _log = log;
        _isLinked = _tokenManager.State == LinkState.Linked;
        _tokenManager.OnLinked += OnLinked;
        _tokenManager.OnUnlinked += OnUnlinked;
        _penumbraOverride = _config.PenumbraPathOverride ?? string.Empty;
        _allowedDiscordIdsBuffer = string.Join(
            "\n",
            _config.ManualAutoList
                .OrderBy(id => id)
                .Select(id => id.ToString(CultureInfo.InvariantCulture)));
    }

    public void Draw()
    {
        if (IsOpen)
        {
            using var scope = new UiStyleScope(_config);
            var open = IsOpen;
            var flags = ImGuiWindowFlags.NoTitleBar;
            ImGui.PushStyleVar(ImGuiStyleVar.WindowBorderSize, 0f);
            var began = ImGui.Begin("DemiCat Settings", ref open, flags);
            if (began)
            {
                var style = ImGui.GetStyle();
                var chromeHeight = Math.Max(22f * ImGuiHelpers.GlobalScale, ImGui.GetFrameHeight() + style.FramePadding.Y * 1.5f);
                ImGui.SetCursorPos(new Vector2(style.WindowPadding.X, style.WindowPadding.Y));
                ImGui.Dummy(new Vector2(1f, chromeHeight));
                ImGui.SetCursorPos(new Vector2(style.WindowPadding.X, style.WindowPadding.Y + chromeHeight));

                if (!_settingsLoaded)
                {
                    _settingsLoaded = true;
                    _ = Task.Run(LoadSettings);
                }

                if (ImGui.BeginTabBar("SettingsTabs"))
                {
                    if (ImGui.BeginTabItem("General"))
                    {
                        DrawGeneralTab();
                        ImGui.EndTabItem();
                    }

                    if (ImGui.BeginTabItem("SyncShell Settings"))
                    {
                        DrawSyncshellTab();
                        ImGui.EndTabItem();
                    }

                    if (ImGui.BeginTabItem("Appearance"))
                    {
                        DrawAppearanceTab();
                        ImGui.EndTabItem();
                    }

                    ImGui.EndTabBar();
                }

                UiTheme.DrawWindowChrome(_config, "DemiCat Settings", () => open = false, chromeHeight);
            }
            ImGui.End();
            ImGui.PopStyleVar();

            IsOpen = (!open || UiTheme.RequestCloseThisFrame) ? false : open;
        }

        _devWindow.Draw();
    }

    private void DrawGeneralTab()
    {
        DrawConnectionIndicator(_isLinked);
        ImGui.Spacing();

        var linked = _isLinked;
        if (!linked)
        {
            ImGui.TextColored(new Vector4(1f, 0.85f, 0f, 1f), "Link DemiCat: run `/demibot embed` in Discord and paste the key.");
            UiTheme.DrawSectionSeparator();
        }

        if (ImGui.InputText("API Base URL", ref _apiBaseUrl, 256))
        {
            _config.ApiBaseUrl = _apiBaseUrl;
            SaveConfig();
            if (ApiHelpers.ValidateApiBaseUrl(_config))
            {
                _ = HardReloadIdentityAndStartAsync();
            }
            else
            {
                StopAllWatchersAndPresence();
            }
        }

        // Allow room for longer server-generated API keys
        ImGui.InputText("API Key", ref _apiKey, 256);
        ImGui.SameLine();
        ImGui.TextDisabled("\u03C0");
        var io = ImGui.GetIO();
        if (ImGui.IsItemClicked() && io.KeyCtrl && io.KeyShift)
        {
            _devWindow.IsOpen = true;
        }

        foreach (var kvp in _categoryToggles.ToList())
        {
            var enabled = kvp.Value;
            if (ImGui.Checkbox($"{kvp.Key}##cat", ref enabled))
            {
                _categoryToggles[kvp.Key] = enabled;
                _ = Task.Run(PushSettings);
            }

            ImGui.SameLine();
            var autoApply = _config.AutoApply.TryGetValue(kvp.Key, out var ap) && ap;
            if (ImGui.Checkbox($"Auto-apply##{kvp.Key}", ref autoApply))
            {
                _config.AutoApply[kvp.Key] = autoApply;
                SaveConfig();
                _ = Task.Run(PushSettings);
            }
        }

        if (ImGui.Button("Clear my cached data"))
        {
            ClearCachedData();
        }

        ImGui.SameLine();
        if (ImGui.Button("Forget me"))
        {
            _ = Task.Run(ForgetMe);
        }

        ImGui.BeginDisabled(_syncInProgress);
        if (ImGui.Button("Sync"))
        {
            _syncInProgress = true;
            _ = Task.Run(async () =>
            {
                try
                {
                    await Sync();
                }
                catch (Exception ex)
                {
                    _log.Error(ex, "Unexpected error during sync");
                }
            });
        }
        ImGui.EndDisabled();

        if (!string.IsNullOrEmpty(_syncStatus))
        {
            Vector4 color;
            if (_syncStatus == "API key validated")
            {
                color = new Vector4(0, 1, 0, 1);
            }
            else if (_syncStatus == "Authentication failed" || _syncStatus == "Network error")
            {
                color = new Vector4(1, 0, 0, 1);
            }
            else
            {
                color = new Vector4(1, 1, 1, 1);
            }

            ImGui.TextColored(color, _syncStatus);
        }
    }

    private void DrawSyncshellTab()
    {
        var service = PluginServices.Instance?.SyncShellService;
        var linked = _tokenManager.State == LinkState.Linked;

        DrawConnectionIndicator(linked);
        ImGui.Spacing();

        var enableSyncshell = _config.EnableSyncShell;
        if (!linked)
        {
            ImGui.BeginDisabled();
        }

        if (ImGui.Checkbox("Enable SyncShell", ref enableSyncshell))
        {
            _config.EnableSyncShell = enableSyncshell;
            _config.FCSyncShell = enableSyncshell;
            SaveConfig();

            if (service != null)
            {
                var syncService = service;
                if (enableSyncshell)
                {
                    _ = Task.Run(async () => await syncService.Start().ConfigureAwait(false));
                }
                else
                {
                    _ = Task.Run(async () => await syncService.Stop().ConfigureAwait(false));
                }
            }
        }

        if (!linked)
        {
            ImGui.EndDisabled();
            if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            {
                ImGui.SetTooltip("Link DemiCat to enable SyncShell.");
            }
        }

        var status = service?.Status ?? (linked ? "SyncShell disabled" : "Not linked");
        ImGui.TextUnformatted($"Status: {status}");
        if (service != null)
        {
            ImGui.SameLine();
            ImGui.TextDisabled($"Nearby users: {service.NearbyUserCount}");
        }

        var overlayVisible = _config.ShowSyncshellProgressOverlay;
        if (ImGui.Checkbox("Show Sync Progress Overlay", ref overlayVisible))
        {
            _config.ShowSyncshellProgressOverlay = overlayVisible;
            SaveConfig();
        }

        ImGui.Separator();

        var autoMode = _config.SyncAutoMode;
        if (ImGui.RadioButton("Auto mode (sync all linked members)", autoMode))
        {
            if (!_config.SyncAutoMode)
            {
                _config.SyncAutoMode = true;
                SaveConfig();
            }
        }
        ImGui.SameLine();
        if (ImGui.RadioButton("Manual mode", !autoMode))
        {
            if (_config.SyncAutoMode)
            {
                _config.SyncAutoMode = false;
                SaveConfig();
            }
        }

        ImGui.TextUnformatted("Manual Sync Allow List (Discord IDs)");
        var listHeight = ImGui.GetTextLineHeightWithSpacing() * 4f;
        if (ImGui.InputTextMultiline("##SyncshellAllowedDiscordIds", ref _allowedDiscordIdsBuffer, 4096, new Vector2(-1, listHeight)))
        {
            var entries = _allowedDiscordIdsBuffer
                .Split(new[] { '\n', ',', ';' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            var parsed = new HashSet<ulong>();
            foreach (var entry in entries)
            {
                if (ulong.TryParse(entry, NumberStyles.Integer, CultureInfo.InvariantCulture, out var id))
                {
                    parsed.Add(id);
                }
            }

            _config.ManualAutoList = parsed;
            _allowedDiscordIdsBuffer = string.Join(
                "\n",
                parsed.OrderBy(id => id).Select(id => id.ToString(CultureInfo.InvariantCulture)));
            SaveConfig();
        }
        ImGui.TextDisabled("One Discord ID per line. Invalid entries are ignored.");

        ImGui.Separator();

        var cacheLimit = _config.SyncshellCacheLimitMb;
        if (ImGui.SliderInt("Cache limit (MiB)", ref cacheLimit, 256, 16384))
        {
            cacheLimit = Math.Clamp(cacheLimit, 256, 16384);
            if (cacheLimit != _config.SyncshellCacheLimitMb)
            {
                _config.SyncshellCacheLimitMb = cacheLimit;
                SaveConfig();
                if (service != null)
                {
                    var syncService = service;
                    _ = Task.Run(async () => await syncService.EnforceCacheLimitAsync().ConfigureAwait(false));
                }
            }
        }

        ImGui.Spacing();

        var running = service?.IsRunning ?? false;
        if (!running)
        {
            ImGui.BeginDisabled();
        }

        if (ImGui.Button("Sync now"))
        {
            if (service != null)
            {
                var syncService = service;
                _ = Task.Run(async () => await syncService.TriggerPublishAsync().ConfigureAwait(false));
            }
        }
        ImGui.SameLine();
        if (ImGui.Button("Resync All"))
        {
            if (service != null)
            {
                var syncService = service;
                _ = Task.Run(async () => await syncService.ResyncAllAsync().ConfigureAwait(false));
            }
        }
        ImGui.SameLine();
        if (ImGui.Button("Clear Cache"))
        {
            service?.ClearCache();
            if (service != null)
            {
                var syncService = service;
                _ = Task.Run(async () => await syncService.EnforceCacheLimitAsync().ConfigureAwait(false));
            }
        }
        ImGui.SameLine();
        var paused = service?.IsPaused ?? false;
        if (ImGui.Button(paused ? "Resume Sync" : "Pause Sync"))
        {
            if (service != null)
            {
                if (paused)
                {
                    service.Resume();
                }
                else
                {
                    service.Pause();
                }
            }
        }

        if (!running)
        {
            ImGui.EndDisabled();
        }

        ImGui.Separator();

        var penumbraAvailable = service?.PenumbraAvailable ?? false;
        var detectedPath = service?.DetectedPenumbraPath;
        var detectedFromSettings = service?.DetectedPenumbraPathFromSettingsJson ?? false;
        if (!string.IsNullOrEmpty(detectedPath))
        {
            var label = detectedFromSettings
                ? $"Detected from Penumbra settings.json: {detectedPath}"
                : $"Detected Penumbra path: {detectedPath}";
            ImGui.TextDisabled(label);
        }
        else if (penumbraAvailable)
        {
            ImGui.TextDisabled("Penumbra IPC reported no mod directory. Set a path manually if needed.");
        }
        else
        {
            ImGui.TextDisabled("Penumbra IPC not detected. Set a path manually if needed.");
        }

        if (ImGui.InputText("Penumbra path override", ref _penumbraOverride, 512))
        {
            _config.PenumbraPathOverride = string.IsNullOrWhiteSpace(_penumbraOverride) ? null : _penumbraOverride.Trim();
            SaveConfig();
        }

        ImGui.SameLine();
        if (ImGui.Button("Browse…"))
        {
            _fileDialog.OpenFolderDialog("Select Penumbra Mod Directory", (ok, path) =>
            {
                if (ok && !string.IsNullOrWhiteSpace(path))
                {
                    _penumbraOverride = path;
                    _config.PenumbraPathOverride = path;
                    SaveConfig();
                    service?.RefreshAppearanceCaches();
                }
            });
        }

        if (ImGui.Button("Validate Path"))
        {
            string? error = null;
            var pathToCheck = string.IsNullOrWhiteSpace(_penumbraOverride) ? detectedPath : _penumbraOverride;
            if (string.IsNullOrWhiteSpace(pathToCheck))
            {
                _penumbraValidationSuccess = false;
                _penumbraValidationMessage = "No path to validate. Set an override or ensure Penumbra is running.";
            }
            else if (service != null && service.TryValidatePenumbraPath(pathToCheck, out error))
            {
                _penumbraValidationSuccess = true;
                _penumbraValidationMessage = "Penumbra path looks good.";
            }
            else
            {
                _penumbraValidationSuccess = false;
                _penumbraValidationMessage = error ?? "Validation failed.";
            }
        }

        if (!string.IsNullOrEmpty(_penumbraValidationMessage))
        {
            var color = _penumbraValidationSuccess ? new Vector4(0f, 0.8f, 0f, 1f) : new Vector4(0.9f, 0f, 0f, 1f);
            ImGui.TextColored(color, _penumbraValidationMessage);
        }

        _fileDialog.Draw();
    }

    private void DrawAppearanceTab()
    {
        var fadeOutEnabled = _config.ChatFadeOutEnabled;
        var scale = ImGuiHelpers.GlobalScale;
        var maxPicker = 420f * scale;
        var minPicker = 240f * scale;

        ImGui.TextUnformatted("Chat Options");
        ImGui.Spacing();

        var chatFontScale = _config.ChatFontScale;
        if (ImGui.SliderFloat("Chat Font Scale", ref chatFontScale, Config.MinChatFontScale, Config.MaxChatFontScale, "%.2fx"))
        {
            chatFontScale = Math.Clamp(chatFontScale, Config.MinChatFontScale, Config.MaxChatFontScale);
            if (Math.Abs(chatFontScale - _config.ChatFontScale) > 0.0001f)
            {
                _config.ChatFontScale = chatFontScale;
                SaveConfig();
                RefreshChatWindows();
            }
        }

        var autoStretch = _config.ChatImageAutoStretch;
        if (ImGui.Checkbox("Auto-stretch chat images", ref autoStretch))
        {
            _config.ChatImageAutoStretch = autoStretch;
            SaveConfig();
            RefreshChatWindows();
        }

        var manualImageScale = _config.ChatImageManualScale;
        ImGui.BeginDisabled(autoStretch);
        var manualScaleChanged = ImGui.SliderFloat("Manual Image Scale", ref manualImageScale, Config.MinChatImageScale, Config.MaxChatImageScale, "%.2fx");
        ImGui.EndDisabled();
        if (autoStretch && ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            ImGui.SetTooltip("Disable auto-stretch to adjust manual image scale.");
        if (manualScaleChanged)
        {
            manualImageScale = Math.Clamp(manualImageScale, Config.MinChatImageScale, Config.MaxChatImageScale);
            if (Math.Abs(manualImageScale - _config.ChatImageManualScale) > 0.0001f)
            {
                _config.ChatImageManualScale = manualImageScale;
                SaveConfig();
                RefreshChatWindows();
            }
        }

        ImGui.Spacing();

        ImGui.TextUnformatted("Theme");
        ImGui.Spacing();

        var primaryColor = _config.PrimaryWindowColor;
        if (ImGui.ColorEdit4("Primary window color", ref primaryColor))
        {
            var sanitized = Config.SanitizeColor(primaryColor, Config.DefaultPrimaryWindowColor);
            if (!ColorsAlmostEqual(sanitized, _config.PrimaryWindowColor))
            {
                _config.PrimaryWindowColor = sanitized;
                SaveConfig();
                RefreshChatWindows();
                MainWindow?.OnAppearanceSettingsChanged();
            }
        }

        var secondaryColor = _config.SecondaryAccentColor;
        if (ImGui.ColorEdit4("Secondary accent color", ref secondaryColor))
        {
            var sanitized = Config.SanitizeColor(secondaryColor, Config.DefaultSecondaryAccentColor);
            if (!ColorsAlmostEqual(sanitized, _config.SecondaryAccentColor))
            {
                _config.SecondaryAccentColor = sanitized;
                SaveConfig();
                RefreshChatWindows();
                MainWindow?.OnAppearanceSettingsChanged();
            }
        }

        UiTheme.DrawSectionSeparator();
        ImGui.TextUnformatted("Dock appearance");
        ImGui.Spacing();

        var style = ImGui.GetStyle();
        var availWidth = MathF.Max(1f, ImGui.GetContentRegionAvail().X);
        var twoColumnLayout = availWidth > (maxPicker * 2f + style.ItemSpacing.X * 3f);
        var pickerWidth = Math.Clamp(twoColumnLayout ? (availWidth - style.ItemSpacing.X * 3f) * 0.5f : availWidth, minPicker, maxPicker);

        if (twoColumnLayout)
        {
            ImGui.Columns(2, "appearance_cols", false);
        }

        ImGui.PushItemWidth(pickerWidth);
        var dockBorderColor = _config.DockBorderColor;
        if (DrawAdvancedColorPicker("Dock border color", ref dockBorderColor))
        {
            var sanitized = Config.SanitizeColor(dockBorderColor, Config.DefaultDockBorderColor);
            if (!ColorsAlmostEqual(sanitized, _config.DockBorderColor))
            {
                _config.DockBorderColor = sanitized;
                SaveConfig();
            }
        }
        ImGui.PopItemWidth();

        if (twoColumnLayout)
        {
            ImGui.Columns(1);
        }
        ImGui.Spacing();

        var gradientEnabled = _config.DockGradientEnabled;
        if (ImGui.Checkbox("Enable gradient background", ref gradientEnabled))
        {
            _config.DockGradientEnabled = gradientEnabled;
            SaveConfig();
        }

        ImGui.Spacing();

        if (twoColumnLayout)
        {
            ImGui.Columns(2, "appearance_cols", false);
        }

        void AdvancePickerColumn()
        {
            if (twoColumnLayout)
            {
                ImGui.NextColumn();
            }
            else
            {
                ImGui.Spacing();
            }
        }

        ImGui.BeginDisabled(gradientEnabled);
        var dockColor = _config.DockBackgroundColor;
        ImGui.PushItemWidth(pickerWidth);
        var dockColorChanged = DrawAdvancedColorPicker("Dock background color", ref dockColor);
        var solidHovered = ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled);
        ImGui.PopItemWidth();
        if (dockColorChanged)
        {
            var sanitized = Config.SanitizeColor(dockColor, Config.DefaultDockBackgroundColor);
            if (!ColorsAlmostEqual(sanitized, _config.DockBackgroundColor))
            {
                _config.DockBackgroundColor = sanitized;
                SaveConfig();
            }
        }
        AdvancePickerColumn();
        ImGui.EndDisabled();
        if (gradientEnabled && solidHovered)
        {
            ImGui.SetTooltip("Disable gradient background to adjust the solid dock color.");
        }

        ImGui.BeginDisabled(!gradientEnabled);
        var gradientStart = _config.DockGradientStartColor;
        ImGui.PushItemWidth(pickerWidth);
        if (DrawAdvancedColorPicker("Gradient start color", ref gradientStart))
        {
            var sanitized = Config.SanitizeColor(gradientStart, Config.DefaultDockBackgroundColor);
            if (!ColorsAlmostEqual(sanitized, _config.DockGradientStartColor))
            {
                _config.DockGradientStartColor = sanitized;
                SaveConfig();
            }
        }
        ImGui.PopItemWidth();
        AdvancePickerColumn();

        var gradientEnd = _config.DockGradientEndColor;
        ImGui.PushItemWidth(pickerWidth);
        if (DrawAdvancedColorPicker("Gradient end color", ref gradientEnd))
        {
            var sanitized = Config.SanitizeColor(gradientEnd, Config.DefaultDockBackgroundColor);
            if (!ColorsAlmostEqual(sanitized, _config.DockGradientEndColor))
            {
                _config.DockGradientEndColor = sanitized;
                SaveConfig();
            }
        }
        ImGui.PopItemWidth();
        AdvancePickerColumn();
        ImGui.EndDisabled();

        if (twoColumnLayout)
        {
            ImGui.Columns(1);
        }

        ImGui.Spacing();

        var dockOpacity = _config.DockOpacity * 100f;
        if (ImGui.SliderFloat("Dock background opacity", ref dockOpacity, 0f, 100f, "%.0f%%"))
        {
            var normalized = Math.Clamp(dockOpacity / 100f, 0f, 1f);
            if (Math.Abs(normalized - _config.DockOpacity) > 0.0001f)
            {
                _config.DockOpacity = normalized;
                SaveConfig();
            }
        }

        var dockAutoFade = _config.DockAutoFadeEnabled;
        if (ImGui.Checkbox("Allow dock to follow auto-fade", ref dockAutoFade))
        {
            _config.DockAutoFadeEnabled = dockAutoFade;
            SaveConfig();
        }

        UiTheme.DrawSectionSeparator();
        ImGui.TextUnformatted("Dock behavior");
        ImGui.Spacing();
        ImGui.TextWrapped("Select which windows should open automatically when the dock is shown.");
        var dockAutoOpen = _config.DockAutoOpenWindows ??= new List<string>();
        foreach (var option in GetDockAutoOpenOptions())
        {
            var enabled = dockAutoOpen.Contains(option.Id);
            if (ImGui.Checkbox(option.Label, ref enabled))
            {
                if (enabled)
                {
                    if (!dockAutoOpen.Contains(option.Id))
                    {
                        dockAutoOpen.Add(option.Id);
                    }
                }
                else
                {
                    dockAutoOpen.RemoveAll(id => string.Equals(id, option.Id, StringComparison.Ordinal));
                }

                SaveConfig();
            }
        }

        ImGui.Spacing();

        ImGui.BeginDisabled(fadeOutEnabled);
        var fcOpacity = _config.FcChatOpacity * 100f;
        if (ImGui.SliderFloat("FC Chat Opacity", ref fcOpacity, 0f, 100f, "%.0f%%"))
        {
            _config.FcChatOpacity = Math.Clamp(fcOpacity / 100f, 0f, 1f);
            SaveConfig();
            MainWindow?.ResetFadeTimer();
        }
        ImGui.EndDisabled();
        if (fadeOutEnabled && ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            ImGui.SetTooltip("Disable fade-out to adjust per-tab opacity.");

        ImGui.BeginDisabled(fadeOutEnabled);
        var officerOpacity = _config.OfficerChatOpacity * 100f;
        if (ImGui.SliderFloat("Officer Tab Opacity", ref officerOpacity, 0f, 100f, "%.0f%%"))
        {
            _config.OfficerChatOpacity = Math.Clamp(officerOpacity / 100f, 0f, 1f);
            SaveConfig();
            MainWindow?.ResetFadeTimer();
        }
        ImGui.EndDisabled();
        if (fadeOutEnabled && ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            ImGui.SetTooltip("Disable fade-out to adjust per-tab opacity.");

        ImGui.NewLine();
        UiTheme.DrawSectionSeparator();

        if (ImGui.Checkbox("Enable fade-out", ref fadeOutEnabled))
        {
            _config.ChatFadeOutEnabled = fadeOutEnabled;
            SaveConfig();
            MainWindow?.ResetFadeTimer();
        }

        ImGui.BeginDisabled(!fadeOutEnabled);
        var fadeDurations = FadeDurations;
        var currentIndex = Array.IndexOf(fadeDurations, _config.ChatFadeOutDelaySeconds);
        if (currentIndex < 0)
        {
            currentIndex = 0;
        }

        var currentLabel = $"{fadeDurations[currentIndex]} seconds";
        if (ImGui.BeginCombo("Fade-out delay", currentLabel))
        {
            for (var i = 0; i < fadeDurations.Length; i++)
            {
                var label = $"{fadeDurations[i]} seconds";
                var selected = i == currentIndex;
                if (ImGui.Selectable(label, selected))
                {
                    _config.ChatFadeOutDelaySeconds = fadeDurations[i];
                    SaveConfig();
                    MainWindow?.ResetFadeTimer();
                    currentIndex = i;
                }
                if (selected)
                    ImGui.SetItemDefaultFocus();
            }
            ImGui.EndCombo();
        }

        var fadeMinimumAlphaPercent = _config.ChatFadeOutMinimumAlpha * 100f;
        if (ImGui.SliderFloat("Fade-out minimum opacity", ref fadeMinimumAlphaPercent, 0f, 100f, "%.0f%%"))
        {
            _config.ChatFadeOutMinimumAlpha = Math.Clamp(fadeMinimumAlphaPercent / 100f, 0f, 1f);
            SaveConfig();
            MainWindow?.ResetFadeTimer();
        }

        ImGui.SameLine();
        DrawFadePreview("##fadePreview", _config.ChatFadeOutMinimumAlpha);
        ImGui.EndDisabled();
    }

    private void RefreshChatWindows()
    {
        ChatWindow?.OnAppearanceSettingsChanged();
        OfficerChatWindow?.OnAppearanceSettingsChanged();
    }

    private static IReadOnlyList<(string Id, string Label)> GetDockAutoOpenOptions()
        => new (string Id, string Label)[]
        {
            (MainWindow.DockIds.Events, "Events"),
            (MainWindow.DockIds.Create, "Create"),
            (MainWindow.DockIds.Templates, "Templates"),
            (MainWindow.DockIds.NotePad, "NotePad"),
            (MainWindow.DockIds.Requests, "Requests"),
            (MainWindow.DockIds.Syncshell, "SyncShell"),
            (MainWindow.DockIds.Chat, "FC Chat"),
            (MainWindow.DockIds.Officer, "Officer Chat"),
        };

    private static bool ColorsAlmostEqual(Vector4 a, Vector4 b)
    {
        return Vector4.DistanceSquared(a, b) <= 0.0001f;
    }

    private static bool DrawAdvancedColorPicker(string label, ref Vector4 value)
    {
        ImGui.TextUnformatted(label);
        ImGui.PushID(label);
        var color = value;
        const ImGuiColorEditFlags flags = ImGuiColorEditFlags.AlphaBar
                                          | ImGuiColorEditFlags.AlphaPreviewHalf
                                          | ImGuiColorEditFlags.PickerHueWheel;
        var width = ImGui.CalcItemWidth();
        ImGui.PushItemWidth(width);
        var changed = ImGui.ColorPicker4("##picker", ref color, flags);
        ImGui.PopItemWidth();
        ImGui.PopID();

        if (changed && !ColorsAlmostEqual(color, value))
        {
            value = color;
            return true;
        }

        return false;
    }

    private static void DrawFadePreview(string id, float alpha)
    {
        var size = new Vector2(ImGui.GetFrameHeight() * 2f, ImGui.GetFrameHeight());
        var cursor = ImGui.GetCursorScreenPos();
        var max = cursor + size;
        var drawList = ImGui.GetWindowDrawList();

        var backgroundColor = ImGui.GetColorU32(new Vector4(0.2f, 0.2f, 0.2f, 1f));
        drawList.AddRectFilled(cursor, max, backgroundColor);

        var previewColor = ImGui.GetColorU32(new Vector4(1f, 1f, 1f, alpha));
        drawList.AddRectFilled(cursor, max, previewColor);

        var borderColor = ImGui.GetColorU32(new Vector4(1f, 1f, 1f, 1f));
        drawList.AddRect(cursor, max, borderColor);

        ImGui.InvisibleButton(id, size);
        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip($"{Math.Clamp((int)Math.Round(alpha * 100f), 0, 100)}% opacity");
        }
    }

    private void OnLinked() => _isLinked = true;

    private void OnUnlinked(string? reason)
    {
        _isLinked = false;
        _syncStatus = reason ?? string.Empty;
    }

    private static void DrawConnectionIndicator(bool linked)
    {
        var color = linked ? new Vector4(0f, 1f, 0f, 1f) : new Vector4(1f, 0f, 0f, 1f);
        var drawList = ImGui.GetWindowDrawList();
        var pos = ImGui.GetCursorScreenPos();
        var radius = ImGui.GetTextLineHeight() * 0.3f;
        drawList.AddCircleFilled(pos + new Vector2(radius, radius), radius, ImGui.GetColorU32(color));
        ImGui.Dummy(new Vector2(radius * 2, radius * 2));
        ImGui.SameLine();
        ImGui.TextColored(color, linked ? "Connected" : "Disconnected");
    }

    private async Task LoadSettings()
    {
        try
        {
            if (_httpClient == null || !_tokenManager.IsReady() || !ApiHelpers.ValidateApiBaseUrl(_config))
                return;

            var url = $"{_config.ApiBaseUrl.TrimEnd('/')}/api/users/me/settings";
            var request = new HttpRequestMessage(HttpMethod.Get, url);
            ApiHelpers.AddAuthHeader(request, _tokenManager);
            var response = await _httpClient.SendAsync(request);
            if (!response.IsSuccessStatusCode)
                return;

            var body = await response.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.TryGetProperty("consent_sync", out var consent))
            {
                var enabled = consent.GetBoolean();
                _config.FCSyncShell = enabled;
                _config.EnableSyncShell = enabled;
            }

            if (doc.RootElement.TryGetProperty("settings", out var settings) && settings.ValueKind == JsonValueKind.Object)
            {
                if (settings.TryGetProperty("autoApply", out var autoApply) && autoApply.ValueKind == JsonValueKind.Object)
                {
                    foreach (var prop in autoApply.EnumerateObject())
                    {
                        _config.AutoApply[prop.Name] = prop.Value.GetBoolean();
                    }
                }

                if (settings.TryGetProperty("categories", out var cats) && cats.ValueKind == JsonValueKind.Object)
                {
                    foreach (var prop in cats.EnumerateObject())
                    {
                        var enabled = prop.Value.ValueKind == JsonValueKind.True
                            || (prop.Value.TryGetProperty("enabled", out var en) && en.GetBoolean());
                        _categoryToggles[prop.Name] = enabled;
                    }
                }
            }

            SaveConfig();
        }
        catch (Exception ex)
        {
            _log.Error(ex, "Failed to load settings");
        }
    }

    private async Task PushSettings()
    {
        try
        {
            if (_httpClient == null || !_tokenManager.IsReady() || !ApiHelpers.ValidateApiBaseUrl(_config))
                return;

            var url = $"{_config.ApiBaseUrl.TrimEnd('/')}/api/users/me/settings";
            var payload = new
            {
                settings = new
                {
                    categories = _categoryToggles,
                    autoApply = _config.AutoApply
                },
                consent_sync = _config.EnableSyncShell
            };

            var request = new HttpRequestMessage(HttpMethod.Put, url)
            {
                Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json")
            };
            ApiHelpers.AddAuthHeader(request, _tokenManager);
            await _httpClient.SendAsync(request);
        }
        catch (Exception ex)
        {
            _log.Error(ex, "Failed to push settings");
        }
    }

    private async Task UpdateIdentityAsync(IFramework? framework)
    {
        try
        {
            if (_httpClient == null || !_tokenManager.IsReady() || !ApiHelpers.ValidateApiBaseUrl(_config))
                return;

            var url = $"{_config.ApiBaseUrl.TrimEnd('/')}/api/users/me";
            var request = new HttpRequestMessage(HttpMethod.Get, url);
            ApiHelpers.AddAuthHeader(request, _tokenManager);
            var response = await _httpClient.SendAsync(request);

            if (!response.IsSuccessStatusCode)
            {
                var responseBody = await response.Content.ReadAsStringAsync();
                _log.Warning($"Failed to load user identity: {response.StatusCode}. Response Body: {responseBody}");
                return;
            }

            using var stream = await response.Content.ReadAsStreamAsync();
            using var doc = await JsonDocument.ParseAsync(stream);

            string? newGuildId = null;
            bool? officerClaim = null;

            if (doc.RootElement.TryGetProperty("guildId", out var guildProperty) && guildProperty.ValueKind == JsonValueKind.String)
            {
                newGuildId = guildProperty.GetString();
            }

            if (doc.RootElement.TryGetProperty("isOfficer", out var officerProperty))
            {
                officerClaim = officerProperty.ValueKind switch
                {
                    JsonValueKind.True => true,
                    JsonValueKind.False => false,
                    JsonValueKind.Number when officerProperty.TryGetInt32(out var numeric) => numeric != 0,
                    _ => officerClaim
                };
            }

            var configChanged = false;

            if (newGuildId != null)
            {
                var normalizedGuild = string.IsNullOrWhiteSpace(newGuildId)
                    ? string.Empty
                    : ChannelKeyHelper.NormalizeGuildId(newGuildId);
                if (!string.Equals(_config.GuildId, normalizedGuild, StringComparison.Ordinal))
                {
                    _config.GuildId = normalizedGuild;
                    configChanged = true;
                }
            }

            if (officerClaim.HasValue && _config.IsOfficerToken != officerClaim.Value)
            {
                _config.IsOfficerToken = officerClaim.Value;
                configChanged = true;
            }

            if (configChanged)
            {
                SaveConfig();
            }

            void ApplyOfficerAccess()
            {
                if (MainWindow != null)
                {
                    MainWindow.HasOfficerAccess = OfficerPermissions.HasAccess(_config);
                }
            }

            if (framework != null)
            {
                try
                {
                    _ = framework.RunOnTick(ApplyOfficerAccess);
                }
                catch (Exception ex)
                {
                    _log.Warning(ex, "Failed to apply officer access update on framework thread.");
                    ApplyOfficerAccess();
                }
            }
            else
            {
                ApplyOfficerAccess();
            }
        }
        catch (Exception ex)
        {
            _log.Error(ex, "Failed to update user identity");
        }
    }

    private void StopAllWatchersAndPresence()
    {
        void SafeInvoke(Action action, string context)
        {
            try
            {
                action();
            }
            catch (Exception ex)
            {
                _log.Error(ex, context);
            }
        }

        SafeInvoke(() => ChatWindow?.StopNetworking(), "Failed to stop ChatWindow networking.");
        SafeInvoke(() => OfficerChatWindow?.StopNetworking(), "Failed to stop OfficerChatWindow networking.");
        SafeInvoke(() => MainWindow?.Ui.StopNetworking(), "Failed to stop UI networking.");
        SafeInvoke(() => MainWindow?.TemplatesWindow.StopNetworking(), "Failed to stop TemplatesWindow networking.");
        SafeInvoke(() =>
        {
            if (ChannelWatcher != null)
            {
                ChannelWatcher.Stop();
            }
        }, "Failed to stop ChannelWatcher.");
        SafeInvoke(() => RequestWatcher?.Stop(), "Failed to stop RequestWatcher.");
        SafeInvoke(() => NotePadService?.Stop(), "Failed to stop NotePad service.");
        SafeInvoke(() =>
        {
            var presence = ChatWindow?.Presence ?? OfficerChatWindow?.Presence;
            if (presence != null)
            {
                presence.SetPresenceReady(false);
                presence.Stop();
            }
        }, "Failed to stop presence service.");
    }

    private void ClearRuntimeCaches()
    {
        try
        {
            RoleCache.Reset();
        }
        catch (Exception ex)
        {
            _log.Error(ex, "Failed to reset role cache.");
        }

        try
        {
            SignupPresetService.Reset();
        }
        catch (Exception ex)
        {
            _log.Error(ex, "Failed to reset signup presets.");
        }

        try
        {
            MembershipCache.Reset();
        }
        catch (Exception ex)
        {
            _log.Error(ex, "Failed to reset membership cache.");
        }

        try
        {
            SyncshellWindow.Instance?.ClearCaches();
        }
        catch (Exception ex)
        {
            _log.Error(ex, "Failed to clear Syncshell caches.");
        }

        try
        {
            RequestStateService.Load(_config);
        }
        catch (Exception ex)
        {
            _log.Error(ex, "Failed to reset request state cache.");
        }

        _config.GuildId = string.Empty;
        _config.GuildRoles.Clear();
        _config.ChannelSelections.Clear();
        _config.ChatChannelId = string.Empty;
        _config.EventChannelId = string.Empty;
        _config.FcChannelId = string.Empty;
        _config.FcChannelName = string.Empty;
        _config.OfficerChannelId = string.Empty;
        _config.ChatCursors.Clear();
        _config.RestChatCursors.Clear();
        _config.Roles.Clear();
        _config.IsOfficerToken = false;
        _config.MentionRoleIds.Clear();
        _config.SignupPresets.Clear();
        _config.TemplateData.Clear();
        _config.RequestStates.Clear();
        _config.RequestsDeltaToken = null;
        _config.Categories.Clear();
        _config.AutoApply.Clear();
        _config.PenumbraChoices.Clear();
        _config.NotePadLastSectionId = null;
        _config.NotePadLastPageId = null;

        _categoryToggles.Clear();

        if (ChatWindow != null)
        {
            ChatWindow.ChannelsLoaded = false;
        }

        if (OfficerChatWindow != null)
        {
            OfficerChatWindow.ChannelsLoaded = false;
        }

        if (MainWindow != null)
        {
            MainWindow.HasOfficerAccess = OfficerPermissions.HasAccess(_config);
        }
    }

    internal Task HardReloadIdentityAndStartAsync(bool openMainWindow = false)
    {
        lock (_hardReloadLock)
        {
            if (openMainWindow)
            {
                _requestMainWindowOpen = true;
            }

            if (_hardReloadTask == null || _hardReloadTask.IsCompleted)
            {
                _hardReloadTask = HardReloadIdentityAndStartInternalAsync();
            }

            return _hardReloadTask;
        }
    }

    private async Task HardReloadIdentityAndStartInternalAsync()
    {
        var framework = PluginServices.Instance?.Framework;
        var presence = ChatWindow?.Presence ?? OfficerChatWindow?.Presence;
        var presenceRestarted = false;

        void UpdateStatus(string status)
        {
            if (framework == null)
            {
                _syncStatus = status;
                return;
            }

            try
            {
                framework.RunOnTick(() => _syncStatus = status);
            }
            catch (Exception ex)
            {
                _log.Warning(ex, "Failed to marshal sync status update to framework thread.");
                _syncStatus = status;
            }
        }

        presence?.SetPresenceReady(false);

        try
        {
            try
            {
                MainWindow?.ReloadSignupPresets();
            }
            catch (Exception ex)
            {
                _log.Error(ex, "Failed to reload signup presets during hard reload.");
            }

            HttpResponseMessage? pingResponse = null;
            try
            {
                var pingService = PingService.Instance ?? new PingService(_httpClient, _config, _tokenManager);
                pingResponse = await pingService.PingAsync(CancellationToken.None);
            }
            catch (Exception ex)
            {
                _log.Warning(ex, "Failed to ping DemiCat backend during hard reload.");
            }

            if (pingResponse == null)
            {
                UpdateStatus("Network error");
                return;
            }

            if (pingResponse.StatusCode == HttpStatusCode.Unauthorized || pingResponse.StatusCode == HttpStatusCode.Forbidden)
            {
                _tokenManager.Clear("Invalid API key");
                UpdateStatus("Authentication failed");
                return;
            }

            if (!pingResponse.IsSuccessStatusCode)
            {
                UpdateStatus("Network error");
                return;
            }

            await UpdateIdentityAsync(framework);

            if (_refreshRoles != null)
            {
                try
                {
                    var rolesRefreshed = await _refreshRoles();
                    if (!rolesRefreshed)
                    {
                        _log.Warning("Role refresh after key validation reported failure.");
                    }
                }
                catch (Exception ex)
                {
                    _log.Error(ex, "Failed to refresh roles during hard reload.");
                }
            }
            else
            {
                _log.Warning("RefreshRoles delegate is not set; roles will not be refreshed.");
            }

            try
            {
                await _startNetworking();
            }
            catch (Exception ex)
            {
                _log.Error(ex, "Failed to start UI networking during hard reload.");
            }

            try
            {
                if (_config.Events)
                {
                    MainWindow?.EventCreateWindow.StartNetworking();
                    MainWindow?.TemplatesWindow.StartNetworking();
                }
            }
            catch (Exception ex)
            {
                _log.Error(ex, "Failed to start event or template networking during hard reload.");
            }

            try
            {
                if (ChannelWatcher != null)
                {
                    await ChannelWatcher.Start();
                }
            }
            catch (Exception ex)
            {
                _log.Error(ex, "Failed to start ChannelWatcher during hard reload.");
            }

            try
            {
                RequestWatcher?.Start();
            }
            catch (Exception ex)
            {
                _log.Error(ex, "Failed to start RequestWatcher during hard reload.");
            }

            try
            {
                if (_config.NotePadEnabled)
                {
                    NotePadService?.Start();
                }
            }
            catch (Exception ex)
            {
                _log.Error(ex, "Failed to start NotePad service during hard reload.");
            }

            try
            {
                if (_config.SyncedChat && _config.EnableFcChat)
                {
                    ChatWindow?.StartNetworking();
                }
            }
            catch (Exception ex)
            {
                _log.Error(ex, "Failed to start ChatWindow networking during hard reload.");
            }

            try
            {
                if (OfficerPermissions.HasAccess(_config))
                {
                    OfficerChatWindow?.StartNetworking();
                }
            }
            catch (Exception ex)
            {
                _log.Error(ex, "Failed to start OfficerChatWindow networking during hard reload.");
            }

            try
            {
                if (_config.SyncedChat && _config.EnableFcChat)
                {
                    _ = ChatWindow?.RefreshChannels();
                }
                if (OfficerPermissions.HasAccess(_config))
                {
                    _ = OfficerChatWindow?.RefreshChannels();
                }
                _ = MainWindow?.EventCreateWindow.RefreshChannels();
                _ = MainWindow?.TemplatesWindow.RefreshChannels();
            }
            catch (Exception ex)
            {
                _log.Error(ex, "Failed to refresh channels during hard reload.");
            }

            try
            {
                if (_config.SyncedChat && _config.EnableFcChat)
                {
                    _ = ChatWindow?.RefreshMessages();
                }
                if (OfficerPermissions.HasAccess(_config))
                {
                    _ = OfficerChatWindow?.RefreshMessages();
                }
            }
            catch (Exception ex)
            {
                _log.Error(ex, "Failed to refresh messages during hard reload.");
            }

            try
            {
                MainWindow?.Ui.ResetChannels();
                MainWindow?.ResetEventCreateRoles();
                MainWindow?.TemplatesWindow.ResetRoles();
            }
            catch (Exception ex)
            {
                _log.Error(ex, "Failed to reset UI or roles during hard reload.");
            }

            try
            {
                presence?.Reset();
                presence?.Reload();
                presenceRestarted = presence != null;
            }
            catch (Exception ex)
            {
                _log.Error(ex, "Failed to reset or reload presence during hard reload.");
            }

            bool shouldOpenWindow;
            lock (_hardReloadLock)
            {
                shouldOpenWindow = _requestMainWindowOpen;
                _requestMainWindowOpen = false;
            }

            if (shouldOpenWindow && MainWindow != null)
            {
                if (framework != null)
                {
                    try
                    {
                        _ = framework.RunOnTick(() => MainWindow.IsOpen = true);
                    }
                    catch (Exception ex)
                    {
                        _log.Warning(ex, "Failed to open main window on framework thread during hard reload.");
                        MainWindow.IsOpen = true;
                    }
                }
                else
                {
                    MainWindow.IsOpen = true;
                }
            }
        }
        finally
        {
            presence?.SetPresenceReady(presenceRestarted);
            lock (_hardReloadLock)
            {
                _hardReloadTask = null;
            }
        }
    }

    private void ClearCachedData()
    {
        _config.Categories.Clear();
        SaveConfig();
        SyncshellWindow.Instance?.ClearCaches();
    }

    private async Task ForgetMe()
    {
        var framework = PluginServices.Instance?.Framework;
        try
        {
            if (_httpClient == null || !_tokenManager.IsReady() || !ApiHelpers.ValidateApiBaseUrl(_config))
                return;

            var url = $"{_config.ApiBaseUrl.TrimEnd('/')}/api/users/me/forget";
            var request = new HttpRequestMessage(HttpMethod.Post, url);
            ApiHelpers.AddAuthHeader(request, _tokenManager);
            var response = await _httpClient.SendAsync(request);
            if (response.IsSuccessStatusCode)
            {
                _tokenManager.Clear("User forgotten");
                _apiKey = string.Empty;
                ClearCachedData();
            }
        }
        catch (Exception ex)
        {
            _log.Error(ex, "Failed to forget user");
            if (framework != null)
                _ = framework.RunOnTick(() => _syncStatus = "Network error");
        }
    }

    private async Task Sync()
    {
        var framework = PluginServices.Instance?.Framework;
        try
        {
            if (framework == null)
            {
                _log.Error("Cannot sync: framework is not available.");
                if (framework != null)
                    _ = framework.RunOnTick(() => _syncStatus = "Network error");
                return;
            }

            if (_httpClient == null)
            {
                _log.Error("Cannot sync: HTTP client is not initialized.");
                _ = framework.RunOnTick(() => _syncStatus = "Network error");
                return;
            }

            if (!ApiHelpers.ValidateApiBaseUrl(_config))
            {
                _ = framework.RunOnTick(() => _syncStatus = "Network error");
                return;
            }

            if (PluginServices.Instance?.PluginInterface == null)
            {
                _log.Error("Cannot sync: plugin interface is not available.");
                _ = framework.RunOnTick(() => _syncStatus = "Network error");
                return;
            }

            try
            {
                _apiKey = _apiKey.Trim();
                if (string.IsNullOrWhiteSpace(_apiKey))
                {
                    _ = framework.RunOnTick(() => _syncStatus = "API key required");
                    return;
                }

                var key = _apiKey;
                var existingToken = _tokenManager.Token;
                if (!string.IsNullOrEmpty(existingToken) && !string.Equals(existingToken, key, StringComparison.Ordinal))
                {
                    StopAllWatchersAndPresence();
                    ClearRuntimeCaches();
                    _tokenManager.Clear("API key replaced");
                    SaveConfig();
                }

                var url = $"{_config.ApiBaseUrl.TrimEnd('/')}/validate";
                var request = new HttpRequestMessage(HttpMethod.Post, url)
                {
                    Content = new StringContent(JsonSerializer.Serialize(new { key }), Encoding.UTF8, "application/json")
                };
                ApiHelpers.AddAuthHeader(request, key);

                _log.Info($"Sync URL: {url}");
                _log.Info($"Headers: {string.Join(", ", request.Headers.Select(h => $"{h.Key}: {string.Join(";", h.Value)}"))}");

                _ = framework.RunOnTick(() => _syncStatus = "Validating API key...");
                var response = await _httpClient.SendAsync(request);
                var responseBody = await response.Content.ReadAsStringAsync();
                _log.Info($"Response Status: {response.StatusCode}");
                _log.Info($"Response Body: {responseBody}");
                if (response.IsSuccessStatusCode)
                {
                    _log.Info("API key validated successfully.");
                    var newKey = key;
                    _tokenManager.Set(newKey);
                    _apiKey = newKey;
                    _ = framework.RunOnTick(() => _syncStatus = "API key validated");
                    await HardReloadIdentityAndStartAsync(openMainWindow: true);
                }
                else if (response.StatusCode == HttpStatusCode.Unauthorized)
                {
                    _log.Warning($"API key validation failed: unauthorized. Response Body: {responseBody}");
                    _ = framework.RunOnTick(() => _syncStatus = "Authentication failed");
                }
                else
                {
                    _log.Warning($"API key validation failed with status {response.StatusCode}. Response Body: {responseBody}");
                    _ = framework.RunOnTick(() => _syncStatus = "Network error");
                }
            }
            catch (Exception ex)
            {
                _log.Error(ex, "Error validating API key.");
                _ = framework.RunOnTick(() => _syncStatus = "Network error");
                return;
            }
        }
        finally
        {
            _syncInProgress = false;
        }
    }

    private void SaveConfig()
    {
        var services = PluginServices.Instance;
        if (services?.PluginInterface == null)
        {
            _log.Error("Plugin interface is not available; cannot save configuration.");
            return;
        }

        if (services.Framework == null)
        {
            _log.Error("Framework is not available; cannot save configuration.");
            return;
        }

        _ = services.Framework.RunOnTick(() => services.PluginInterface.SavePluginConfig(_config));
    }

    public void Dispose()
    {
        _tokenManager.OnLinked -= OnLinked;
        _tokenManager.OnUnlinked -= OnUnlinked;
    }
}
