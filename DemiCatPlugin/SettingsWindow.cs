using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Numerics;
using System.Threading;
using System.Threading.Tasks;
using System.Linq;
using System.IO;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.ImGuiFileDialog;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;

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
    private static readonly Vector2 DefaultWindowSize = new(960f, 720f);

    private string _apiKey = string.Empty;
    private string _penumbraModsDirectory = string.Empty;
    private string _penumbraConfigDirectory = string.Empty;
    private string _syncStatus = string.Empty;
    private bool _syncInProgress;
    private readonly Dictionary<string, bool> _categoryToggles = new();
    private readonly object _hardReloadLock = new();
    private Task? _hardReloadTask;
    private bool _requestMainWindowOpen;
    private bool _settingsLoaded;
    private bool _isLinked;
    private static readonly int[] FadeDurations = { 5, 10, 15, 20, 30 };

    private readonly FileDialogManager _penumbraModsDialog = new();
    private readonly FileDialogManager _penumbraConfigDialog = new();
    private string _penumbraCollectionOverride = string.Empty;

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
        _penumbraModsDirectory = config.PenumbraModsDirectory ?? string.Empty;
        _penumbraConfigDirectory = config.PenumbraConfigDirectory ?? string.Empty;
        _penumbraCollectionOverride = config.PenumbraCollectionOverride ?? string.Empty;
        _devWindow = new DeveloperWindow(
            config,
            pluginInterface,
            () => _tokenManager.IsReady(),
            () => HardReloadIdentityAndStartAsync(),
            StopAllWatchersAndPresence);
        _log = log;
        _isLinked = _tokenManager.State == LinkState.Linked;
        _tokenManager.OnLinked += OnLinked;
        _tokenManager.OnUnlinked += OnUnlinked;
    }

    public void Draw()
    {
        if (IsOpen)
        {
            var colorPushCount = 0;
            try
            {
                var primaryColor = Config.SanitizeColor(_config.PrimaryWindowColor, Config.DefaultPrimaryWindowColor);
                primaryColor.W = 1f;
                ImGui.PushStyleColor(ImGuiCol.WindowBg, primaryColor);
                ImGui.PushStyleColor(ImGuiCol.ChildBg, primaryColor);
                colorPushCount = 2;

                ImGui.SetNextWindowSize(DefaultWindowSize, ImGuiCond.FirstUseEver);
                if (ImGui.Begin("DemiCat Settings", ref IsOpen))
                {
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

                        if (ImGui.BeginTabItem("Appearance"))
                        {
                            DrawAppearanceTab();
                            ImGui.EndTabItem();
                        }

                        if (ImGui.BeginTabItem("SyncShell Settings"))
                        {
                            DrawSyncshellTab();
                            ImGui.EndTabItem();
                        }

                        ImGui.EndTabBar();
                    }

                    ImGui.End();
                }
                else
                {
                    ImGui.End();
                }
            }
            finally
            {
                if (colorPushCount > 0)
                    ImGui.PopStyleColor(colorPushCount);
            }
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
            ImGui.Separator();
        }

        var effectiveApiBaseUrl = string.IsNullOrWhiteSpace(_config.ApiBaseUrl)
            ? Config.DefaultApiBaseUrl
            : _config.ApiBaseUrl;
        var isDefaultApiBaseUrl = string.Equals(effectiveApiBaseUrl, Config.DefaultApiBaseUrl, StringComparison.OrdinalIgnoreCase);
        ImGui.TextUnformatted($"API Base URL: {effectiveApiBaseUrl}");
        if (!isDefaultApiBaseUrl)
        {
            ImGui.SameLine();
            ImGui.TextDisabled("(override)");
        }

        // Allow room for longer server-generated API keys
        ImGui.InputText("API Key", ref _apiKey, 256);

        var enableFc = _config.EnableFcChat;
        if (!linked)
            ImGui.BeginDisabled();
        if (ImGui.Checkbox("Enable FC Chat", ref enableFc))
        {
            _config.EnableFcChat = enableFc;
            _config.EnableFcChatUserSet = true;
            SaveConfig();
            if (ChatWindow != null)
            {
                ChatWindow.ChannelsLoaded = false;
                if (enableFc)
                {
                    ChatWindow.StartNetworking();
                }
                else
                {
                    ChatWindow.StopNetworking();
                    var presenceService = ChatWindow.Presence ?? OfficerChatWindow?.Presence;
                    presenceService?.SetPresenceReady(false);
                    presenceService?.Stop();
                }
            }
        }
        if (!linked)
        {
            ImGui.EndDisabled();
            if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
                ImGui.SetTooltip("Link DemiCat to enable chat and presence.");
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

        var style = ImGui.GetStyle();
        var piGlyph = "\u03C0";
        var piSize = ImGui.CalcTextSize(piGlyph);
        var windowPos = ImGui.GetWindowPos();
        var contentRegionMax = ImGui.GetWindowContentRegionMax();
        var bottomRight = new Vector2(
            windowPos.X + contentRegionMax.X - style.WindowPadding.X - piSize.X,
            windowPos.Y + contentRegionMax.Y - style.WindowPadding.Y - piSize.Y);

        ImGui.SetCursorScreenPos(bottomRight);
        ImGui.TextDisabled(piGlyph);
        var io = ImGui.GetIO();
        if (ImGui.IsItemClicked() && io.KeyCtrl && io.KeyShift)
        {
            _devWindow.IsOpen = true;
        }
    }

    private void DrawSyncshellTab()
    {
        _penumbraModsDialog.Draw();
        _penumbraConfigDialog.Draw();

        var linked = _isLinked;
        ImGui.TextUnformatted("SyncShell Preferences");
        ImGui.Spacing();

        var syncEnabled = _config.FCSyncShell;
        if (!linked)
            ImGui.BeginDisabled();
        if (ImGui.Checkbox("Enable FC SyncShell", ref syncEnabled))
        {
            _config.FCSyncShell = syncEnabled;
            SaveConfig();
            MainWindow?.UpdateSyncshell();
            _ = Task.Run(PushSettings);
        }
        if (!linked)
        {
            ImGui.EndDisabled();
            if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
                ImGui.SetTooltip("Link DemiCat to enable SyncShell.");
        }

        var overlayVisible = _config.ShowSyncshellProgressOverlay;
        var overlayDisabled = !_config.FCSyncShell;
        if (overlayDisabled)
            ImGui.BeginDisabled();
        if (ImGui.Checkbox("Show Sync Progress Overlay", ref overlayVisible))
        {
            _config.ShowSyncshellProgressOverlay = overlayVisible;
            SaveConfig();
        }
        if (overlayDisabled)
        {
            ImGui.EndDisabled();
            if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
                ImGui.SetTooltip("Enable FC SyncShell to display transfer progress overlay.");
        }

        DrawPenumbraDirectorySettings();
        DrawPenumbraCollectionSettings();
    }

    private void DrawAppearanceTab()
    {
        var fadeOutEnabled = _config.ChatFadeOutEnabled;

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

        ImGui.Spacing();

        DrawDockSettings();

        ImGui.Spacing();

        ImGui.BeginDisabled(fadeOutEnabled);
        var fcOpacity = _config.FcChatOpacity * 100f;
        if (ImGui.SliderFloat("FC Chat window opacity", ref fcOpacity, 0f, 100f, "%.0f%%"))
        {
            _config.FcChatOpacity = Math.Clamp(fcOpacity / 100f, 0f, 1f);
            SaveConfig();
            MainWindow?.OnAppearanceSettingsChanged();
            MainWindow?.ResetFadeTimer();
        }
        ImGui.EndDisabled();
        if (fadeOutEnabled && ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            ImGui.SetTooltip("Disable fade-out to adjust per-tab opacity.");

        ImGui.BeginDisabled(fadeOutEnabled);
        var officerOpacity = _config.OfficerChatOpacity * 100f;
        if (ImGui.SliderFloat("Officer Chat window opacity", ref officerOpacity, 0f, 100f, "%.0f%%"))
        {
            _config.OfficerChatOpacity = Math.Clamp(officerOpacity / 100f, 0f, 1f);
            SaveConfig();
            MainWindow?.OnAppearanceSettingsChanged();
            MainWindow?.ResetFadeTimer();
        }
        ImGui.EndDisabled();
        if (fadeOutEnabled && ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            ImGui.SetTooltip("Disable fade-out to adjust per-tab opacity.");

        ImGui.NewLine();
        ImGui.Separator();

        if (ImGui.Checkbox("Enable fade-out", ref fadeOutEnabled))
        {
            _config.ChatFadeOutEnabled = fadeOutEnabled;
            SaveConfig();
            MainWindow?.OnAppearanceSettingsChanged();
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
                    MainWindow?.OnAppearanceSettingsChanged();
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
            MainWindow?.OnAppearanceSettingsChanged();
            MainWindow?.ResetFadeTimer();
        }

        ImGui.SameLine();
        DrawFadePreview("##fadePreview", _config.ChatFadeOutMinimumAlpha);
        ImGui.EndDisabled();
    }

    private void DrawDockSettings()
    {
        ImGui.Separator();
        ImGui.TextUnformatted("Dock");
        ImGui.Spacing();

        var dockLocked = _config.DockLocked;
        if (ImGui.Checkbox("Lock dock position", ref dockLocked))
        {
            _config.DockLocked = dockLocked;
            SaveConfig();
        }

        var rememberPosition = _config.DockRememberPosition;
        if (ImGui.Checkbox("Remember dock position between sessions", ref rememberPosition))
        {
            _config.DockRememberPosition = rememberPosition;
            SaveConfig();
            if (rememberPosition)
            {
                MainWindow?.ResetDockPosition();
            }
        }

        var iconScale = _config.DockIconScale;
        if (ImGui.SliderFloat("Dock icon scale", ref iconScale, Config.MinDockIconScale, Config.MaxDockIconScale, "%.2fx"))
        {
            _config.DockIconScale = Config.SanitizeDockIconScale(iconScale);
            SaveConfig();
        }

        var dockColor = _config.DockBackgroundColor;
        var colorEditFlags = ImGuiColorEditFlags.AlphaBar | ImGuiColorEditFlags.AlphaPreviewHalf;
        if (ImGui.ColorEdit4("Dock background color", ref dockColor, colorEditFlags))
        {
            var sanitized = Config.SanitizeDockBackgroundColor(dockColor);
            if (!ColorsAlmostEqual(sanitized, _config.DockBackgroundColor))
            {
                _config.DockBackgroundColor = sanitized;
                _config.DockBackgroundAlpha = sanitized.W;
                SaveConfig();
                MainWindow?.OnAppearanceSettingsChanged();
            }
        }

        var gradientEnabled = _config.DockGradientEnabled;
        if (ImGui.Checkbox("Enable dock gradient", ref gradientEnabled))
        {
            _config.DockGradientEnabled = gradientEnabled;
            SaveConfig();
            MainWindow?.OnAppearanceSettingsChanged();
        }

        var shouldDisableGradientOptions = !gradientEnabled;
        if (shouldDisableGradientOptions)
        {
            ImGui.BeginDisabled();
        }

        var gradientTop = _config.DockGradientTopColor;
        if (ImGui.ColorEdit4("Gradient top color", ref gradientTop, colorEditFlags))
        {
            var sanitized = Config.SanitizeDockGradientColor(gradientTop);
            if (!ColorsAlmostEqual(sanitized, _config.DockGradientTopColor))
            {
                _config.DockGradientTopColor = sanitized;
                SaveConfig();
                MainWindow?.OnAppearanceSettingsChanged();
            }
        }

        var gradientBottom = _config.DockGradientBottomColor;
        if (ImGui.ColorEdit4("Gradient bottom color", ref gradientBottom, colorEditFlags))
        {
            var sanitized = Config.SanitizeDockGradientColor(gradientBottom);
            if (!ColorsAlmostEqual(sanitized, _config.DockGradientBottomColor))
            {
                _config.DockGradientBottomColor = sanitized;
                SaveConfig();
                MainWindow?.OnAppearanceSettingsChanged();
            }
        }

        if (shouldDisableGradientOptions)
        {
            ImGui.EndDisabled();
        }

        ImGui.Spacing();
        ImGui.TextUnformatted("Auto-open windows");
        ImGui.Spacing();

        var options = new List<(string Id, string Label, bool Available)>
        {
            ("events", "Events", _config.Events),
            ("create", "Create Event", _config.Events),
            ("templates", "Templates", _config.Templates),
            ("requests", "Request Board", _config.Requests),
            ("notepad", "NotePad", _config.NotePadEnabled)
        };

        if (MainWindow?.ChatWindowHost != null && ChatWindow != null)
        {
            var chatLabel = ChatWindow is FcChatWindow ? "FC Chat" : "Chat";
            options.Add(("chat", chatLabel, true));
        }

        if (OfficerChatWindow != null)
        {
            options.Add(("officer", "Officer Chat", MainWindow?.HasOfficerAccess ?? false));
        }

        var syncshellAvailable = _config.FCSyncShell && (MainWindow?.SyncshellWindowHost != null);
        options.Add(("syncshell", "Syncshell", syncshellAvailable));

        foreach (var option in options)
        {
            var autoShow = _config.GetDockAutoShow(option.Id);
            var available = option.Available;

            if (!available)
            {
                ImGui.BeginDisabled();
            }

            var label = $"Auto-open {option.Label}";
            if (ImGui.Checkbox(label, ref autoShow))
            {
                _config.SetDockAutoShow(option.Id, autoShow);
                SaveConfig();
                MainWindow?.RefreshDockAutoShow();
            }

            if (!available)
            {
                ImGui.EndDisabled();
                if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
                {
                    ImGui.SetTooltip("Enable this feature to auto-open the window.");
                }
            }
        }
    }

    private void RefreshChatWindows()
    {
        ChatWindow?.OnAppearanceSettingsChanged();
        OfficerChatWindow?.OnAppearanceSettingsChanged();
    }

    private static bool ColorsAlmostEqual(Vector4 a, Vector4 b)
    {
        return Vector4.DistanceSquared(a, b) <= 0.0001f;
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
                _config.FCSyncShell = consent.GetBoolean();
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
                consent_sync = _config.FCSyncShell
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
        _config.PenumbraModsDirectory = string.Empty;
        _config.PenumbraConfigDirectory = string.Empty;
        _config.PenumbraCollectionOverride = string.Empty;
        _penumbraModsDirectory = string.Empty;
        _penumbraConfigDirectory = string.Empty;
        _penumbraCollectionOverride = string.Empty;
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
                if (_config.EnableFcChat)
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
                if (_config.EnableFcChat)
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
                if (_config.EnableFcChat)
                {
                    _ = ChatWindow?.RequestRefreshMessagesAsync();
                }
                if (OfficerPermissions.HasAccess(_config))
                {
                    _ = OfficerChatWindow?.RequestRefreshMessagesAsync();
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

    private void DrawPenumbraDirectorySettings()
    {
        ImGui.Separator();
        ImGui.TextUnformatted("Penumbra Directories");
        ImGui.TextDisabled("Override the Penumbra folders used by SyncShell.");
        ImGui.Spacing();

        ImGuiDirectoryPickerHelper.DrawDirectoryPicker(
            "Penumbra Mods Directory",
            "Leave blank to use Penumbra's configured mods directory.",
            () => _penumbraModsDirectory,
            SetPenumbraModsDirectory,
            _penumbraModsDialog,
            "PenumbraMods",
            NormalizeDirectory,
            OpenFolderDialog);

        ImGuiDirectoryPickerHelper.DrawDirectoryPicker(
            "Penumbra Config Directory",
            "Should contain default_mod.json. Leave blank to auto-detect.",
            () => _penumbraConfigDirectory,
            SetPenumbraConfigDirectory,
            _penumbraConfigDialog,
            "PenumbraConfig",
            NormalizeDirectory,
            OpenFolderDialog);
    }

    private void DrawPenumbraCollectionSettings()
    {
        ImGui.Separator();
        ImGui.TextUnformatted("Penumbra Collection");
        ImGui.TextDisabled("Choose the collection used when syncing.");
        ImGui.Spacing();

        var collection = _penumbraCollectionOverride;
        if (ImGui.InputText("Active collection override", ref collection, 260))
        {
            SetPenumbraCollectionOverride(collection);
        }

        var suggestions = EnumeratePenumbraCollectionSuggestions();
        var preview = GetCollectionPreview(_penumbraCollectionOverride, suggestions);
        if (suggestions.Count > 0 && ImGui.BeginCombo("##syncshell-collection", preview))
        {
            foreach (var option in suggestions)
            {
                var selected = string.Equals(_penumbraCollectionOverride, option.Identifier, StringComparison.OrdinalIgnoreCase);
                if (ImGui.Selectable(option.Display, selected))
                {
                    SetPenumbraCollectionOverride(option.Identifier);
                    collection = _penumbraCollectionOverride;
                }

                if (selected)
                {
                    ImGui.SetItemDefaultFocus();
                }
            }

            ImGui.EndCombo();
        }

        ImGui.TextDisabled(suggestions.Count > 0
            ? "Select from detected collections or enter a custom value (e.g. default)."
            : "Enter the collection name or file (e.g. default or default_mod.json).");
    }

    private void OpenFolderDialog(
        FileDialogManager dialog,
        string label,
        string? currentPath,
        Action<string?> setter)
    {
        var initial = NormalizeDirectory(currentPath);
        if (string.IsNullOrEmpty(initial) || !Directory.Exists(initial))
        {
            initial = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        }

        if (string.IsNullOrEmpty(initial) || !Directory.Exists(initial))
        {
            initial = Environment.CurrentDirectory;
        }

        dialog.OpenFolderDialog($"Select {label}", (success, selected) =>
        {
            if (!success || string.IsNullOrWhiteSpace(selected))
            {
                return;
            }

            var normalized = NormalizeDirectory(selected);
            var framework = PluginServices.Instance?.Framework;
            if (framework != null)
            {
                _ = framework.RunOnTick(() => setter(normalized));
            }
            else
            {
                setter(normalized);
            }
        }, initial);
    }

    private void SetPenumbraModsDirectory(string? path)
    {
        var normalized = NormalizeDirectory(path);
        if (string.Equals(_penumbraModsDirectory, normalized, StringComparison.Ordinal))
        {
            return;
        }

        _penumbraModsDirectory = normalized;
        _config.PenumbraModsDirectory = normalized;
        SaveConfig();
        SyncshellWindow.NotifyPenumbraOverridesChanged(_config);
    }

    private void SetPenumbraConfigDirectory(string? path)
    {
        var normalized = NormalizeDirectory(path);
        if (string.Equals(_penumbraConfigDirectory, normalized, StringComparison.Ordinal))
        {
            return;
        }

        _penumbraConfigDirectory = normalized;
        _config.PenumbraConfigDirectory = normalized;
        SaveConfig();
        SyncshellWindow.NotifyPenumbraOverridesChanged(_config);
    }

    private void SetPenumbraCollectionOverride(string? value)
    {
        var normalized = string.IsNullOrWhiteSpace(value) ? string.Empty : value.Trim();
        if (string.Equals(_penumbraCollectionOverride, normalized, StringComparison.Ordinal))
        {
            return;
        }

        _penumbraCollectionOverride = normalized;
        _config.PenumbraCollectionOverride = normalized;
        SaveConfig();
        SyncshellWindow.NotifyPenumbraOverridesChanged(_config);
    }

    private static string NormalizeDirectory(string? value)
        => string.IsNullOrWhiteSpace(value) ? string.Empty : value.Trim();

    private List<CollectionOption> EnumeratePenumbraCollectionSuggestions()
    {
        var results = new List<CollectionOption>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var baseDir = NormalizeDirectory(_config.PenumbraConfigDirectory);
        if (string.IsNullOrEmpty(baseDir) || !Directory.Exists(baseDir))
        {
            return results;
        }

        string Describe(string identifier, string relative)
            => string.IsNullOrEmpty(relative) ? identifier : $"{identifier} ({relative})";

        void AddSuggestion(string filePath)
        {
            if (!File.Exists(filePath))
            {
                return;
            }

            string relative;
            try
            {
                relative = Path.GetRelativePath(baseDir, filePath);
            }
            catch
            {
                relative = Path.GetFileName(filePath) ?? filePath;
            }

            var identifier = Path.GetFileNameWithoutExtension(filePath);
            if (string.IsNullOrEmpty(identifier))
            {
                return;
            }

            if (identifier.EndsWith("_mod", StringComparison.OrdinalIgnoreCase))
            {
                var trimmed = identifier[..^4];
                if (seen.Add(trimmed))
                {
                    results.Add(new CollectionOption(trimmed, Describe(trimmed, relative)));
                }
            }

            if (seen.Add(identifier))
            {
                results.Add(new CollectionOption(identifier, Describe(identifier, relative)));
            }
        }

        try
        {
            foreach (var file in Directory.EnumerateFiles(baseDir, "*_mod.json", SearchOption.TopDirectoryOnly))
            {
                AddSuggestion(file);
            }

            foreach (var file in Directory.EnumerateFiles(baseDir, "*.json", SearchOption.TopDirectoryOnly))
            {
                AddSuggestion(file);
            }

            var collectionsDir = Path.Combine(baseDir, "collections");
            if (Directory.Exists(collectionsDir))
            {
                foreach (var file in Directory.EnumerateFiles(collectionsDir, "*.json", SearchOption.TopDirectoryOnly))
                {
                    AddSuggestion(file);
                }
            }
        }
        catch (Exception ex)
        {
            PluginServices.Instance?.Log?.Debug(ex, "Failed to enumerate Penumbra collections in {Directory}", baseDir);
        }

        results.Sort((a, b) => string.Compare(a.Identifier, b.Identifier, StringComparison.OrdinalIgnoreCase));
        return results;
    }

    private static string GetCollectionPreview(string? currentValue, IReadOnlyList<CollectionOption> options)
    {
        if (string.IsNullOrWhiteSpace(currentValue))
        {
            return "Auto-detect (default)";
        }

        foreach (var option in options)
        {
            if (string.Equals(option.Identifier, currentValue, StringComparison.OrdinalIgnoreCase))
            {
                return option.Display;
            }
        }

        return currentValue;
    }

    private sealed record CollectionOption(string Identifier, string Display);

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
