using Dalamud.Configuration;
using System;
using System.Collections.Generic;
using System.Numerics;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DemiCatPlugin;

public class Config : IPluginConfiguration
{
    public const float MinChatFontScale = 0.75f;
    public const float MaxChatFontScale = 1.5f;
    public const float MinChatImageScale = 0.5f;
    public const float MaxChatImageScale = 3f;
    public static readonly Vector4 DefaultPrimaryWindowColor = new(0.11f, 0.11f, 0.12f, 1f);
    public static readonly Vector4 DefaultSecondaryAccentColor = new(0.2f, 0.6f, 1f, 1f);
    public static readonly Vector4 DefaultDockBackgroundColor = new(0.05f, 0.05f, 0.06f, 1f);

    // Required by Dalamud
    public const float MinEmojiTileSize = 16f;
    public const float MaxEmojiTileSize = 64f;
    public const float MinEmojiGridHeight = 120f;
    public const float MaxEmojiGridHeight = 800f;
    public const uint DefaultFcEmbedColor = 0x5865F2;
    public const uint DefaultOfficerEmbedColor = 0xED4245;
    public const string DefaultEmbedBorderGlyph = "⬛";
    public const int CurrentVersion = 19;

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
    public string ApiBaseUrl { get; set; } = "http://127.0.0.1:5050";
    public string WebSocketPath { get; set; } = "/ws/embeds";
    public int PollIntervalSeconds { get; set; } = 5;
    [JsonPropertyName("guildId")]
    public string GuildId { get; set; } = string.Empty;
    public string ChatChannelId { get; set; } = string.Empty;
    public Dictionary<string, long> ChatCursors { get; set; } = new();
    public Dictionary<string, long> RestChatCursors { get; set; } = new();
    public Dictionary<string, string> ChannelSelections { get; set; } = new();
    public string EventChannelId { get; set; } = string.Empty;
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

    public float ChatFontScale { get; set; } = 1f;
    public bool ChatImageAutoStretch { get; set; } = true;
    public float ChatImageManualScale { get; set; } = 1f;

    [JsonPropertyName("emojiTileSize")]
    public float EmojiTileSize { get; set; } = 28f;

    [JsonPropertyName("emojiGridHeight")]
    public float EmojiGridHeight { get; set; } = 220f;

    [JsonPropertyName("primaryWindowColor")]
    public Vector4 PrimaryWindowColor { get; set; } = DefaultPrimaryWindowColor;

    [JsonPropertyName("secondaryAccentColor")]
    public Vector4 SecondaryAccentColor { get; set; } = DefaultSecondaryAccentColor;

    [JsonPropertyName("dockBackgroundColor")]
    public Vector4 DockBackgroundColor { get; set; } = DefaultDockBackgroundColor;

    [JsonPropertyName("dockOpacity")]
    public float DockOpacity { get; set; } = 0.85f;

    [JsonPropertyName("dockGradientEnabled")]
    public bool DockGradientEnabled { get; set; } = false;

    [JsonPropertyName("dockGradientStartColor")]
    public Vector4 DockGradientStartColor { get; set; } = DefaultDockBackgroundColor;

    [JsonPropertyName("dockGradientEndColor")]
    public Vector4 DockGradientEndColor { get; set; } = DefaultDockBackgroundColor;

    [JsonPropertyName("dockAutoFadeEnabled")]
    public bool DockAutoFadeEnabled { get; set; } = false;

    [JsonPropertyName("dockAutoOpenWindows")]
    public List<string> DockAutoOpenWindows { get; set; } = new();

    [JsonPropertyName("dockOrder")]
    public List<string> DockOrder { get; set; } = new();

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

    [JsonPropertyName("penumbraChoices")]
    public Dictionary<string, bool> PenumbraChoices { get; set; } = new();

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
            DockBackgroundColor = SanitizeColor(DockBackgroundColor, DefaultDockBackgroundColor);
            if (!float.IsFinite(DockOpacity))
            {
                DockOpacity = 0.85f;
            }
            DockOpacity = Math.Clamp(DockOpacity, 0f, 1f);
            DockOrder ??= new List<string>();

            Version = 17;
            ExtensionData = null;
        }
        if (Version < 18)
        {
            EnableFcChat = true;
            EnableFcChatUserSet = true;
            Version = 18;
            ExtensionData = null;
        }
        if (Version < 19)
        {
            DockGradientStartColor = SanitizeColor(DockGradientStartColor, DefaultDockBackgroundColor);
            DockGradientEndColor = SanitizeColor(DockGradientEndColor, DefaultDockBackgroundColor);
            DockAutoOpenWindows = (DockAutoOpenWindows ?? new List<string>())
                .Where(static s => !string.IsNullOrWhiteSpace(s))
                .Distinct(StringComparer.Ordinal)
                .ToList();

            Version = 19;
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
}
