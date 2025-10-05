using Dalamud.Configuration;
using System;
using System.Collections.Generic;
using System.Numerics;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DemiCatPlugin;

public class Config : IPluginConfiguration
{
    public const float MinChatFontScale = 0.75f;
    public const float MaxChatFontScale = 1.5f;
    public const float MinChatImageScale = 0.5f;
    public const float MaxChatImageScale = 3f;
    public const float MinDockIconScale = 0.75f;
    public const float MaxDockIconScale = 2.5f;
    public static readonly Vector4 DefaultPrimaryWindowColor = new(0.11f, 0.11f, 0.12f, 1f);
    public static readonly Vector4 DefaultSecondaryAccentColor = new(0.2f, 0.6f, 1f, 1f);
    public static readonly Vector4 DefaultDockBackgroundColor = new(0.11f, 0.11f, 0.12f, 0.9f);
    public static readonly Vector4 DefaultDockGradientTopColor = new(0.16f, 0.16f, 0.17f, 0.9f);
    public static readonly Vector4 DefaultDockGradientBottomColor = new(0.08f, 0.08f, 0.09f, 0.9f);

    public static class FadePreferenceKeys
    {
        public const string Events = "events";
        public const string EventCreate = "create";
        public const string Templates = "templates";
        public const string NotePad = "notepad";
        public const string Requests = "requests";
        public const string Chat = "chat";
        public const string OfficerChat = "officer";
        public const string Syncshell = "syncshell";
    }

    // Required by Dalamud
    public const float MinEmojiTileSize = 16f;
    public const float MaxEmojiTileSize = 64f;
    public const float MinEmojiGridHeight = 120f;
    public const float MaxEmojiGridHeight = 800f;
    public const uint DefaultFcEmbedColor = 0x5865F2;
    public const uint DefaultOfficerEmbedColor = 0xED4245;
    public const string DefaultEmbedBorderGlyph = "⬛";
    public const int DefaultChatMaxMessages = 100;
    public const int MinChatMaxMessages = 20;
    public const int MaxChatMaxMessages = 500;
    public const int DefaultTextureCacheCapacity = 100;
    public const int MinTextureCacheCapacity = 10;
    public const int MaxTextureCacheCapacity = 500;
    public const int DefaultImageMaxDecodeWidth = 4096;
    public const int DefaultImageMaxDecodeHeight = 4096;
    public const int MinImageDecodeDimension = 256;
    public const int MaxImageDecodeDimension = 8192;
    public const int DefaultImageDownloadConcurrency = 4;
    public const int DefaultImageDecodeConcurrency = 2;
    public const int MinImageConcurrency = 1;
    public const int MaxImageDownloadConcurrency = 16;
    public const int MaxImageDecodeConcurrency = 8;
    public const long DefaultImageBytesInFlightBudget = 32L * 1024L * 1024L;
    public const long MinImageBytesInFlightBudget = 4L * 1024L * 1024L;
    public const long MaxImageBytesInFlightBudget = 512L * 1024L * 1024L;
    public const int DefaultPreloadRowsAhead = 24;
    public const int MinPreloadRowsAhead = 0;
    public const int MaxPreloadRowsAhead = 32;
    public const int CurrentVersion = 26;

    public int Version { get; set; } = CurrentVersion;

    public sealed class EmbedBorderSettings
    {
        [JsonPropertyName("enabled")]
        public bool Enabled { get; set; }

        [JsonPropertyName("glyph")]
        public string Glyph { get; set; } = DefaultEmbedBorderGlyph;

        [JsonPropertyName("color")]
        public uint Color { get; set; }

        public EmbedBorderSettings Clone()
            => new()
            {
                Enabled = Enabled,
                Glyph = SanitizeEmbedBorderGlyph(Glyph),
                Color = Color
            };

        public static EmbedBorderSettings CreateDefault(string? channelKind)
            => new()
            {
                Enabled = false,
                Glyph = DefaultEmbedBorderGlyph,
                Color = GetDefaultEmbedColor(channelKind)
            };
    }

    public bool Enabled { get; set; } = true;
    public const string DefaultApiBaseUrl = "https://mew.the-demiurge.com";

    [JsonPropertyName("apiBaseUrl")]
    public string ApiBaseUrl { get; set; } = DefaultApiBaseUrl;
    public string WebSocketPath { get; set; } = "/ws/embeds";
    public int PollIntervalSeconds { get; set; } = 5;
    [JsonPropertyName("guildId")]
    public string GuildId { get; set; } = string.Empty;
    public string ChatChannelId { get; set; } = string.Empty;
    public Dictionary<string, long> ChatCursors { get; set; } = new();
    public Dictionary<string, long> RestChatCursors { get; set; } = new();
    public Dictionary<string, string> ChannelSelections { get; set; } = new();
    public string EventChannelId { get; set; } = string.Empty;
    public string RequestsChannelId { get; set; } = string.Empty;
    public string FcChannelId { get; set; } = string.Empty;
    public string FcChannelName { get; set; } = string.Empty;
    public string OfficerChannelId { get; set; } = string.Empty;
    public bool EnableFcChat { get; set; } = true;
    public bool EnableFcChatUserSet { get; set; } = false;
    public float FcChatOpacity { get; set; } = 1f;
    public float OfficerChatOpacity { get; set; } = 1f;
    [JsonPropertyName("fcEmbedColor")]
    public uint? FcEmbedColor { get; set; } = DefaultFcEmbedColor;
    [JsonPropertyName("officerEmbedColor")]
    public uint? OfficerEmbedColor { get; set; } = DefaultOfficerEmbedColor;
    public bool ChatFadeOutEnabled { get; set; } = false;
    public int ChatFadeOutDelaySeconds { get; set; } = 10;
    public float ChatFadeOutMinimumAlpha { get; set; } = 0.3f;

    [JsonPropertyName("windowFadePreferences")]
    public Dictionary<string, bool> WindowFadePreferences { get; set; } = new();

    [JsonPropertyName("dockVisible")]
    public bool DockVisible { get; set; } = true;

    [JsonPropertyName("dockLocked")]
    public bool DockLocked { get; set; }

    [JsonPropertyName("dockIconScale")]
    public float DockIconScale { get; set; } = 1f;

    [JsonPropertyName("dockBackgroundAlpha")]
    public float DockBackgroundAlpha { get; set; } = 0.9f;

    [JsonPropertyName("dockBackgroundColor")]
    public Vector4 DockBackgroundColor { get; set; } = DefaultDockBackgroundColor;

    [JsonPropertyName("dockGradientEnabled")]
    public bool DockGradientEnabled { get; set; }

    [JsonPropertyName("dockGradientTopColor")]
    public Vector4 DockGradientTopColor { get; set; } = DefaultDockGradientTopColor;

    [JsonPropertyName("dockGradientBottomColor")]
    public Vector4 DockGradientBottomColor { get; set; } = DefaultDockGradientBottomColor;

    [JsonPropertyName("dockPosition")]
    public Vector2 DockPosition { get; set; } = Vector2.Zero;

    [JsonPropertyName("dockPositionInitialized")]
    public bool DockPositionInitialized { get; set; }

    [JsonPropertyName("dockRememberPosition")]
    public bool DockRememberPosition { get; set; } = true;

    [JsonPropertyName("dockAutoShow")]
    public Dictionary<string, bool> DockAutoShow { get; set; } = new();

    [JsonPropertyName("dockOrder")]
    public List<string> DockOrder { get; set; } = new();

    public float ChatFontScale { get; set; } = 1f;
    public bool ChatImageAutoStretch { get; set; } = true;
    public float ChatImageManualScale { get; set; } = 1f;
    public int ChatMaxMessages { get; set; } = DefaultChatMaxMessages;
    public int TextureCacheCapacity { get; set; } = DefaultTextureCacheCapacity;
    public int ImageMaxDecodeWidth { get; set; } = DefaultImageMaxDecodeWidth;
    public int ImageMaxDecodeHeight { get; set; } = DefaultImageMaxDecodeHeight;
    public int ImageDownloadConcurrency { get; set; } = DefaultImageDownloadConcurrency;
    public int ImageDecodeConcurrency { get; set; } = DefaultImageDecodeConcurrency;
    public long ImageBytesInFlightBudget { get; set; } = DefaultImageBytesInFlightBudget;
    public int PreloadRowsAhead { get; set; } = DefaultPreloadRowsAhead;
    public bool LazyLoadEmbeds { get; set; } = false;

    [JsonPropertyName("emojiTileSize")]
    public float EmojiTileSize { get; set; } = 28f;

    [JsonPropertyName("emojiGridHeight")]
    public float EmojiGridHeight { get; set; } = 220f;

    [JsonPropertyName("primaryWindowColor")]
    public Vector4 PrimaryWindowColor { get; set; } = DefaultPrimaryWindowColor;

    [JsonPropertyName("secondaryAccentColor")]
    public Vector4 SecondaryAccentColor { get; set; } = DefaultSecondaryAccentColor;

    [JsonPropertyName("chatInputSplitRatio")]
    public float ChatInputSplitRatio { get; set; } = 0.35f;

    [JsonPropertyName("syncedChat")]
    public bool SyncedChat { get; set; } = true;
    [JsonPropertyName("events")]
    public bool Events { get; set; } = true;
    [JsonPropertyName("templatesEnabled")]
    public bool Templates { get; set; } = true;
    [JsonPropertyName("requests")]
    public bool Requests { get; set; } = true;
    [JsonPropertyName("officer")]
    public bool Officer { get; set; } = true;
    [JsonPropertyName("isOfficerToken")]
    public bool IsOfficerToken { get; set; }
    [JsonPropertyName("fcSyncShell")]
    public bool FCSyncShell { get; set; } = false;
    [JsonPropertyName("showSyncshellProgressOverlay")]
    public bool ShowSyncshellProgressOverlay { get; set; } = true;

    [JsonPropertyName("syncshellPeerSyncEnabled")]
    public bool SyncshellPeerSyncEnabled { get; set; } = true;

    [JsonPropertyName("syncshellCacheLimitMb")]
    public int SyncshellCacheLimitMb { get; set; } = 4096;

    [JsonPropertyName("syncshellAutoSyncAllUsers")]
    public bool SyncshellAutoSyncAllUsers { get; set; } = true;

    [JsonPropertyName("syncshellManualSyncAllUsers")]
    public bool SyncshellManualSyncAllUsers { get; set; }

    [JsonPropertyName("syncshellManualSyncCustom")]
    public bool SyncshellManualSyncCustom { get; set; }
    [JsonPropertyName("fcEmbedBorder")]
    public EmbedBorderSettings FcEmbedBorder { get; set; } = EmbedBorderSettings.CreateDefault(ChannelKind.FcChat);

    [JsonPropertyName("officerEmbedBorder")]
    public EmbedBorderSettings OfficerEmbedBorder { get; set; } = EmbedBorderSettings.CreateDefault(ChannelKind.OfficerChat);
    public bool UseCharacterName { get; set; } = false;
    public List<string> Roles { get; set; } = new();
    [JsonPropertyName("mentionRoleIds")]
    public List<string> MentionRoleIds { get; set; } = new();
    public List<RoleDto> GuildRoles { get; set; } = new();
    [JsonPropertyName("templates")]
    public List<Template> TemplateData { get; set; } = new();
    public List<SignupPreset> SignupPresets { get; set; } = new();

    [JsonPropertyName("requestStates")]
    public List<RequestState> RequestStates { get; set; } = new();

    [JsonPropertyName("requestsDeltaToken")]
    public string? RequestsDeltaToken { get; set; }


    [JsonPropertyName("autoApply")]
    public Dictionary<string, bool> AutoApply { get; set; } = new();

    [JsonPropertyName("penumbraModsDirectory")]
    public string PenumbraModsDirectory { get; set; } = string.Empty;

    [JsonPropertyName("penumbraConfigDirectory")]
    public string PenumbraConfigDirectory { get; set; } = string.Empty;

    [JsonPropertyName("penumbraChoices")]
    public Dictionary<string, bool> PenumbraChoices { get; set; } = new();

    [JsonPropertyName("penumbraCollectionOverride")]
    public string PenumbraCollectionOverride
    {
        get => _penumbraCollectionOverride;
        set => _penumbraCollectionOverride = value?.Trim() ?? string.Empty;
    }

    private string _penumbraCollectionOverride = string.Empty;

    [JsonPropertyName("categories")]
    public Dictionary<string, CategoryState> Categories { get; set; } = new();

    [JsonPropertyName("notePadEnabled")]
    public bool NotePadEnabled { get; set; } = true;

    [JsonPropertyName("notePadLastSectionId")]
    public string? NotePadLastSectionId { get; set; }

    [JsonPropertyName("notePadLastPageId")]
    public string? NotePadLastPageId { get; set; }

    [JsonPropertyName("notePadPageListWidthRatio")]
    public float NotePadPageListWidthRatio { get; set; } = 0.3f;

    [JsonPropertyName("notePadEditorZoom")]
    public float NotePadEditorZoom { get; set; } = 1f;

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? ExtensionData { get; set; }

    public class CategoryState
    {
        [JsonPropertyName("lastPullAt")]
        public DateTimeOffset? LastPullAt { get; set; }

        [JsonPropertyName("seenAssets")]
        public HashSet<string> SeenAssets { get; set; } = new();

        [JsonPropertyName("paused")]
        public bool Paused { get; set; }

        [JsonPropertyName("lastResyncAt")]
        public DateTimeOffset? LastResyncAt { get; set; }

        [JsonPropertyName("pairingExpiresAt")]
        public DateTimeOffset? PairingExpiresAt { get; set; }

        [JsonPropertyName("invites")]
        public List<SyncshellInviteState> Invites { get; set; } = new();

        [JsonPropertyName("membershipPanelRatios")]
        public Dictionary<string, float> MembershipPanelRatios { get; set; } = new();

        [JsonPropertyName("syncSettingsHeight")]
        public float? SyncSettingsHeight { get; set; }

        [JsonExtensionData]
        public Dictionary<string, JsonElement>? ExtensionData { get; set; }
    }

    public class SyncshellInviteState
    {
        [JsonPropertyName("target")]
        public string Target { get; set; } = string.Empty;

        [JsonPropertyName("status")]
        public string Status { get; set; } = "pending";

        [JsonPropertyName("updatedAt")]
        public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;

        [JsonPropertyName("requestId")]
        public string? RequestId { get; set; }

        [JsonPropertyName("direction")]
        public string? Direction { get; set; }
    }

    public void Migrate()
    {
        RestChatCursors ??= new Dictionary<string, long>();

        if (Version < 3)
        {
            if (ExtensionData != null)
            {
                if (ExtensionData.TryGetValue("HelperBaseUrl", out var helperUrl) && helperUrl.ValueKind == JsonValueKind.String)
                {
                    ApiBaseUrl = helperUrl.GetString() ?? ApiBaseUrl;
                }
                else if (ExtensionData.TryGetValue("ServerAddress", out var serverUrl) && serverUrl.ValueKind == JsonValueKind.String)
                {
                    ApiBaseUrl = serverUrl.GetString() ?? ApiBaseUrl;
                }
            }
            Version = 3;
            ExtensionData = null;
        }
        if (Version < 4)
        {
            if (ExtensionData != null)
            {
                if (ExtensionData.TryGetValue("requestStates", out var reqStates) && reqStates.ValueKind == JsonValueKind.Array)
                {
                    try
                    {
                        RequestStates = JsonSerializer.Deserialize<List<RequestState>>(reqStates.GetRawText()) ?? new();
                    }
                    catch
                    {
                        // ignore
                    }
                }
                if (ExtensionData.TryGetValue("requestsDeltaToken", out var tokEl) && tokEl.ValueKind == JsonValueKind.String)
                {
                    RequestsDeltaToken = tokEl.GetString();
                }
            }
            Version = 4;
            ExtensionData = null;
        }
        if (Version < 5)
        {
            ChannelSelections ??= new Dictionary<string, string>();

            void EnsureSelection(string kind, string? channelId)
            {
                if (string.IsNullOrEmpty(channelId)) return;
                var key = ChannelKeyHelper.BuildSelectionKey(GuildId, kind);
                if (!ChannelSelections.ContainsKey(key))
                {
                    ChannelSelections[key] = channelId;
                }
            }

            EnsureSelection(ChannelKind.Chat, ChatChannelId);
            EnsureSelection(ChannelKind.Event, EventChannelId);
            EnsureSelection(ChannelKind.Requests, RequestsChannelId);
            EnsureSelection(ChannelKind.FcChat, FcChannelId);
            EnsureSelection(ChannelKind.OfficerChat, OfficerChannelId);

            if (ChatCursors.Count > 0)
            {
                var migrated = new Dictionary<string, long>();
                foreach (var kvp in ChatCursors)
                {
                    var channelId = kvp.Key;
                    var cursor = kvp.Value;
                    if (string.IsNullOrEmpty(channelId))
                        continue;

                    var kinds = new List<string>();
                    if (!string.IsNullOrEmpty(EventChannelId) && channelId == EventChannelId)
                        kinds.Add(ChannelKind.Event);
                    if (!string.IsNullOrEmpty(RequestsChannelId) && channelId == RequestsChannelId)
                        kinds.Add(ChannelKind.Requests);
                    if (!string.IsNullOrEmpty(FcChannelId) && channelId == FcChannelId)
                        kinds.Add(ChannelKind.FcChat);
                    if (!string.IsNullOrEmpty(OfficerChannelId) && channelId == OfficerChannelId)
                        kinds.Add(ChannelKind.OfficerChat);
                    if (!string.IsNullOrEmpty(ChatChannelId) && channelId == ChatChannelId)
                        kinds.Add(ChannelKind.Chat);
                    if (kinds.Count == 0)
                        kinds.Add(ChannelKind.FcChat);

                    foreach (var kind in kinds)
                    {
                        var key = ChannelKeyHelper.BuildCursorKey(GuildId, kind, channelId);
                        if (!migrated.ContainsKey(key))
                        {
                            migrated[key] = cursor;
                        }
                    }
                }
                ChatCursors = migrated;
            }

            Version = 5;
            ExtensionData = null;
        }
        if (Version < 6)
        {
            if (ExtensionData != null)
            {
                if (ExtensionData.TryGetValue("FcChatTransparency", out var fcOpacityElement) && fcOpacityElement.ValueKind == JsonValueKind.Number && fcOpacityElement.TryGetDouble(out var fcOpacity))
                {
                    FcChatOpacity = (float)Math.Clamp(fcOpacity, 0d, 1d);
                }
                if (ExtensionData.TryGetValue("OfficerChatTransparency", out var officerOpacityElement) && officerOpacityElement.ValueKind == JsonValueKind.Number && officerOpacityElement.TryGetDouble(out var officerOpacity))
                {
                    OfficerChatOpacity = (float)Math.Clamp(officerOpacity, 0d, 1d);
                }
                if (ExtensionData.TryGetValue("ChatFadeOutAlpha", out var fadeAlphaElement) && fadeAlphaElement.ValueKind == JsonValueKind.Number && fadeAlphaElement.TryGetDouble(out var fadeAlpha))
                {
                    ChatFadeOutMinimumAlpha = (float)Math.Clamp(fadeAlpha, 0d, 1d);
                }
            }

            FcChatOpacity = Math.Clamp(FcChatOpacity, 0f, 1f);
            OfficerChatOpacity = Math.Clamp(OfficerChatOpacity, 0f, 1f);
            ChatFadeOutMinimumAlpha = Math.Clamp(ChatFadeOutMinimumAlpha, 0f, 1f);
            if (ChatFadeOutDelaySeconds <= 0)
            {
                ChatFadeOutDelaySeconds = 10;
            }

            Version = 6;
            ExtensionData = null;
        }
        if (Version < 7)
        {
            if (RestChatCursors.Count == 0 && ChatCursors.Count > 0)
            {
                foreach (var kvp in ChatCursors)
                {
                    if (!RestChatCursors.ContainsKey(kvp.Key))
                    {
                        RestChatCursors[kvp.Key] = kvp.Value;
                    }
                }
            }

            Version = 7;
            ExtensionData = null;
        }
        if (Version < 8)
        {
            if (float.IsNaN(ChatInputSplitRatio) || float.IsInfinity(ChatInputSplitRatio) || ChatInputSplitRatio <= 0f)
            {
                ChatInputSplitRatio = 0.35f;
            }

            ChatInputSplitRatio = Math.Clamp(ChatInputSplitRatio, 0.2f, 0.8f);

            Version = 8;
            ExtensionData = null;
        }
        if (Version < 9)
        {
            IsOfficerToken = false;
            Version = 9;
            ExtensionData = null;
        }
        if (Version < 10)
        {
            if (!float.IsFinite(ChatFontScale) || ChatFontScale <= 0f)
            {
                ChatFontScale = 1f;
            }
            ChatFontScale = Math.Clamp(ChatFontScale, MinChatFontScale, MaxChatFontScale);

            ChatImageAutoStretch = true;

            if (!float.IsFinite(ChatImageManualScale) || ChatImageManualScale <= 0f)
            {
                ChatImageManualScale = 1f;
            }
            ChatImageManualScale = Math.Clamp(ChatImageManualScale, MinChatImageScale, MaxChatImageScale);

            Version = 10;
            ExtensionData = null;
        }
        if (Version < 11)
        {
            PrimaryWindowColor = SanitizeColor(PrimaryWindowColor, DefaultPrimaryWindowColor);
            SecondaryAccentColor = SanitizeColor(SecondaryAccentColor, DefaultSecondaryAccentColor);

            Version = 11;
            ExtensionData = null;
        }
        if (Version < 12)
        {
            NotePadEnabled = true;
            if (string.IsNullOrWhiteSpace(NotePadLastSectionId))
            {
                NotePadLastSectionId = null;
            }

            if (string.IsNullOrWhiteSpace(NotePadLastPageId))
            {
                NotePadLastPageId = null;
            }

            if (!float.IsFinite(NotePadPageListWidthRatio) || NotePadPageListWidthRatio <= 0f)
            {
                NotePadPageListWidthRatio = 0.3f;
            }
            NotePadPageListWidthRatio = Math.Clamp(NotePadPageListWidthRatio, 0.15f, 0.6f);

            if (!float.IsFinite(NotePadEditorZoom) || NotePadEditorZoom <= 0f)
            {
                NotePadEditorZoom = 1f;
            }
            NotePadEditorZoom = Math.Clamp(NotePadEditorZoom, 0.5f, 2f);

            Version = 12;
            ExtensionData = null;
        }
        if (Version < 13)
        {
            EmojiTileSize = SanitizeEmojiTileSize(EmojiTileSize);
            EmojiGridHeight = SanitizeEmojiGridHeight(EmojiGridHeight);

            Version = 13;
            ExtensionData = null;
        }
        if (Version < 14)
        {
            FcEmbedColor ??= DefaultFcEmbedColor;
            OfficerEmbedColor ??= DefaultOfficerEmbedColor;

            Version = 14;
            ExtensionData = null;
        }
        if (Version < 15)
        {
            FcEmbedBorder ??= EmbedBorderSettings.CreateDefault(ChannelKind.FcChat);
            OfficerEmbedBorder ??= EmbedBorderSettings.CreateDefault(ChannelKind.OfficerChat);
            FcEmbedBorder.Color = SanitizeRgb(FcEmbedBorder.Color, GetDefaultEmbedColor(ChannelKind.FcChat));
            OfficerEmbedBorder.Color = SanitizeRgb(OfficerEmbedBorder.Color, GetDefaultEmbedColor(ChannelKind.OfficerChat));

            Version = 15;
            ExtensionData = null;
        }
        if (Version < 16)
        {
            FcEmbedBorder ??= EmbedBorderSettings.CreateDefault(ChannelKind.FcChat);
            OfficerEmbedBorder ??= EmbedBorderSettings.CreateDefault(ChannelKind.OfficerChat);
            FcEmbedBorder.Glyph = SanitizeEmbedBorderGlyph(FcEmbedBorder.Glyph);
            OfficerEmbedBorder.Glyph = SanitizeEmbedBorderGlyph(OfficerEmbedBorder.Glyph);

            Version = 16;
            ExtensionData = null;
        }
        if (Version < 17)
        {
            DockVisible = true;
            DockLocked = false;
            DockIconScale = SanitizeDockIconScale(DockIconScale);
            if (!float.IsFinite(DockBackgroundAlpha) || DockBackgroundAlpha <= 0f)
            {
                DockBackgroundAlpha = 0.9f;
            }
            DockBackgroundAlpha = Math.Clamp(DockBackgroundAlpha, 0.1f, 1f);
            DockPositionInitialized = false;
            DockPosition = Vector2.Zero;

            Version = 17;
            ExtensionData = null;
        }
        if (Version < 18)
        {
            var primary = SanitizeColor(PrimaryWindowColor, DefaultPrimaryWindowColor);
            var backgroundAlpha = DockBackgroundAlpha;
            if (!float.IsFinite(backgroundAlpha) || backgroundAlpha <= 0f)
            {
                backgroundAlpha = 0.9f;
            }

            backgroundAlpha = Math.Clamp(backgroundAlpha, 0.1f, 1f);
            var backgroundColor = new Vector4(primary.X, primary.Y, primary.Z, backgroundAlpha);
            DockBackgroundColor = SanitizeDockBackgroundColor(backgroundColor);
            DockBackgroundAlpha = DockBackgroundColor.W;
            DockAutoShow ??= new Dictionary<string, bool>();

            Version = 18;
            ExtensionData = null;
        }
        if (Version < 19)
        {
            var defaultDockOrder = new[]
            {
                "events",
                "create",
                "templates",
                "notepad",
                "requests",
                "chat",
                "officer",
                "syncshell",
                "settings"
            };

            var order = DockOrder ?? new List<string>();
            var defaultSet = new HashSet<string>(defaultDockOrder);
            var seen = new HashSet<string>();
            var normalized = new List<string>();

            foreach (var id in order)
            {
                if (string.IsNullOrWhiteSpace(id))
                {
                    continue;
                }

                var trimmed = id.Trim();
                if (!defaultSet.Contains(trimmed))
                {
                    continue;
                }

                if (seen.Add(trimmed))
                {
                    normalized.Add(trimmed);
                }
            }

            foreach (var id in defaultDockOrder)
            {
                if (seen.Add(id))
                {
                    normalized.Add(id);
                }
            }

            DockOrder = normalized;

            Version = 19;
            ExtensionData = null;
            PluginServices.Instance?.PluginInterface.SavePluginConfig(this);
        }
        if (Version < 20)
        {
            PenumbraModsDirectory ??= string.Empty;
            PenumbraConfigDirectory ??= string.Empty;

            Version = 20;
            ExtensionData = null;
        }

        if (Version < 21)
        {
            PenumbraCollectionOverride ??= string.Empty;

            Version = 21;
            ExtensionData = null;
        }

        if (Version < 22)
        {
            WindowFadePreferences ??= new Dictionary<string, bool>();

            Version = 22;
            ExtensionData = null;
        }
        if (Version < 23)
        {
            DockGradientTopColor = SanitizeDockGradientColor(DockGradientTopColor);
            DockGradientBottomColor = SanitizeDockGradientColor(DockGradientBottomColor);
            DockGradientEnabled = false;

            Version = 23;
            ExtensionData = null;
        }
        if (Version < 24)
        {
            ChatMaxMessages = SanitizeChatMaxMessages(ChatMaxMessages);
            TextureCacheCapacity = SanitizeTextureCacheCapacity(TextureCacheCapacity);
            ImageMaxDecodeWidth = SanitizeImageDecodeDimension(ImageMaxDecodeWidth);
            ImageMaxDecodeHeight = SanitizeImageDecodeDimension(ImageMaxDecodeHeight);
            ImageDownloadConcurrency = SanitizeImageDownloadConcurrency(ImageDownloadConcurrency);
            ImageDecodeConcurrency = SanitizeImageDecodeConcurrency(ImageDecodeConcurrency);
            ImageBytesInFlightBudget = SanitizeImageBytesInFlightBudget(ImageBytesInFlightBudget);
            PreloadRowsAhead = SanitizePreloadRowsAhead(PreloadRowsAhead);

            Version = 24;
            ExtensionData = null;
        }
        if (Version < 25)
        {
            const int OldDefaultPreloadRowsAhead = 2;
            if (PreloadRowsAhead == OldDefaultPreloadRowsAhead)
            {
                PreloadRowsAhead = DefaultPreloadRowsAhead;
            }

            PreloadRowsAhead = SanitizePreloadRowsAhead(PreloadRowsAhead);

            Version = 25;
            ExtensionData = null;
        }
        if (Version < 26)
        {
            if (ExtensionData != null)
            {
                string? candidate = null;
                if (ExtensionData.TryGetValue("apiBaseUrl", out var apiBaseUrlElement) &&
                    apiBaseUrlElement.ValueKind == JsonValueKind.String)
                {
                    candidate = apiBaseUrlElement.GetString();
                }
                else if (ExtensionData.TryGetValue("ApiBaseUrl", out var pascalApiBaseUrlElement) &&
                         pascalApiBaseUrlElement.ValueKind == JsonValueKind.String)
                {
                    candidate = pascalApiBaseUrlElement.GetString();
                }

                if (!string.IsNullOrWhiteSpace(candidate))
                {
                    ApiBaseUrl = candidate;
                }
            }

            ApiBaseUrl = SanitizeApiBaseUrl(ApiBaseUrl);

            Version = 26;
            ExtensionData = null;
        }
    }

    public static uint GetDefaultEmbedColor(string? channelKind)
    {
        return channelKind switch
        {
            ChannelKind.OfficerChat => DefaultOfficerEmbedColor,
            _ => DefaultFcEmbedColor
        };
    }

    public static string SanitizeEmbedBorderGlyph(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return DefaultEmbedBorderGlyph;
        }

        var trimmed = value.Trim();
        return trimmed switch
        {
            "Square" or "square" => "⬛",
            "Circle" or "circle" => "⚫",
            "Triangle" or "triangle" => "🔺",
            _ => trimmed
        };
    }

    public EmbedBorderSettings GetEmbedBorderSettings(string? channelKind)
    {
        return channelKind switch
        {
            ChannelKind.OfficerChat => OfficerEmbedBorder ?? EmbedBorderSettings.CreateDefault(channelKind),
            ChannelKind.FcChat => FcEmbedBorder ?? EmbedBorderSettings.CreateDefault(channelKind),
            _ => EmbedBorderSettings.CreateDefault(channelKind)
        };
    }

    public EmbedBorderSettings GetEmbedBorderSettingsCopy(string? channelKind)
    {
        var settings = GetEmbedBorderSettings(channelKind);
        if (settings != null)
        {
            settings = settings.Clone();
        }
        else
        {
            settings = EmbedBorderSettings.CreateDefault(channelKind);
        }
        settings.Color = SanitizeRgb(settings.Color, GetDefaultEmbedColor(channelKind));
        settings.Glyph = SanitizeEmbedBorderGlyph(settings.Glyph);
        return settings;
    }

    public void SetEmbedBorderSettings(string? channelKind, EmbedBorderSettings settings)
    {
        if (settings == null)
        {
            return;
        }

        settings = settings.Clone();
        settings.Color = SanitizeRgb(settings.Color, GetDefaultEmbedColor(channelKind));
        settings.Glyph = SanitizeEmbedBorderGlyph(settings.Glyph);
        switch (channelKind)
        {
            case ChannelKind.OfficerChat:
                OfficerEmbedBorder = settings;
                break;
            case ChannelKind.FcChat:
                FcEmbedBorder = settings;
                break;
        }
    }

    public bool GetDockAutoShow(string id)
    {
        if (DockAutoShow == null)
        {
            DockAutoShow = new Dictionary<string, bool>();
        }

        if (string.IsNullOrWhiteSpace(id))
        {
            return false;
        }

        return DockAutoShow.TryGetValue(id, out var value) && value;
    }

    public void SetDockAutoShow(string id, bool value)
    {
        if (DockAutoShow == null)
        {
            DockAutoShow = new Dictionary<string, bool>();
        }

        if (string.IsNullOrWhiteSpace(id))
        {
            return;
        }

        if (value)
        {
            DockAutoShow[id] = true;
        }
        else
        {
            DockAutoShow.Remove(id);
        }
    }

    public bool IsWindowFadeEnabled(string? id, bool defaultValue)
    {
        if (!ChatFadeOutEnabled)
        {
            return false;
        }

        if (WindowFadePreferences == null)
        {
            WindowFadePreferences = new Dictionary<string, bool>();
        }

        if (string.IsNullOrWhiteSpace(id))
        {
            return defaultValue;
        }

        return WindowFadePreferences.TryGetValue(id, out var enabled) ? enabled : defaultValue;
    }

    public static string SanitizeApiBaseUrl(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return DefaultApiBaseUrl;
        }

        var trimmed = value.Trim();
        if (!Uri.TryCreate(trimmed, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            return DefaultApiBaseUrl;
        }

        return trimmed;
    }

    public void SetWindowFadePreference(string? id, bool enabled)
    {
        if (WindowFadePreferences == null)
        {
            WindowFadePreferences = new Dictionary<string, bool>();
        }

        if (string.IsNullOrWhiteSpace(id))
        {
            return;
        }

        WindowFadePreferences[id] = enabled;
    }

    internal static uint SanitizeRgb(uint value, uint fallback)
    {
        var sanitized = value & 0xFFFFFFu;
        if (sanitized == value)
        {
            return sanitized;
        }

        return fallback & 0xFFFFFFu;
    }

    internal static Vector4 SanitizeColor(Vector4 color, Vector4 fallback)
    {
        if (!float.IsFinite(color.X) || !float.IsFinite(color.Y) || !float.IsFinite(color.Z) || !float.IsFinite(color.W))
        {
            return fallback;
        }

        return new Vector4(
            Math.Clamp(color.X, 0f, 1f),
            Math.Clamp(color.Y, 0f, 1f),
            Math.Clamp(color.Z, 0f, 1f),
            Math.Clamp(color.W, 0f, 1f));
    }

    internal static float SanitizeEmojiTileSize(float value)
    {
        if (!float.IsFinite(value) || value <= 0f)
        {
            return 28f;
        }

        return Math.Clamp(value, MinEmojiTileSize, MaxEmojiTileSize);
    }

    internal static float SanitizeEmojiGridHeight(float value)
    {
        if (!float.IsFinite(value))
        {
            return 220f;
        }

        if (value <= 0f)
        {
            return 0f;
        }

        return Math.Clamp(value, MinEmojiGridHeight, MaxEmojiGridHeight);
    }

    internal static float SanitizeDockIconScale(float value)
    {
        if (!float.IsFinite(value) || value <= 0f)
        {
            return 1f;
        }

        return Math.Clamp(value, MinDockIconScale, MaxDockIconScale);
    }

    internal static Vector4 SanitizeDockBackgroundColor(Vector4 color)
    {
        var sanitized = SanitizeColor(color, DefaultDockBackgroundColor);
        sanitized.W = Math.Clamp(sanitized.W, 0.1f, 1f);
        return sanitized;
    }

    internal static Vector4 SanitizeDockGradientColor(Vector4 color)
        => SanitizeDockBackgroundColor(color);

    public static int SanitizeChatMaxMessages(int value)
    {
        if (value <= 0)
        {
            return DefaultChatMaxMessages;
        }

        return Math.Clamp(value, MinChatMaxMessages, MaxChatMaxMessages);
    }

    public static int SanitizeTextureCacheCapacity(int value)
    {
        if (value <= 0)
        {
            return DefaultTextureCacheCapacity;
        }

        return Math.Clamp(value, MinTextureCacheCapacity, MaxTextureCacheCapacity);
    }

    public static int SanitizeImageDecodeDimension(int value)
    {
        if (value <= 0)
        {
            return DefaultImageMaxDecodeWidth;
        }

        return Math.Clamp(value, MinImageDecodeDimension, MaxImageDecodeDimension);
    }

    public static int SanitizeImageDownloadConcurrency(int value)
    {
        if (value <= 0)
        {
            return DefaultImageDownloadConcurrency;
        }

        return Math.Clamp(value, MinImageConcurrency, MaxImageDownloadConcurrency);
    }

    public static int SanitizeImageDecodeConcurrency(int value)
    {
        if (value <= 0)
        {
            return DefaultImageDecodeConcurrency;
        }

        return Math.Clamp(value, MinImageConcurrency, MaxImageDecodeConcurrency);
    }

    public static long SanitizeImageBytesInFlightBudget(long value)
    {
        if (value <= 0)
        {
            return DefaultImageBytesInFlightBudget;
        }

        return Math.Clamp(value, MinImageBytesInFlightBudget, MaxImageBytesInFlightBudget);
    }

    public static int SanitizePreloadRowsAhead(int value)
    {
        if (value < 0)
        {
            return DefaultPreloadRowsAhead;
        }

        return Math.Clamp(value, MinPreloadRowsAhead, MaxPreloadRowsAhead);
    }
}
