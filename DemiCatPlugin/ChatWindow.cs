using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Numerics;
using System.Threading;
using System.Threading.Tasks;
using System.Linq;
using System.Globalization;
using Dalamud.Interface.Textures;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Textures.TextureWraps;
using ImGuiNET;
using System.IO;
using DiscordHelper;
using System.Diagnostics;
using Dalamud.Interface.ImGuiFileDialog;
using DemiCatPlugin.Emoji;
using DemiCatPlugin.Avatars;
using DemiCatPlugin.Images;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace DemiCatPlugin;

public class ChatWindow : IDisposable
{
    protected readonly Config _config;
    protected readonly HttpClient _httpClient;
    protected readonly List<DiscordMessageDto> _messages = new();
    protected readonly List<ChannelDto> _channels = new();
    private string[] _channelDisplayNames = Array.Empty<string>();
    protected int _selectedIndex;
    protected bool _channelsLoaded;
    protected bool _channelsLoading;
    protected bool _channelFetchFailed;
    protected string _channelErrorMessage = string.Empty;
    protected string _input = string.Empty;
    protected bool _useCharacterName;
    protected string _statusMessage = string.Empty;
    private string _lastError = string.Empty;
    protected readonly DiscordPresenceService? _presence;
    protected readonly List<string> _attachments = new();
    private readonly FileDialogManager _fileDialog = new();
    private string _attachmentError = string.Empty;
    protected readonly TokenManager _tokenManager;
    protected readonly ChannelService _channelService;
    protected string? _replyToId;
    protected string? _editingMessageId;
    protected string _editingChannelId = string.Empty;
    protected string _editContent = string.Empty;
    protected uint? _editEmbedColor;
    protected EmbedBorderRenderDto? _editEmbedBorder;
    private static readonly string[] DefaultReactions = new[] { "👍", "👎", "❤️" };
    private static readonly ImGuiMouseCursor ResizeNsCursor = ResolveResizeNsCursor();
    private readonly EmojiManager _emojiManager;
    private readonly EmojiPicker _emojiPicker;
    private readonly Dictionary<string, EmojiData> _emojiCatalog = new();
    protected readonly ChatBridge _bridge;
    private readonly ChannelSelectionService _channelSelection;
    private readonly AvatarCache? _avatarCache;
    private readonly string _channelKind;
    private const string ChannelUnavailableMessage = "Selected channel is no longer available";
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true
    };
    private const float DefaultComposeSplitRatio = 0.35f;
    private const float MinComposeSplitRatio = 0.2f;
    private const float MaxComposeSplitRatio = 0.8f;
    private const int MinInputLines = 1;
    private const int MaxInputLines = 8;
    private readonly Dictionary<string, TextureCacheEntry> _textureCache = new();
    private readonly LinkedList<string> _textureLru = new();
    private readonly Dictionary<string, TypingUser> _typingUsers = new();
    private static readonly Vector4 MessageHoverBgColor = new(0.24f, 0.33f, 0.53f, 0.22f);
    private static readonly Vector4 MessageIdleBgColor = new(1f, 1f, 1f, 0.03f);
    private const float MessageHoverTextBlend = 0.2f;
    private readonly Dictionary<string, bool> _messageHoverStates = new();
    private bool _networkingActive;
    private string? _lastSubscribedChannelId;
    private string? _lastSubscribedGuildId;
    private bool _pendingRefreshAfterSubscribe;
    private bool _pendingInitialScroll = true;
    private readonly SemaphoreSlim _refreshGate = new(1, 1);
    private volatile bool _refreshQueued;
    private bool _wasAtBottomLastFrame = true;
    private float _chatTopPadding;
    private int _selectionStart;
    private int _selectionEnd;
    private bool _focusComposerNextFrame;
    private static ChatWindow? _activeInputCallbackOwner;
    private static unsafe readonly ImGuiInputTextCallback _inputEditedCallback = OnInputEdited;
    private BridgeMessageFormatter.BridgeFormattedMessage _previewMessage = BridgeMessageFormatter.BridgeFormattedMessage.Empty;
    private string _previewKey = string.Empty;
    private readonly Dictionary<string, ISharedImmediateTexture?> _attachmentPreviewTextures = new(StringComparer.OrdinalIgnoreCase);
    private float _previewContentHeight;
    private bool _previewForceLoadEmbeds;
    private const string OldestRestCursorSuffix = ":oldest";
    private const int MentionResultLimit = 20;
    private const float MentionDrawerAnimationSpeed = 12f;
    private const float MentionDrawerBaseOffset = 4f;
    private const float MentionDrawerTravelDistance = 10f;
    private MentionDrawerState? _mentionDrawerState;
    private int _imageMaxDecodeWidth = Config.DefaultImageMaxDecodeWidth;
    private int _imageMaxDecodeHeight = Config.DefaultImageMaxDecodeHeight;
    private int _preloadRowsAhead = Config.DefaultPreloadRowsAhead;
    private bool _lazyLoadEmbedsEnabled;
    private bool _allowTextureLoads = true;
    private readonly Dictionary<string, (Vector2 Min, Vector2 Max)> _messageRectCache = new();
    private IImmediateTextureFactory? _textureFactory;
    private ImageLoader? _imageLoader;
    private CancellationTokenSource _imageCts;

    protected string CurrentChannelId => _channelSelection.GetChannel(_channelKind, _config.GuildId);
    protected string ChannelKindKey => _channelKind;

    private class TextureCacheEntry
    {
        public ISharedImmediateTexture? Texture;
        public LinkedListNode<string> Node;

        public TextureCacheEntry(ISharedImmediateTexture? texture, LinkedListNode<string> node)
        {
            Texture = texture;
            Node = node;
        }
    }

    private static bool TryGetTextureWrap(ISharedImmediateTexture? texture, out IDalamudTextureWrap? wrap)
    {
        wrap = texture?.GetWrapOrEmpty();
        var handle = wrap.ToImGuiHandle();
        if (wrap == null || handle == 0 || wrap.Width <= 0 || wrap.Height <= 0)
        {
            wrap = null;
            return false;
        }

        return true;
    }

    private void ReleaseTextureReferences(ISharedImmediateTexture? texture)
    {
        if (texture == null)
        {
            return;
        }

        foreach (var message in _messages)
        {
            if (ReferenceEquals(message.AvatarTexture, texture))
            {
                message.AvatarTexture = null;
            }

            if (message.Attachments != null)
            {
                foreach (var attachment in message.Attachments)
                {
                    if (ReferenceEquals(attachment.Texture, texture))
                    {
                        attachment.Texture = null;
                    }
                }
            }

            if (message.Reactions != null)
            {
                foreach (var reaction in message.Reactions)
                {
                    if (ReferenceEquals(reaction.Texture, texture))
                    {
                        reaction.Texture = null;
                    }
                }
            }
        }

        foreach (var emoji in _emojiCatalog.Values)
        {
            if (ReferenceEquals(emoji.Texture, texture))
            {
                emoji.Texture = null;
            }
        }

        foreach (var kvp in _attachmentPreviewTextures.ToList())
        {
            if (ReferenceEquals(kvp.Value, texture))
            {
                _attachmentPreviewTextures[kvp.Key] = null;
            }
        }

        if (_presence != null)
        {
            foreach (var presence in _presence.Presences)
            {
                if (ReferenceEquals(presence.AvatarTexture, texture))
                {
                    presence.AvatarTexture = null;
                }

                if (ReferenceEquals(presence.BannerTexture, texture))
                {
                    presence.BannerTexture = null;
                }
            }
        }

        EmbedRenderer.ReleaseTexture(texture);
        EmbedPreviewRenderer.ReleaseTexture(texture);
    }

    private float GetManualImageScale()
    {
        var manualScale = _config.ChatImageManualScale;
        if (!float.IsFinite(manualScale) || manualScale <= 0f)
        {
            manualScale = 1f;
        }

        return Math.Clamp(manualScale, Config.MinChatImageScale, Config.MaxChatImageScale);
    }

    private bool SupportsEmbedColorSelection()
        => _channelKind == global::DemiCatPlugin.ChannelKind.FcChat || _channelKind == global::DemiCatPlugin.ChannelKind.OfficerChat;

    private uint? GetEmbedColorOverride()
        => _channelKind switch
        {
            global::DemiCatPlugin.ChannelKind.FcChat => _config.FcEmbedColor,
            global::DemiCatPlugin.ChannelKind.OfficerChat => _config.OfficerEmbedColor,
            _ => null
        };

    private uint GetEffectiveEmbedColor()
    {
        var overrideColor = GetEmbedColorOverride();
        return overrideColor ?? Config.GetDefaultEmbedColor(_channelKind);
    }

    private void SetEmbedColorOverride(uint? color)
    {
        var changed = false;
        if (_channelKind == global::DemiCatPlugin.ChannelKind.FcChat)
        {
            if (_config.FcEmbedColor != color)
            {
                _config.FcEmbedColor = color;
                changed = true;
            }
        }
        else if (_channelKind == global::DemiCatPlugin.ChannelKind.OfficerChat)
        {
            if (_config.OfficerEmbedColor != color)
            {
                _config.OfficerEmbedColor = color;
                changed = true;
            }
        }

        if (changed)
        {
            SaveConfig();
            InvalidatePreview();
        }
    }

    private void SetEmbedBorderSettings(Config.EmbedBorderSettings settings)
    {
        if (settings == null)
        {
            return;
        }

        var current = _config.GetEmbedBorderSettingsCopy(_channelKind);
        var currentGlyph = EmbedBorderBuilder.GetGlyphSymbol(current.Glyph);
        var newGlyph = EmbedBorderBuilder.GetGlyphSymbol(settings.Glyph);
        if (current.Enabled == settings.Enabled && string.Equals(currentGlyph, newGlyph, StringComparison.Ordinal) && current.Color == settings.Color)
        {
            return;
        }

        var sanitized = settings.Clone();
        sanitized.Glyph = newGlyph;
        _config.SetEmbedBorderSettings(_channelKind, sanitized);
        SaveConfig();
        InvalidatePreview();
    }

    private string SerializeBorderSettingsForPayload()
    {
        var border = _config.GetEmbedBorderSettingsCopy(_channelKind);
        var payload = new
        {
            enabled = border.Enabled,
            glyph = EmbedBorderBuilder.GetGlyphSymbol(border.Glyph),
            color = border.Color
        };
        return JsonSerializer.Serialize(payload);
    }

    private void DrawEmbedStyleControls()
    {
        if (!SupportsEmbedColorSelection())
        {
            return;
        }

        var context = new EmbedStyleControls.Context
        {
            ChannelKindKey = _channelKind,
            EffectiveEmbedColor = GetEffectiveEmbedColor(),
            EmbedColorOverride = GetEmbedColorOverride(),
            Border = _config.GetEmbedBorderSettingsCopy(_channelKind),
            EmojiManager = _emojiManager,
            EmojiTileSize = Config.SanitizeEmojiTileSize(_config.EmojiTileSize),
            EmojiGridHeight = Config.SanitizeEmojiGridHeight(_config.EmojiGridHeight)
        };

        var result = EmbedStyleControls.Draw(context);

        if (result.EmbedColorChanged)
        {
            SetEmbedColorOverride(result.EmbedColorOverride);
        }

        if (result.BorderChanged)
        {
            SetEmbedBorderSettings(result.Border);
        }

        ImGui.SameLine();
    }

    protected float GetFontScale()
    {
        var fontScale = _config.ChatFontScale;
        if (!float.IsFinite(fontScale) || fontScale <= 0f)
        {
            fontScale = 1f;
        }

        return Math.Clamp(fontScale, Config.MinChatFontScale, Config.MaxChatFontScale);
    }

    protected Vector2 GetAttachmentBounds(Vector2 baseBounds, bool allowAutoStretch = true)
    {
        var width = MathF.Max(1f, baseBounds.X);
        var height = MathF.Max(1f, baseBounds.Y);

        if (allowAutoStretch && _config.ChatImageAutoStretch)
        {
            var availableWidth = MathF.Max(1f, ImGui.GetContentRegionAvail().X);
            width = availableWidth;
        }

        return new Vector2(width, height);
    }

    private Vector2 GetConfiguredAttachmentCap()
    {
        var scale = ImGuiHelpers.GlobalScale;
        if (!float.IsFinite(scale) || scale <= 0f)
        {
            scale = 1f;
        }

        var maxWidth = Config.SanitizeImageDecodeDimension(_config.ImageMaxDecodeWidth);
        var maxHeight = Config.SanitizeImageDecodeDimension(_config.ImageMaxDecodeHeight);

        return new Vector2(MathF.Max(1f, maxWidth * scale), MathF.Max(1f, maxHeight * scale));
    }

    private readonly struct WindowFontScaleScope : IDisposable
    {
        private readonly bool _active;

        public WindowFontScaleScope(float scale)
        {
            if (!float.IsFinite(scale) || Math.Abs(scale - 1f) < 0.0001f)
            {
                _active = false;
                return;
            }

            ImGui.SetWindowFontScale(scale);
            _active = true;
        }

        public void Dispose()
        {
            if (_active)
            {
                ImGui.SetWindowFontScale(1f);
            }
        }
    }

    private WindowFontScaleScope PushWindowFontScale(float scale) => new(scale);

    private class TypingUser
    {
        public string Name;
        public DateTime Expires;

        public TypingUser(string name, DateTime expires)
        {
            Name = name;
            Expires = expires;
        }
    }

    private class EmojiData
    {
        public string Id;
        public bool Animated;
        public ISharedImmediateTexture? Texture;

        public EmojiData(string id, bool animated)
        {
            Id = id;
            Animated = animated;
        }

        public string ImageUrl => $"https://cdn.discordapp.com/emojis/{Id}.{(Animated ? "gif" : "png")}";
    }

    private sealed class MentionDrawerState
    {
        public bool Active;
        public int TokenStart;
        public int TokenEnd;
        public string Query = string.Empty;
        public List<MentionCandidate> Candidates { get; } = new();
        public int HighlightedIndex = -1;
        public float AnimationProgress;
        public Vector2 AnchorMin;
        public Vector2 AnchorMax;
        public int SuppressedStart = -1;
        public int SuppressedEnd = -1;
        public string? SuppressedQuery;

        public void ClearCandidates()
        {
            Candidates.Clear();
            HighlightedIndex = -1;
        }
    }

    private sealed class MentionCandidate
    {
        public MentionCandidate(MentionCandidateType type, string id, string name)
        {
            Type = type;
            Id = id;
            Name = name;
        }

        public MentionCandidateType Type { get; }
        public string Id { get; }
        public string Name { get; }
        public string? Subtitle { get; set; }
        public Vector4? AccentColor { get; set; }
        public PresenceDto? Presence { get; set; }
        public RoleDto? Role { get; set; }
        public bool AvatarRequested { get; set; }
    }

    private enum MentionCandidateType
    {
        User,
        Role
    }

    public bool ChannelsLoaded
    {
        get => _channelsLoaded;
        set => _channelsLoaded = value;
    }

    protected bool HasActiveSubscription =>
        _networkingActive &&
        !string.IsNullOrEmpty(_lastSubscribedChannelId) &&
        !string.IsNullOrEmpty(_lastSubscribedGuildId) &&
        string.Equals(_lastSubscribedChannelId, CurrentChannelId, StringComparison.Ordinal) &&
        string.Equals(
            _lastSubscribedGuildId,
            ChannelKeyHelper.NormalizeGuildId(_config.GuildId),
            StringComparison.Ordinal);

    public DiscordPresenceService? Presence => _presence;
    public Action<string?, Action<ISharedImmediateTexture?>> TextureLoader => LoadTexture;

    protected virtual string MessagesPath => "/api/messages";
    protected virtual bool MentionsEnabled => false;

    public ChatWindow(
        Config config,
        HttpClient httpClient,
        DiscordPresenceService? presence,
        TokenManager tokenManager,
        ChannelService channelService,
        ChannelSelectionService channelSelection,
        string channelKind,
        AvatarCache? avatarCache,
        EmojiManager emojiManager)
    {
        _config = config;
        _httpClient = httpClient;
        _presence = presence;
        _tokenManager = tokenManager;
        _channelService = channelService;
        _channelSelection = channelSelection;
        _avatarCache = avatarCache;
        _channelKind = channelKind;
        _emojiManager = emojiManager;
        _emojiPicker = new EmojiPicker(_emojiManager, _config, SaveConfig);
        _ = _emojiManager.EnsureUnicodeAsync();
        _ = _emojiManager.EnsureCustomAsync();
        _useCharacterName = config.UseCharacterName;
        _bridge = new ChatBridge(config, httpClient, tokenManager, BuildWebSocketUri, channelSelection);
        _bridge.MessageReceived += HandleBridgeMessage;
        _bridge.TypingReceived += HandleBridgeTyping;
        _bridge.Linked += HandleBridgeLinked;
        _bridge.Unlinked += HandleBridgeUnlinked;
        _bridge.StatusChanged += s => _ = PluginServices.Instance!.Framework.RunOnTick(() => _statusMessage = s);
        _bridge.ResyncRequested += (ch, cur) =>
            PluginServices.Instance!.Framework.RunOnTick(async () =>
            {
                if (ch == CurrentChannelId) await RequestRefreshMessagesAsync();
            });

        _channelSelection.ChannelChanged += HandleChannelSelectionChanged;
        _imageCts = new CancellationTokenSource();
        ReconfigureImageLoader();
    }

#if TEST
    internal ChatWindow(Config config, HttpClient httpClient, DiscordPresenceService? presence, TokenManager tokenManager, ChannelService channelService)
        : this(
            config,
            httpClient,
            presence,
            tokenManager,
            channelService,
            new ChannelSelectionService(config),
            global::DemiCatPlugin.ChannelKind.Chat,
            null,
            new EmojiManager(httpClient, tokenManager, config))
    {
    }
#endif

    protected virtual void OnSubscriptionStateChanged(bool isSubscribed)
    {
    }

    public bool IsNetworkingActive => _networkingActive;

    protected void MarkNetworkingStarted()
    {
        _networkingActive = true;
    }

    protected void MarkNetworkingStopped()
    {
        _networkingActive = false;
        _lastSubscribedChannelId = null;
        _lastSubscribedGuildId = null;
        _pendingRefreshAfterSubscribe = false;
    }

    protected bool TrySubscribeCurrentChannel(bool force = false, bool refreshMessages = true)
    {
        if (refreshMessages)
        {
            _pendingRefreshAfterSubscribe = true;
        }

        if (!_networkingActive)
        {
            return false;
        }

        var channelId = CurrentChannelId;
        if (string.IsNullOrEmpty(channelId))
        {
            if (!string.IsNullOrEmpty(_lastSubscribedChannelId))
            {
                _bridge.Unsubscribe(_lastSubscribedChannelId);
            }

            _lastSubscribedChannelId = null;
            _lastSubscribedGuildId = null;
            OnSubscriptionStateChanged(false);
            return false;
        }

        var guildId = _config.GuildId;
        if (string.IsNullOrWhiteSpace(guildId))
        {
            if (!string.IsNullOrEmpty(_lastSubscribedChannelId))
            {
                _bridge.Unsubscribe(_lastSubscribedChannelId);
            }

            _lastSubscribedChannelId = null;
            _lastSubscribedGuildId = null;
            OnSubscriptionStateChanged(false);
            return false;
        }

        var normalizedGuild = ChannelKeyHelper.NormalizeGuildId(guildId);
        var alreadySubscribed = !force &&
            string.Equals(_lastSubscribedChannelId, channelId, StringComparison.Ordinal) &&
            string.Equals(_lastSubscribedGuildId, normalizedGuild, StringComparison.Ordinal);

        if (alreadySubscribed)
        {
            if (_pendingRefreshAfterSubscribe)
            {
                _ = PluginServices.Instance!.Framework.RunOnTick(async () => await RequestRefreshMessagesAsync());
                _pendingRefreshAfterSubscribe = false;
            }

            return true;
        }

        if (!string.IsNullOrEmpty(_lastSubscribedChannelId))
        {
            _bridge.Unsubscribe(_lastSubscribedChannelId);
        }

        _bridge.Subscribe(channelId, guildId, _channelKind);

        if (_pendingRefreshAfterSubscribe)
        {
            _ = PluginServices.Instance!.Framework.RunOnTick(async () => await RequestRefreshMessagesAsync());
            _pendingRefreshAfterSubscribe = false;
        }

        _lastSubscribedChannelId = channelId;
        _lastSubscribedGuildId = normalizedGuild;
        OnSubscriptionStateChanged(true);
        return true;
    }

    public virtual void StartNetworking()
    {
        MarkNetworkingStarted();
        _presence?.SetPresenceReady(true);
        _bridge.Start();
        TrySubscribeCurrentChannel(force: true);
        _presence?.Reset();
    }

    public void StopNetworking()
    {
        _bridge.Stop();
        MarkNetworkingStopped();
        OnSubscriptionStateChanged(false);
        _presence?.Stop();
        _presence?.SetPresenceReady(false);
        _ = PluginServices.Instance!.Framework.RunOnTick(() => _statusMessage = string.Empty);
    }

    private void HandleChannelSelectionChanged(string kind, string guildId, string oldId, string newId)
    {
        if (kind != _channelKind) return;
        if (!string.Equals(ChannelKeyHelper.NormalizeGuildId(guildId), ChannelKeyHelper.NormalizeGuildId(_config.GuildId), StringComparison.Ordinal))
            return;
        PluginServices.Instance!.Framework.RunOnTick(() =>
        {
            _imageCts.Cancel();
            _imageCts.Dispose();
            _imageCts = new CancellationTokenSource();

            _pendingInitialScroll = true;
            _wasAtBottomLastFrame = true;
            _chatTopPadding = 0f;

            foreach (var message in _messages)
            {
                DisposeMessageTextures(message);
            }

            _messages.Clear();
            _messageHoverStates.Clear();
            _messageRectCache.Clear();
            ClearTextureCache();

            if (!string.IsNullOrEmpty(oldId))
            {
                _bridge.Unsubscribe(oldId);
            }

            if (string.IsNullOrEmpty(newId))
            {
                _lastSubscribedChannelId = null;
                _lastSubscribedGuildId = null;
                OnSubscriptionStateChanged(false);
                return;
            }

            _lastSubscribedChannelId = null;
            _lastSubscribedGuildId = null;
            OnSubscriptionStateChanged(false);
            TrySubscribeCurrentChannel(force: true);
        });
    }

    public virtual void Draw()
    {
        if (!ImGuiHelpers.IsImGuiReady || ImGui.GetCurrentContext() == IntPtr.Zero)
        {
            return;
        }

        _fileDialog.Draw();
        if (!_bridge.IsReady())
        {
            if (!string.IsNullOrEmpty(_statusMessage))
            {
                ImGui.TextUnformatted(_statusMessage);
            }
            else
            {
                ImGui.TextUnformatted("Link DemiCat…");
            }
            return;
        }

        if (!_channelsLoaded && !_channelsLoading)
        {
            _ = FetchChannels();
        }

        if (_channels.Count > 0)
        {
            var channelNames = _channelDisplayNames;
            var previousIndex = _selectedIndex;
            var activeChannelId = CurrentChannelId;
            string? selectedChannelId = null;

            {
                using var emojiFont = _emojiManager.PushEmojiFont();
                if (ImGui.Combo("Channel", ref _selectedIndex, channelNames, channelNames.Length))
                {
                    selectedChannelId = _channels[_selectedIndex].Id;
                    if (string.Equals(selectedChannelId, activeChannelId, StringComparison.Ordinal))
                    {
                        if (previousIndex != _selectedIndex)
                        {
                            _selectedIndex = previousIndex;
                        }

                        selectedChannelId = null;
                    }
                }
            }

            if (selectedChannelId != null)
            {
                _channelSelection.SetChannel(_channelKind, _config.GuildId, selectedChannelId);
            }
        }
        else
        {
            ImGui.TextUnformatted(_channelFetchFailed ? _channelErrorMessage : "No channels available");
        }
        if (ImGui.Checkbox("Use Character Name", ref _useCharacterName))
        {
            _config.UseCharacterName = _useCharacterName;
            SaveConfig();
            InvalidatePreview();
        }

        var totalAvailableHeight = ImGui.GetContentRegionAvail().Y;
        var ratioDenominator = totalAvailableHeight > 0f ? totalAvailableHeight : 1f;
        var composeRatio = _config.ChatInputSplitRatio;
        if (!float.IsFinite(composeRatio) || composeRatio <= 0f)
        {
            composeRatio = DefaultComposeSplitRatio;
        }
        composeRatio = Math.Clamp(composeRatio, MinComposeSplitRatio, MaxComposeSplitRatio);
        var composeHeight = ratioDenominator * composeRatio;
        var scrollRegionHeight = totalAvailableHeight - composeHeight;
        if (scrollRegionHeight < 0f)
        {
            scrollRegionHeight = 0f;
            composeHeight = totalAvailableHeight;
        }
        var composeAreaHeight = Math.Max(0f, totalAvailableHeight - scrollRegionHeight);
        var actualRatio = totalAvailableHeight > 0f ? composeAreaHeight / ratioDenominator : composeRatio;
        actualRatio = Math.Clamp(actualRatio, MinComposeSplitRatio, MaxComposeSplitRatio);
        if (Math.Abs(actualRatio - _config.ChatInputSplitRatio) > 0.0001f)
        {
            _config.ChatInputSplitRatio = actualRatio;
        }
        composeRatio = actualRatio;
        const float ScrollTolerance = 1f;
        ImGui.BeginChild("##chatScroll", new Vector2(-1, scrollRegionHeight), ImGuiChildFlags.Border, ImGuiWindowFlags.None);

        var fontScale = GetFontScale();
        var chatFontScope = PushWindowFontScale(fontScale);

        var io = ImGui.GetIO();
        var hoverFlags = ImGuiHoveredFlags.AllowWhenBlockedByPopup | ImGuiHoveredFlags.AllowWhenBlockedByActiveItem;
        var chatHovered = ImGui.IsWindowHovered(hoverFlags);
        var userWheelScrolling = chatHovered && Math.Abs(io.MouseWheel) > float.Epsilon;
        var userDragging = chatHovered && ImGui.IsMouseDragging(ImGuiMouseButton.Left);
        var userInteractingWithScroll = userWheelScrolling || userDragging;

        if (userInteractingWithScroll && _pendingInitialScroll)
        {
            _pendingInitialScroll = false;
        }

        var shouldAutoScroll = (_pendingInitialScroll || _wasAtBottomLastFrame) && !userInteractingWithScroll;
        if (shouldAutoScroll && _messages.Count == 0)
        {
            shouldAutoScroll = false;
        }

        var topPadding = _chatTopPadding;
        if (_messages.Count == 0)
        {
            topPadding = 0f;
            _chatTopPadding = 0f;
        }

        if (topPadding > 0f)
        {
            ImGui.Dummy(new Vector2(1f, topPadding));
        }

        var keepMargin = Math.Max(0, _preloadRowsAhead);
        unsafe
        {
            var clipperPtr = ImGuiNative.ImGuiListClipper_ImGuiListClipper();
            var clipper = new ImGuiListClipperPtr(clipperPtr);
            clipper.Begin(_messages.Count);
            while (clipper.Step())
            {
                var visibleStart = clipper.DisplayStart;
                var visibleEnd = clipper.DisplayEnd;
                if (visibleStart >= visibleEnd)
                {
                    PurgeOffscreenTextures(visibleStart, visibleEnd, keepMargin);
                    continue;
                }

                var preloadStart = Math.Max(0, visibleStart - keepMargin);
                var preloadEnd = Math.Min(_messages.Count, visibleEnd + keepMargin);
                for (var i = preloadStart; i < preloadEnd; i++)
                {
                    if (i < visibleStart || i >= visibleEnd)
                    {
                        PreloadMessageAttachments(i);
                        continue;
                    }

                    DrawMessageRow(i, hoverFlags);
                }

                PurgeOffscreenTextures(visibleStart, visibleEnd, keepMargin);
            }

            clipper.End();
            clipper.Destroy();
        }

        if (_messageHoverStates.Count > _messages.Count)
        {
            var validIds = new HashSet<string>(_messages.Select(m => m.Id));
            foreach (var key in _messageHoverStates.Keys.ToList())
            {
                if (!validIds.Contains(key))
                {
                    _messageHoverStates.Remove(key);
                }
            }
        }

        if (shouldAutoScroll)
        {
            var maxScrollY = ImGui.GetScrollMaxY();
            if (maxScrollY > 0f)
            {
                ImGui.SetScrollY(maxScrollY);
            }
            _pendingInitialScroll = false;
        }

        var cursorStartY = ImGui.GetCursorStartPos().Y;
        var contentRegionMaxY = ImGui.GetWindowContentRegionMax().Y;
        var innerHeight = MathF.Max(0f, contentRegionMaxY - cursorStartY);
        var usedHeight = ImGui.GetCursorPosY() - cursorStartY;
        var messageHeight = MathF.Max(0f, usedHeight - topPadding);
        if (_messages.Count > 0 && innerHeight > 0f)
        {
            var paddingNeeded = innerHeight - messageHeight;
            _chatTopPadding = paddingNeeded > 0f ? paddingNeeded : 0f;
        }
        else
        {
            _chatTopPadding = 0f;
        }

        var newScrollMaxY = ImGui.GetScrollMaxY();
        var newScrollY = ImGui.GetScrollY();
        _wasAtBottomLastFrame = newScrollMaxY <= 0f || newScrollY >= newScrollMaxY - ScrollTolerance;

        chatFontScope.Dispose();
        ImGui.EndChild();

        var style = ImGui.GetStyle();
        var splitterHeight = Math.Max(4f, style.FramePadding.Y);
        var splitterWidth = ImGui.GetContentRegionAvail().X;
        if (splitterWidth > 0f)
        {
            ImGui.InvisibleButton("##chatInputSplitter", new Vector2(splitterWidth, splitterHeight));
            if (ImGui.IsItemHovered() || ImGui.IsItemActive())
            {
                ImGui.SetMouseCursor(ResizeNsCursor);
            }

            if (ImGui.IsItemActive() && totalAvailableHeight > 0f)
            {
                var delta = ImGui.GetIO().MouseDelta.Y;
                if (Math.Abs(delta) > float.Epsilon)
                {
                    var minHeight = totalAvailableHeight * MinComposeSplitRatio;
                    var maxHeight = totalAvailableHeight * MaxComposeSplitRatio;
                    var newComposeHeight = Math.Clamp(composeAreaHeight - delta, minHeight, maxHeight);
                    var newRatio = Math.Clamp(newComposeHeight / totalAvailableHeight, MinComposeSplitRatio, MaxComposeSplitRatio);
                    if (Math.Abs(newRatio - _config.ChatInputSplitRatio) > 0.0001f)
                    {
                        _config.ChatInputSplitRatio = newRatio;
                        composeAreaHeight = newComposeHeight;
                    }
                }
            }

            var splitterMin = ImGui.GetItemRectMin();
            var splitterMax = ImGui.GetItemRectMax();
            var drawList = ImGui.GetWindowDrawList();
            var separatorColor = ImGui.GetColorU32(ImGuiCol.Separator);
            var centerY = (splitterMin.Y + splitterMax.Y) * 0.5f;
            drawList.AddLine(new Vector2(splitterMin.X, centerY), new Vector2(splitterMax.X, centerY), separatorColor);
        }

        composeRatio = _config.ChatInputSplitRatio;
        composeAreaHeight = Math.Max(0f, totalAvailableHeight * composeRatio);

        _bridge.Ack(CurrentChannelId, _config.GuildId, _channelKind);
        SaveConfig();

        var composerFontScope = PushWindowFontScale(fontScale);

        if (_replyToId != null)
        {
            var refMsg = _messages.Find(m => m.Id == _replyToId);
            if (refMsg != null)
            {
                var preview = refMsg.Content ?? string.Empty;
                if (preview.Length > 50) preview = preview.Substring(0, 50) + "...";
                {
                    using var emojiFont = _emojiManager.PushEmojiFont();
                    ImGui.TextUnformatted($"Replying to {refMsg.Author?.Name ?? "Unknown"}: {preview}");
                }
                ImGui.SameLine();
                if (ImGui.SmallButton("Cancel Reply"))
                {
                    _replyToId = null;
                }
            }
        }

        if (ImGui.BeginPopup("editMessage"))
        {
            unsafe
            {
                _ = ImGui.InputTextMultiline(
                    "##editContent",
                    ref _editContent,
                    2048u,
                    new Vector2(400, ImGui.GetTextLineHeight() * 5)
                );
            }

            if (ImGui.Button("Save"))
            {
                if (_editingMessageId != null)
                {
                    _ = EditMessage(_editingMessageId, _editingChannelId, _editContent);
                }
                _editEmbedColor = null;
                _editEmbedBorder = null;
                _editingMessageId = null;
                ImGui.CloseCurrentPopup();
            }
            ImGui.SameLine();
            if (ImGui.Button("Cancel"))
            {
                _editEmbedColor = null;
                _editEmbedBorder = null;
                _editingMessageId = null;
                ImGui.CloseCurrentPopup();
            }
            ImGui.EndPopup();
        }

        if (ImGui.Button("Attach"))
        {
            _fileDialog.OpenFileDialog(
                "Select Attachment",
                "All files{.*}",
                (bool ok, List<string> files) =>
                {
                    if (!ok) return;
                    foreach (var file in files)
                    {
                        try
                        {
                            using var stream = File.OpenRead(file);
                            _attachments.Add(file);
                            InvalidatePreview();
                        }
                        catch (Exception)
                        {
                            _attachmentError = $"Unable to open {Path.GetFileName(file)}";
                        }
                    }
                },
                1,
                "."); // start directory
        }
        if (!string.IsNullOrEmpty(_attachmentError))
        {
            ImGui.SameLine();
            ImGui.PushStyleColor(ImGuiCol.Text, new Vector4(1f, 0f, 0f, 1f));
            ImGui.TextUnformatted("!");
            ImGui.PopStyleColor();
            if (ImGui.IsItemHovered())
            {
                ImGui.SetTooltip(_attachmentError);
            }
        }
        if (_attachments.Count > 0)
        {
            DrawAttachmentChips();
        }

        var now = DateTime.UtcNow;
        foreach (var key in _typingUsers.Where(kvp => kvp.Value.Expires < now).Select(kvp => kvp.Key).ToList())
        {
            _typingUsers.Remove(key);
        }
        if (_typingUsers.Count > 0)
        {
            var names = string.Join(", ", _typingUsers.Values.Select(v => v.Name));
            var verb = _typingUsers.Count > 1 ? "are" : "is";
            {
                using var emojiFont = _emojiManager.PushEmojiFont();
                ImGui.TextUnformatted($"{names} {verb} typing...");
            }
        }

        DrawEmbedStyleControls();

        if (ImGui.SmallButton("B")) WrapSelection("**", "**");
        ImGui.SameLine();
        if (ImGui.SmallButton("I")) WrapSelection("*", "*");
        ImGui.SameLine();
        if (ImGui.SmallButton("Code")) WrapSelection("`", "`");
        ImGui.SameLine();
        if (ImGui.SmallButton("Spoiler")) WrapSelection("||", "||");
        ImGui.SameLine();
        if (ImGui.SmallButton("Link")) WrapSelection("[", "](url)");

        var availableWidth = ImGui.GetContentRegionAvail().X;
        var framePadding = style.FramePadding.X * 2f;
        var emojiButtonWidth = 0f;
        using (var _ = _emojiManager.PushEmojiFont())
        {
            emojiButtonWidth = ImGui.CalcTextSize("😊").X + framePadding;
        }
        var sendButtonWidth = ImGui.CalcTextSize("Send").X + framePadding;
        var spacing = style.ItemSpacing.X * 2f;
        var inputWidth = Math.Max(120f, availableWidth - emojiButtonWidth - sendButtonWidth - spacing);
        var textForCalc = string.IsNullOrEmpty(_input) ? " " : _input;
        var lineHeight = ImGui.GetTextLineHeight();
        if (lineHeight <= 0f)
        {
            lineHeight = 1f;
        }
        var textSize = ImGui.CalcTextSize(textForCalc, false, inputWidth);
        var wrapLineCount = Math.Max(1, (int)MathF.Ceiling(textSize.Y / lineHeight));
        var newlineCount = string.IsNullOrEmpty(_input) ? 1 : _input.Count(c => c == '\n') + 1;
        var lineCount = Math.Clamp(Math.Max(newlineCount, wrapLineCount), MinInputLines, MaxInputLines);
        var inputHeight = lineCount * lineHeight + style.FramePadding.Y * 2f;
        if (_focusComposerNextFrame)
        {
            ImGui.SetKeyboardFocusHere();
            _focusComposerNextFrame = false;
        }
        _activeInputCallbackOwner = this;
        try
        {
            _ = ImGui.InputTextMultiline(
                "##chatInput",
                ref _input,
                2048u,
                new Vector2(inputWidth, inputHeight),
                ImGuiInputTextFlags.EnterReturnsTrue | ImGuiInputTextFlags.CallbackAlways,
                _inputEditedCallback,
                IntPtr.Zero
            );
        }
        finally
        {
            _activeInputCallbackOwner = null;
        }
        if (MentionsEnabled)
        {
            var mentionState = EnsureMentionDrawerState();
            mentionState.AnchorMin = ImGui.GetItemRectMin();
            mentionState.AnchorMax = ImGui.GetItemRectMax();
        }

        var composerActive = ImGui.IsItemActive();
        var inputEdited = ImGui.IsItemEdited();
        if (MentionsEnabled)
        {
            UpdateMentionState(composerActive, inputEdited);
        }

        io = ImGui.GetIO();
        var mentionHandledSubmit = MentionsEnabled && composerActive
            ? HandleMentionKeys(io, composerActive)
            : false;
        var send = false;
        if (composerActive && ImGui.IsKeyPressed(ImGuiKey.Enter))
        {
            if (mentionHandledSubmit)
            {
                // handled by mention drawer
            }
            else if (io.KeyShift)
            {
                var start = Math.Min(_selectionStart, _selectionEnd);
                var end = Math.Max(_selectionStart, _selectionEnd);
                start = Math.Clamp(start, 0, _input.Length);
                end = Math.Clamp(end, 0, _input.Length);
                var builder = new StringBuilder(_input.Length + 1);
                if (start > 0)
                {
                    builder.Append(_input.AsSpan(0, start));
                }
                builder.Append('\n');
                if (end < _input.Length)
                {
                    builder.Append(_input.AsSpan(end));
                }
                _input = builder.ToString();
                _selectionStart = _selectionEnd = start + 1;
                ImGui.SetKeyboardFocusHere(-1);
            }
            else
            {
                send = true;
            }
        }

        ImGui.SameLine();
        using (var _ = _emojiManager.PushEmojiFont())
        {
            if (ImGui.Button("😊")) ImGui.OpenPopup("##dc_emoji_picker");
        }
        if (ImGui.BeginPopup("##dc_emoji_picker"))
        {
            var inserted = _emojiPicker.Draw();
            if (!string.IsNullOrEmpty(inserted))
            {
                InsertTextAtSelection(inserted);
                ImGui.CloseCurrentPopup();
            }
            ImGui.EndPopup();
        }

        ImGui.SameLine();
        if (ImGui.Button("Send") || send)
        {
            if (MentionsEnabled)
            {
                DismissMentionDrawer(immediate: true);
            }
            _ = SendMessage();
        }

        if (MentionsEnabled)
        {
            DrawMentionDrawer();
        }

        if (!string.IsNullOrEmpty(_input) || _attachments.Count > 0)
        {
            UpdatePreviewMessage();

            var plainTextPreview = GetPreviewPlainText(_previewMessage);

            using (var emojiFont = _emojiManager.PushEmojiFont())
            {
                var previewStyle = ImGui.GetStyle();
                var available = ImGui.GetContentRegionAvail();
                var previewLineHeight = ImGui.GetTextLineHeightWithSpacing();
                var minPreviewHeight = MathF.Max(previewLineHeight * 2f, previewLineHeight * MinInputLines);
                var fallbackHeight = previewLineHeight * 6f;
                var availableHeight = available.Y;
                if (!float.IsFinite(availableHeight) || availableHeight <= 0f)
                {
                    availableHeight = fallbackHeight;
                }

                var maxPreviewHeight = MathF.Max(minPreviewHeight, availableHeight);
                var desiredPreviewHeight = _previewContentHeight > 0f ? _previewContentHeight : fallbackHeight;
                var previewHeight = Math.Clamp(desiredPreviewHeight, minPreviewHeight, maxPreviewHeight);

                ImGui.BeginChild("##inputPreview", new Vector2(0, previewHeight), ImGuiChildFlags.Border, ImGuiWindowFlags.None);
                var childStartCursor = ImGui.GetCursorPosY();
                if (!string.IsNullOrEmpty(plainTextPreview))
                {
                    ImGui.TextWrapped(plainTextPreview);
                }

                var embedSizeCap = GetConfiguredAttachmentCap();
                var embedIndex = 0;
                foreach (var embed in _previewMessage.Embeds)
                {
                    var allowAutoLoad = !_lazyLoadEmbedsEnabled || _previewForceLoadEmbeds;
                    var result = EmbedPreviewRenderer.Draw(
                        embed,
                        LoadTexture,
                        _emojiManager,
                        allowAutoLoad,
                        embedSizeCap,
                        embedSizeCap);

                    if (!allowAutoLoad && result.AnyDeferred)
                    {
                        ImGui.Spacing();
                        var lazyLoadStyle = ImGui.GetStyle();
                        ImGui.PushStyleColor(ImGuiCol.Text, lazyLoadStyle.Colors[(int)ImGuiCol.TextDisabled]);
                        ImGui.TextUnformatted("Images not loaded (lazy-load enabled).");
                        ImGui.PopStyleColor();
                        ImGui.SameLine();
                        var buttonId = !string.IsNullOrEmpty(embed.Id)
                            ? embed.Id!
                            : embedIndex.ToString(CultureInfo.InvariantCulture);
                        if (ImGui.SmallButton($"Load images##embedprev{buttonId}"))
                        {
                            _previewForceLoadEmbeds = true;
                        }
                    }

                    embedIndex++;
                }

                foreach (var att in _previewMessage.Attachments)
                {
                    RenderAttachmentPreview(att);
                }

                var childEndCursor = ImGui.GetCursorPosY();
                ImGui.EndChild();

                var measuredHeight = (childEndCursor - childStartCursor) + (previewStyle.WindowPadding.Y * 2f);
                if (float.IsFinite(measuredHeight) && measuredHeight > 0f)
                {
                    var absoluteMax = previewLineHeight * 24f;
                    _previewContentHeight = Math.Clamp(measuredHeight, minPreviewHeight, absoluteMax);
                }
            }

            foreach (var warning in _previewMessage.Warnings)
            {
                ImGui.PushStyleColor(ImGuiCol.Text, new Vector4(1f, 0.85f, 0.3f, 1f));
                ImGui.TextWrapped(warning);
                ImGui.PopStyleColor();
            }

            foreach (var error in _previewMessage.Errors)
            {
                ImGui.PushStyleColor(ImGuiCol.Text, new Vector4(1f, 0.4f, 0.4f, 1f));
                ImGui.TextWrapped(error);
                ImGui.PopStyleColor();
            }
        }
        else
        {
            _previewContentHeight = 0f;
        }

        if (!string.IsNullOrEmpty(_lastError))
        {
            ImGui.PushStyleColor(ImGuiCol.Text, new Vector4(1f, 0.4f, 0.4f, 1f));
            ImGui.TextWrapped(_lastError);
            ImGui.PopStyleColor();
        }
        else if (!string.IsNullOrEmpty(_statusMessage))
        {
            ImGui.TextUnformatted(_statusMessage);
        }

        composerFontScope.Dispose();
    }

    protected List<ChannelDto> PrepareChannelsForDisplay(IEnumerable<ChannelDto> channels)
    {
        var channelList = channels?.ToList() ?? new List<ChannelDto>();
        var hasStoredGuild = !ChannelKeyHelper.IsDefaultGuild(_config.GuildId);
        var normalizedStoredGuild = ChannelKeyHelper.NormalizeGuildId(_config.GuildId);

        if (!hasStoredGuild)
        {
            var channelWithGuild = channelList.FirstOrDefault(c => !string.IsNullOrWhiteSpace(c.GuildId));
            if (channelWithGuild != null && !string.IsNullOrWhiteSpace(channelWithGuild.GuildId))
            {
                normalizedStoredGuild = ChannelKeyHelper.NormalizeGuildId(channelWithGuild.GuildId);
                _config.GuildId = normalizedStoredGuild;
                ChannelWatcher.Instance?.InvalidateCache();
                SaveConfig();
                hasStoredGuild = true;
            }
        }

        if (hasStoredGuild)
        {
            channelList = channelList
                .Where(c =>
                    string.IsNullOrWhiteSpace(c.GuildId) ||
                    string.Equals(
                        ChannelKeyHelper.NormalizeGuildId(c.GuildId),
                        normalizedStoredGuild,
                        StringComparison.Ordinal))
                .ToList();
        }

        return ChannelDtoExtensions.SortForDisplay(channelList);
    }

    public void SetChannels(IEnumerable<ChannelDto> channels)
    {
        var prepared = PrepareChannelsForDisplay(channels);
        ApplyPreparedChannels(prepared);
    }

    protected void ApplyPreparedChannels(List<ChannelDto> channels)
    {
        _channels.Clear();
        _channels.AddRange(channels);
        UpdateChannelDisplayNames();
        if (_channels.Count == 0)
        {
            _selectedIndex = 0;
            _channelSelection.SetChannel(_channelKind, _config.GuildId, string.Empty);
            return;
        }

        var current = CurrentChannelId;
        if (!string.IsNullOrEmpty(current))
        {
            _selectedIndex = _channels.FindIndex(c => c.Id == current);
        }

        if (_selectedIndex < 0 || _selectedIndex >= _channels.Count)
        {
            _selectedIndex = 0;
        }

        var newId = _channels[_selectedIndex].Id;
        _channelSelection.SetChannel(_channelKind, _config.GuildId, newId);
    }

    private void UpdateChannelDisplayNames()
    {
        if (_channels.Count == 0)
        {
            _channelDisplayNames = Array.Empty<string>();
            return;
        }

        _channelDisplayNames = _channels
            .Select(c => c.ParentId == null ? c.Name : "  " + c.Name)
            .ToArray();
    }

    internal void OnGuildUpdated()
    {
        var framework = PluginServices.Instance?.Framework;
        if (framework != null)
        {
            framework.RunOnTick(() => TrySubscribeCurrentChannel(force: true));
        }
        else
        {
            TrySubscribeCurrentChannel(force: true);
        }
    }

    private bool EnsureChannelAvailable(string channelId, string action)
    {
        if (_channels.Any(c => c.Id == channelId))
        {
            return true;
        }

        PluginServices.Instance!.Log.Warning($"Cannot {action}: channel {channelId} is no longer available.");
        _ = PluginServices.Instance!.Framework.RunOnTick(() =>
        {
            _statusMessage = ChannelUnavailableMessage;
            _lastError = ChannelUnavailableMessage;
        });

        return false;
    }

    private static ImGuiMouseCursor ResolveResizeNsCursor()
    {
        if (Enum.TryParse("ResizeNS", ignoreCase: true, out ImGuiMouseCursor cursor))
        {
            return cursor;
        }

        return ImGuiMouseCursor.ResizeAll;
    }

    private Vector2 CalculateAttachmentDisplaySize(Vector2 originalSize, Vector2 maxSize, bool allowAutoStretch = true)
    {
        var width = MathF.Max(1f, originalSize.X);
        var height = MathF.Max(1f, originalSize.Y);
        var maxWidth = MathF.Max(1f, maxSize.X);
        var maxHeight = MathF.Max(1f, maxSize.Y);

        if (allowAutoStretch && _config.ChatImageAutoStretch)
        {
            var widthScale = maxWidth / width;
            var heightScale = maxHeight / height;
            var scale = MathF.Min(widthScale, heightScale);

            if (!float.IsFinite(scale) || scale <= 0f)
            {
                scale = 1f;
            }

            return new Vector2(width * scale, height * scale);
        }

        var manualScale = GetManualImageScale();

        var scaledWidth = width * manualScale;
        var scaledHeight = height * manualScale;

        var widthClamp = maxWidth / MathF.Max(1f, scaledWidth);
        var heightClamp = maxHeight / MathF.Max(1f, scaledHeight);
        var clampScale = MathF.Min(1f, MathF.Min(widthClamp, heightClamp));

        if (clampScale < 1f)
        {
            scaledWidth *= clampScale;
            scaledHeight *= clampScale;
        }

        return new Vector2(scaledWidth, scaledHeight);
    }

    public virtual void OnAppearanceSettingsChanged()
    {
        _pendingInitialScroll = true;
        InvalidatePreview();
    }

    protected void FormatContent(DiscordMessageDto msg, bool isHovered)
    {
        _ = isHovered;
        using var emojiFont = _emojiManager.PushEmojiFont();
        var text = msg.Content ?? string.Empty;
        text = ReplaceMentionTokens(text, msg.Mentions);
        text = MarkdownFormatter.Format(text);
        text = EmojiFormatter.NormalizeCustomTokens(_emojiManager, text);
        var parts = Regex.Split(text, "(<a?:[a-zA-Z0-9_]+:\\d+>)");
        ImGui.PushTextWrapPos();
        var first = true;
        foreach (var part in parts)
        {
            if (string.IsNullOrEmpty(part))
                continue;
            if (!first)
                ImGui.SameLine(0, 0);
            var guildMatch = Regex.Match(part, "^<a?:([a-zA-Z0-9_]+):(\\d+)>$");
            if (guildMatch.Success)
            {
                var name = guildMatch.Groups[1].Value;
                var id = guildMatch.Groups[2].Value;
                var animated = part.StartsWith("<a:");
                if (!_emojiCatalog.TryGetValue(id, out var emoji))
                {
                    emoji = new EmojiData(id, animated);
                    _emojiCatalog[id] = emoji;
                }
                if (emoji.Texture == null)
                    LoadTexture(emoji.ImageUrl, t => emoji.Texture = t);
                if (emoji.Texture != null && TryGetTextureWrap(emoji.Texture, out var wrap))
                {
                    ImGui.Image(wrap.ToImGuiHandle(), new Vector2(20, 20));
                }
                else
                {
                    ImGui.TextUnformatted($":{name}:");
                }
            }
            else
            {
                RenderMarkdown(part);
            }
            first = false;
        }
        ImGui.PopTextWrapPos();
    }

    internal static string ReplaceMentionTokens(string text, IReadOnlyList<DiscordMentionDto>? mentions)
    {
        if (mentions == null)
            return text;

        foreach (var m in mentions)
        {
            switch (m.Type)
            {
                case "user":
                    text = text.Replace($"<@{m.Id}>", $"@{m.Name}");
                    text = text.Replace($"<@!{m.Id}>", $"@{m.Name}");
                    break;
                case "role":
                    text = text.Replace($"<@&{m.Id}>", $"@{m.Name}");
                    break;
                case "channel":
                    text = text.Replace($"<#{m.Id}>", $"#{m.Name}");
                    break;
            }
        }

        return text;
    }

    private void RenderMarkdown(string text)
    {
        var codeBlock = Regex.Match(text, "^\\[CODEBLOCK\\](.+?)\\[/CODEBLOCK\\]$", RegexOptions.Singleline);
        if (codeBlock.Success)
        {
            ImGui.BeginChild($"codeblock{GetHashCode()}{text.GetHashCode()}", new Vector2(0, 0), ImGuiChildFlags.Border, ImGuiWindowFlags.None);
            ImGui.TextUnformatted(codeBlock.Groups[1].Value);
            ImGui.EndChild();
            return;
        }

        var inlineCode = Regex.Match(text, "^\\[CODE\\](.+?)\\[/CODE\\]$");
        if (inlineCode.Success)
        {
            ImGui.TextUnformatted(inlineCode.Groups[1].Value);
            return;
        }

        var quote = Regex.Match(text, "^\\[QUOTE\\](.+?)\\[/QUOTE\\]$", RegexOptions.Singleline);
        if (quote.Success)
        {
            ImGui.Indent();
            ImGui.TextUnformatted(quote.Groups[1].Value);
            ImGui.Unindent();
            return;
        }

        var spoiler = Regex.Match(text, "^\\[SPOILER\\](.+?)\\[/SPOILER\\]$", RegexOptions.Singleline);
        if (spoiler.Success)
        {
            ImGui.TextUnformatted(spoiler.Groups[1].Value);
            return;
        }

        var strike = Regex.Match(text, "^\\[S\\](.+?)\\[/S\\]$");
        if (strike.Success)
        {
            var content = strike.Groups[1].Value;
            var start = ImGui.GetCursorScreenPos();
            ImGui.TextUnformatted(content);
            var size = ImGui.CalcTextSize(content);
            var dl = ImGui.GetWindowDrawList();
            dl.AddLine(new Vector2(start.X, start.Y + size.Y / 2), new Vector2(start.X + size.X, start.Y + size.Y / 2), 0xFF000000);
            return;
        }

        text = Regex.Replace(text, "\\[(?:B|I|U)\\]", "");
        text = Regex.Replace(text, "\\[/(?:B|I|U)\\]", "");
        text = Regex.Replace(text, "\\[LINK=([^\\]]+)\\](.+?)\\[/LINK\\]", "$2 ($1)");
        ImGui.TextUnformatted(text);
    }

    // Correct signature for Dalamud's callback: unsafe pointer, returns int
    private static unsafe int OnInputEdited(ImGuiInputTextCallbackData* data)
    {
        if (data == null)
        {
            return 0;
        }

        var owner = _activeInputCallbackOwner;
        if (owner == null)
        {
            return 0;
        }

        owner._selectionStart = data->SelectionStart;
        owner._selectionEnd = data->SelectionEnd;
        return 0;
    }

    private MentionDrawerState EnsureMentionDrawerState()
        => _mentionDrawerState ??= new MentionDrawerState();

    private void UpdateMentionState(bool composerActive, bool inputEdited)
    {
        var input = _input ?? string.Empty;
        var caret = Math.Clamp(Math.Min(_selectionStart, _selectionEnd), 0, input.Length);

        if (!composerActive || _selectionStart != _selectionEnd)
        {
            DismissMentionDrawer();
            return;
        }

        var token = FindMentionToken(input, caret);
        if (!token.HasValue)
        {
            DismissMentionDrawer();
            return;
        }

        var state = EnsureMentionDrawerState();
        if (state.SuppressedQuery != null)
        {
            if (token.Value.Start == state.SuppressedStart &&
                token.Value.End == state.SuppressedEnd &&
                string.Equals(token.Value.Query, state.SuppressedQuery, StringComparison.Ordinal))
            {
                state.HighlightedIndex = -1;
                state.Active = false;
                return;
            }

            state.SuppressedStart = -1;
            state.SuppressedEnd = -1;
            state.SuppressedQuery = null;
        }
        var previousStart = state.TokenStart;
        var previousEnd = state.TokenEnd;
        var previousQuery = state.Query;

        state.TokenStart = token.Value.Start;
        state.TokenEnd = token.Value.End;
        state.Query = token.Value.Query;

        var shouldRefresh = inputEdited ||
            state.Candidates.Count == 0 ||
            previousStart != token.Value.Start ||
            previousEnd != token.Value.End ||
            !string.Equals(previousQuery, token.Value.Query, StringComparison.Ordinal);

        if (shouldRefresh)
        {
            var matches = BuildMentionCandidates(token.Value.Query);
            state.ClearCandidates();
            state.Candidates.AddRange(matches);
            state.HighlightedIndex = state.Candidates.Count > 0 ? 0 : -1;
        }
        else if (state.HighlightedIndex >= state.Candidates.Count)
        {
            state.HighlightedIndex = state.Candidates.Count - 1;
        }

        if (state.Candidates.Count == 0)
        {
            state.HighlightedIndex = -1;
        }

        state.Active = state.Candidates.Count > 0;
    }

    private bool HandleMentionKeys(ImGuiIOPtr io, bool composerActive)
    {
        var state = _mentionDrawerState;
        if (state == null)
        {
            return false;
        }

        var hasCandidates = state.Active && state.Candidates.Count > 0;
        if (!composerActive)
        {
            if (ImGui.IsKeyPressed(ImGuiKey.Escape))
            {
                DismissMentionDrawer();
                return true;
            }

            return false;
        }

        if (hasCandidates)
        {
            if (ImGui.IsKeyPressed(ImGuiKey.DownArrow))
            {
                var next = state.HighlightedIndex + 1;
                if (next >= state.Candidates.Count)
                {
                    next = 0;
                }
                state.HighlightedIndex = Math.Max(0, next);
            }
            else if (ImGui.IsKeyPressed(ImGuiKey.UpArrow))
            {
                var prev = state.HighlightedIndex - 1;
                if (prev < 0)
                {
                    prev = state.Candidates.Count - 1;
                }
                state.HighlightedIndex = Math.Max(0, prev);
            }
        }

        if (ImGui.IsKeyPressed(ImGuiKey.Escape))
        {
            DismissMentionDrawer(suppress: true);
            return true;
        }

        var inserted = false;
        if (hasCandidates && ImGui.IsKeyPressed(ImGuiKey.Tab, repeat: false))
        {
            inserted = TryInsertHighlightedMention();
        }
        else if (hasCandidates && ImGui.IsKeyPressed(ImGuiKey.Enter) && !io.KeyShift)
        {
            inserted = TryInsertHighlightedMention();
        }

        return inserted;
    }

    private void DrawMessageRow(int index, ImGuiHoveredFlags hoverFlags)
    {
        if (index < 0 || index >= _messages.Count)
        {
            return;
        }

        var msg = _messages[index];
        ImGui.PushID(msg.Id);

        var shouldLoadTextures = ShouldLoadRowTextures(msg.Id);
        using var textureScope = new TextureLoadScope(this, shouldLoadTextures);
        using var emojiFont = _emojiManager.PushEmojiFont();

        var drawList = ImGui.GetWindowDrawList();
        drawList.ChannelsSplit(2);
        drawList.ChannelsSetCurrent(1);

        var hoveredLastFrame = _messageHoverStates.TryGetValue(msg.Id, out var wasHovered) && wasHovered;
        var styleForRow = ImGui.GetStyle();
        var pushedHoverText = false;
        if (hoveredLastFrame)
        {
            var baseTextColor = styleForRow.Colors[(int)ImGuiCol.Text];
            var hoverTextColor = Vector4.Lerp(baseTextColor, Vector4.One, MessageHoverTextBlend);
            ImGui.PushStyleColor(ImGuiCol.Text, hoverTextColor);
            pushedHoverText = true;
        }

        ImGui.BeginGroup();
        if (msg.Author != null && msg.AvatarTexture == null && _avatarCache != null)
        {
            _ = _avatarCache.GetAsync(msg.Author.AvatarUrl, msg.Author.Id)
                .ContinueWith(t => PluginServices.Instance!.Framework.RunOnTick(() => msg.AvatarTexture = t.Result));
        }

        if (msg.AvatarTexture != null)
        {
            var wrapAvatar = msg.AvatarTexture.GetWrapOrEmpty();
            var avatarHandle = wrapAvatar.ToImGuiHandle();
            if (avatarHandle != 0)
            {
                ImGui.Image(avatarHandle, new Vector2(20, 20));
            }
            else
            {
                ImGui.Dummy(new Vector2(20, 20));
            }
        }
        else
        {
            ImGui.Dummy(new Vector2(20, 20));
        }

        ImGui.SameLine();

        ImGui.BeginGroup();
        ImGui.TextUnformatted(msg.Author?.Name ?? "Unknown");
        ImGui.SameLine();
        ImGui.TextUnformatted(msg.Timestamp.ToLocalTime().ToString());

        if (msg.Reference?.MessageId != null)
        {
            var refMsg = _messages.Find(m => m.Id == msg.Reference.MessageId);
            if (refMsg != null)
            {
                var preview = refMsg.Content ?? string.Empty;
                if (preview.Length > 50)
                {
                    preview = preview[..50] + "...";
                }

                ImGui.PushStyleColor(ImGuiCol.Text, new Vector4(0.7f, 0.7f, 0.7f, 1f));
                ImGui.TextUnformatted($"> {refMsg.Author?.Name ?? "Unknown"}: {preview}");
                ImGui.PopStyleColor();
            }
        }

        FormatContent(msg, hoveredLastFrame);

        if (msg.EditedTimestamp != null)
        {
            ImGui.SameLine();
            ImGui.PushStyleColor(ImGuiCol.Text, new Vector4(0.6f, 0.6f, 0.6f, 1f));
            ImGui.TextUnformatted("(edited)");
            ImGui.PopStyleColor();
        }

        if (msg.Embeds != null)
        {
            foreach (var embed in msg.Embeds)
            {
                EmbedRenderer.Draw(embed, LoadTexture, _emojiManager, cid => _ = Interact(msg.Id, msg.ChannelId, cid));
            }
        }

        if (msg.Attachments != null)
        {
            var attachmentCap = GetConfiguredAttachmentCap();
            foreach (var att in msg.Attachments)
            {
                if (att.ContentType != null && att.ContentType.StartsWith("image", StringComparison.OrdinalIgnoreCase))
                {
                    if (att.Texture == null)
                    {
                        LoadTexture(att.Url, t => att.Texture = t);
                    }

                    if (att.Texture != null)
                    {
                        var wrapAtt = att.Texture.GetWrapOrEmpty();
                        var attachmentHandle = wrapAtt.ToImGuiHandle();
                        if (attachmentHandle == 0)
                        {
                            continue;
                        }

                        var originalSize = new Vector2(wrapAtt.Width, wrapAtt.Height);
                        var bounds = GetAttachmentBounds(attachmentCap);
                        var displaySize = CalculateAttachmentDisplaySize(originalSize, bounds);
                        ImGui.Image(attachmentHandle, displaySize);
                        if (ImGui.IsItemHovered())
                        {
                            ImGui.BeginTooltip();
                            ImGui.TextUnformatted("Open original");
                            ImGui.EndTooltip();
                        }

                        if (ImGui.IsItemClicked())
                        {
                            try
                            {
                                Process.Start(new ProcessStartInfo(att.Url) { UseShellExecute = true });
                            }
                            catch
                            {
                                // ignore shell exceptions
                            }
                        }
                    }
                }
                else
                {
                    ImGui.PushStyleColor(ImGuiCol.Text, new Vector4(0.2f, 0.6f, 1f, 1f));
                    ImGui.TextUnformatted(att.Filename ?? att.Url);
                    if (ImGui.IsItemClicked())
                    {
                        try
                        {
                            Process.Start(new ProcessStartInfo(att.Url) { UseShellExecute = true });
                        }
                        catch
                        {
                            // ignore shell exceptions
                        }
                    }

                    ImGui.PopStyleColor();
                }
            }
        }

        if (msg.Components != null && msg.Components.Count > 0)
        {
            var buttons = msg.Components.Select(c => new EmbedButtonDto
            {
                Label = c.Label,
                CustomId = c.CustomId,
                Url = c.Url,
                Emoji = c.Emoji,
                Style = c.Style,
                RowIndex = c.RowIndex
            }).ToList();

            var pseudo = new EmbedDto { Id = msg.Id + "_components", Buttons = buttons };
            EmbedRenderer.Draw(pseudo, LoadTexture, _emojiManager, cid => _ = Interact(msg.Id, msg.ChannelId, cid));
        }

        ImGui.Spacing();

        if (msg.Reactions != null && msg.Reactions.Count > 0)
        {
            for (var j = 0; j < msg.Reactions.Count; j++)
            {
                var reaction = msg.Reactions[j];
                ImGui.BeginGroup();
                var handled = false;

                if (!string.IsNullOrEmpty(reaction.EmojiId))
                {
                    if (reaction.Texture == null)
                    {
                        var ext = reaction.IsAnimated ? "gif" : "png";
                        var url = $"https://cdn.discordapp.com/emojis/{reaction.EmojiId}.{ext}";
                        LoadTexture(url, t => reaction.Texture = t);
                    }

                    if (reaction.Texture != null && TryGetTextureWrap(reaction.Texture, out var wrap))
                    {
                        if (ImGui.ImageButton(
                                $"##react{msg.Id}{reaction.EmojiId ?? reaction.Emoji}",
                                wrap.ToImGuiHandle(),
                                new Vector2(20, 20),
                                Vector2.Zero,
                                Vector2.One,
                                Vector4.Zero,
                                Vector4.One))
                        {
                            _ = React(msg.Id, reaction.Emoji, reaction.Me);
                        }

                        ImGui.SameLine();
                        ImGui.TextUnformatted(reaction.Count.ToString());
                        handled = true;
                    }
                }

                if (!handled)
                {
                    if (ImGui.SmallButton($"{reaction.Emoji} {reaction.Count}##{msg.Id}{reaction.Emoji}"))
                    {
                        _ = React(msg.Id, reaction.Emoji, reaction.Me);
                    }
                }

                ImGui.EndGroup();
                if (j < msg.Reactions.Count - 1)
                {
                    ImGui.SameLine();
                }
            }
        }

        if (ImGui.SmallButton($"+##react{msg.Id}"))
        {
            ImGui.OpenPopup($"reactPicker{msg.Id}");
        }

        if (ImGui.BeginPopup($"reactPicker{msg.Id}"))
        {
            ImGui.TextUnformatted("Pick an emoji:");
            foreach (var emoji in DefaultReactions)
            {
                if (ImGui.Button($"{emoji}##pick{msg.Id}{emoji}"))
                {
                    _ = React(msg.Id, emoji, false);
                    ImGui.CloseCurrentPopup();
                }

                ImGui.SameLine();
            }

            ImGui.NewLine();
            var pick = _emojiPicker.Draw();
            if (!string.IsNullOrEmpty(pick))
            {
                _ = React(msg.Id, pick, false);
                ImGui.CloseCurrentPopup();
            }

            ImGui.EndPopup();
        }

        ImGui.EndGroup();

        if (pushedHoverText)
        {
            ImGui.PopStyleColor();
        }

        var rowMin = ImGui.GetItemRectMin();
        var rowMax = ImGui.GetItemRectMax();
        var rowHovered = ImGui.IsItemHovered(hoverFlags);
        _messageHoverStates[msg.Id] = rowHovered;
        if (!string.IsNullOrEmpty(msg.Id))
        {
            _messageRectCache[msg.Id] = (rowMin, rowMax);
        }

        if (ImGui.BeginPopupContextItem("messageContext"))
        {
            if (ImGui.MenuItem("Reply"))
            {
                _replyToId = msg.Id;
            }

            if (ImGui.MenuItem("Edit"))
            {
                _editingMessageId = msg.Id;
                _editingChannelId = msg.ChannelId;
                _editContent = msg.Content ?? string.Empty;
                var firstEmbed = msg.Embeds?.FirstOrDefault();
                _editEmbedColor = firstEmbed?.Color;
                _editEmbedBorder = firstEmbed?.Border != null
                    ? new EmbedBorderRenderDto
                    {
                        Enabled = firstEmbed.Border.Enabled,
                        Glyph = firstEmbed.Border.Glyph,
                        Color = firstEmbed.Border.Color
                    }
                    : null;
                ImGui.OpenPopup("editMessage");
            }

            if (ImGui.MenuItem("Delete"))
            {
                _ = DeleteMessage(msg.Id, msg.ChannelId);
            }

            ImGui.EndPopup();
        }

        drawList.ChannelsSetCurrent(0);
        var bgColor = rowHovered ? MessageHoverBgColor : MessageIdleBgColor;
        if (bgColor.W > 0f)
        {
            var rounding = styleForRow.FrameRounding > 0f ? styleForRow.FrameRounding : 4f * ImGuiHelpers.GlobalScale;
            drawList.AddRectFilled(rowMin, rowMax, ImGui.ColorConvertFloat4ToU32(bgColor), rounding);
        }

        drawList.ChannelsMerge();

        if (index < _messages.Count - 1)
        {
            var spacingY = styleForRow.ItemSpacing.Y * 0.5f;
            if (spacingY > 0f)
            {
                ImGui.Dummy(new Vector2(0f, spacingY));
            }
        }

        ImGui.PopID();
    }

    private void PreloadMessageAttachments(int index)
    {
        if (index < 0 || index >= _messages.Count)
        {
            return;
        }

        var message = _messages[index];
        var shouldLoadTextures = ShouldLoadRowTextures(message.Id);
        if (!shouldLoadTextures)
        {
            return;
        }

        using var textureScope = new TextureLoadScope(this, true);
        if (message.Attachments == null)
        {
            return;
        }

        foreach (var attachment in message.Attachments)
        {
            if (attachment.ContentType != null && attachment.ContentType.StartsWith("image", StringComparison.OrdinalIgnoreCase) && attachment.Texture == null)
            {
                LoadTexture(attachment.Url, t => attachment.Texture = t);
            }
        }
    }

    private void PurgeOffscreenTextures(int visibleStart, int visibleEnd, int keepMargin)
    {
        if (_messages.Count == 0)
        {
            return;
        }

        var safeMargin = Math.Max(0, keepMargin);
        var keepStart = Math.Max(0, visibleStart - safeMargin);
        var keepEnd = Math.Min(_messages.Count, visibleEnd + safeMargin);

        for (var i = 0; i < _messages.Count; i++)
        {
            if (i >= keepStart && i < keepEnd)
            {
                continue;
            }

            var message = _messages[i];
            if (message.Attachments == null)
            {
                continue;
            }

            foreach (var attachment in message.Attachments)
            {
                if (attachment.Texture?.GetWrapOrEmpty() is IDisposable wrap)
                {
                    wrap.Dispose();
                }

                attachment.Texture = null;
            }
        }
    }

    private float CalculateMentionDrawerHeight(MentionDrawerState state, float scale)
    {
        var style = ImGui.GetStyle();
        var windowPaddingY = 6f * scale;
        var totalHeight = windowPaddingY * 2f;
        var textHeight = ImGui.GetTextLineHeight();
        var avatarSize = textHeight + style.FramePadding.Y;
        var rowHeight = Math.Max(avatarSize + style.FramePadding.Y, ImGui.GetFrameHeight());
        var separatorHeight = style.ItemSpacing.Y * 0.5f;
        if (separatorHeight <= 0f)
        {
            separatorHeight = style.FramePadding.Y;
        }

        if (separatorHeight <= 0f)
        {
            var dpiScale = scale;
            if (!float.IsFinite(dpiScale) || dpiScale <= 0f)
            {
                dpiScale = ImGuiHelpers.GlobalScale;
            }

            if (!float.IsFinite(dpiScale) || dpiScale <= 0f)
            {
                dpiScale = 1f;
            }

            separatorHeight = 1f * dpiScale;
        }

        var firstElement = true;

        void AddElement(float height)
        {
            if (!firstElement)
            {
                totalHeight += style.ItemSpacing.Y;
            }

            totalHeight += height;
            firstElement = false;
        }

        MentionCandidateType? currentGroup = null;
        for (var i = 0; i < state.Candidates.Count; i++)
        {
            var candidate = state.Candidates[i];
            if (candidate.Type != currentGroup)
            {
                if (currentGroup != null)
                {
                    AddElement(separatorHeight);
                }

                AddElement(textHeight);
                currentGroup = candidate.Type;
            }

            AddElement(rowHeight);
        }

        return totalHeight;
    }

    private void DrawMentionDrawer()
    {
        var state = _mentionDrawerState;
        if (state == null)
        {
            return;
        }

        var width = state.AnchorMax.X - state.AnchorMin.X;
        if (width <= 0f)
        {
            return;
        }

        var target = state.Active && state.Candidates.Count > 0 ? 1f : 0f;
        var delta = ImGui.GetIO().DeltaTime;
        if (target > state.AnimationProgress)
        {
            state.AnimationProgress = Math.Min(1f, state.AnimationProgress + delta * MentionDrawerAnimationSpeed);
        }
        else
        {
            state.AnimationProgress = Math.Max(0f, state.AnimationProgress - delta * MentionDrawerAnimationSpeed);
        }

        if (state.AnimationProgress <= 0f)
        {
            if (!state.Active)
            {
                state.ClearCandidates();
            }
            return;
        }

        if (state.Candidates.Count == 0)
        {
            return;
        }

        var scale = ImGuiHelpers.GlobalScale;
        var style = ImGui.GetStyle();
        var drawerHeight = CalculateMentionDrawerHeight(state, scale);
        var viewport = ImGui.GetMainViewport();
        var min = viewport.WorkPos;
        var max = min + viewport.WorkSize;
        var availableBelow = MathF.Max(0f, max.Y - state.AnchorMax.Y);
        var availableAbove = MathF.Max(0f, state.AnchorMin.Y - min.Y);
        var anchorAbove = false;

        if (drawerHeight > availableBelow)
        {
            if (availableAbove >= drawerHeight || availableAbove > availableBelow)
            {
                anchorAbove = true;
            }
        }

        var position = anchorAbove
            ? new Vector2(state.AnchorMin.X, state.AnchorMin.Y - MentionDrawerBaseOffset * scale - drawerHeight)
            : new Vector2(state.AnchorMin.X, state.AnchorMax.Y + MentionDrawerBaseOffset * scale);

        var animationOffset = (1f - state.AnimationProgress) * MentionDrawerTravelDistance * scale;
        if (anchorAbove)
        {
            position.Y -= animationOffset;
        }
        else
        {
            position.Y += animationOffset;
        }

        var flags = ImGuiWindowFlags.NoDecoration |
                    ImGuiWindowFlags.NoMove |
                    ImGuiWindowFlags.AlwaysAutoResize |
                    ImGuiWindowFlags.NoSavedSettings |
                    ImGuiWindowFlags.NoFocusOnAppearing |
                    ImGuiWindowFlags.NoNavInputs |
                    ImGuiWindowFlags.NoNav;

        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new Vector2(8f, 6f) * scale);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowRounding, 6f * scale);
        ImGui.PushStyleColor(ImGuiCol.WindowBg, style.Colors[(int)ImGuiCol.PopupBg]);
        ImGui.SetNextWindowPos(position);
        ImGui.SetNextWindowSize(new Vector2(width, 0f));
        ImGui.SetNextWindowBgAlpha(style.Alpha);
        if (ImGui.Begin("##dc_mention_drawer", flags))
        {
            DrawMentionDrawerContents(state);
        }
        ImGui.End();
        ImGui.PopStyleColor();
        ImGui.PopStyleVar(2);
    }

    private void DrawMentionDrawerContents(MentionDrawerState state)
    {
        if (state.Candidates.Count == 0)
        {
            return;
        }

        MentionCandidateType? currentGroup = null;
        var style = ImGui.GetStyle();

        for (var i = 0; i < state.Candidates.Count; i++)
        {
            var candidate = state.Candidates[i];
            if (currentGroup != candidate.Type)
            {
                if (i > 0)
                {
                    ImGui.Separator();
                }

                var header = candidate.Type == MentionCandidateType.User ? "Members" : "Roles";
                ImGui.PushStyleColor(ImGuiCol.Text, style.Colors[(int)ImGuiCol.TextDisabled]);
                ImGui.TextUnformatted(header);
                ImGui.PopStyleColor();
                currentGroup = candidate.Type;
            }

            DrawMentionCandidateRow(state, candidate, i);
        }
    }

    private void DrawMentionCandidateRow(MentionDrawerState state, MentionCandidate candidate, int index)
    {
        var style = ImGui.GetStyle();
        var avatarSize = ImGui.GetTextLineHeight() + style.FramePadding.Y;
        var rowHeight = Math.Max(avatarSize + style.FramePadding.Y, ImGui.GetFrameHeight());

        ImGui.PushID(index);
        var selected = state.HighlightedIndex == index;
        if (ImGui.Selectable("##mention_row", selected, ImGuiSelectableFlags.None, new Vector2(0f, rowHeight)))
        {
            state.HighlightedIndex = index;
            InsertMentionCandidate(state, candidate);
        }

        if (ImGui.IsItemHovered())
        {
            state.HighlightedIndex = index;
        }

        var min = ImGui.GetItemRectMin();
        var max = ImGui.GetItemRectMax();
        var avatarPos = new Vector2(min.X + style.FramePadding.X, min.Y + style.FramePadding.Y);
        var textPos = new Vector2(avatarPos.X + avatarSize + style.ItemSpacing.X, min.Y + style.FramePadding.Y);

        DrawMentionAvatar(candidate, avatarPos, avatarSize);

        ImGui.SetCursorScreenPos(textPos);
        ImGui.BeginGroup();
        if (candidate.AccentColor.HasValue)
        {
            ImGui.PushStyleColor(ImGuiCol.Text, candidate.AccentColor.Value);
            ImGui.TextUnformatted(candidate.Name);
            ImGui.PopStyleColor();
        }
        else
        {
            ImGui.TextUnformatted(candidate.Name);
        }

        if (!string.IsNullOrEmpty(candidate.Subtitle))
        {
            ImGui.PushStyleColor(ImGuiCol.Text, style.Colors[(int)ImGuiCol.TextDisabled]);
            ImGui.TextUnformatted(candidate.Subtitle);
            ImGui.PopStyleColor();
        }
        ImGui.EndGroup();

        ImGui.PopID();
        ImGui.SetCursorScreenPos(new Vector2(min.X, max.Y));
    }

    private void DrawMentionAvatar(MentionCandidate candidate, Vector2 position, float size)
    {
        var textureSize = new Vector2(size, size);
        var drawList = ImGui.GetWindowDrawList();
        var style = ImGui.GetStyle();

        if (candidate.Type == MentionCandidateType.User && candidate.Presence != null)
        {
            var presence = candidate.Presence;
            if (!candidate.AvatarRequested && presence.AvatarTexture == null && !string.IsNullOrEmpty(presence.AvatarUrl))
            {
                candidate.AvatarRequested = true;
                LoadTexture(presence.AvatarUrl, t => presence.AvatarTexture = t);
            }

            var wrap = presence.AvatarTexture?.GetWrapOrEmpty();
            var handle = wrap.ToImGuiHandle();
            if (handle != 0 && wrap != null && wrap.Width > 0 && wrap.Height > 0)
            {
                ImGui.SetCursorScreenPos(position);
                ImGui.Image(handle, textureSize);
                return;
            }
        }

        var accent = candidate.AccentColor ?? style.Colors[(int)ImGuiCol.FrameBgActive];
        var color = ImGui.ColorConvertFloat4ToU32(accent);
        var center = position + new Vector2(size * 0.5f, size * 0.5f);
        drawList.AddCircleFilled(center, size * 0.5f, color);
    }

    private bool TryInsertHighlightedMention()
    {
        var state = _mentionDrawerState;
        if (state == null)
        {
            return false;
        }

        if (state.HighlightedIndex < 0 || state.HighlightedIndex >= state.Candidates.Count)
        {
            return false;
        }

        InsertMentionCandidate(state, state.Candidates[state.HighlightedIndex]);
        return true;
    }

    private void InsertMentionCandidate(MentionDrawerState state, MentionCandidate candidate)
    {
        var input = _input ?? string.Empty;
        var start = Math.Clamp(state.TokenStart, 0, input.Length);
        var end = Math.Clamp(state.TokenEnd, start, input.Length);
        var needsSpace = end >= input.Length || !char.IsWhiteSpace(input[end]);
        var mentionText = $"@{candidate.Name}" + (needsSpace ? " " : string.Empty);

        _selectionStart = start;
        _selectionEnd = end;
        InsertTextAtSelection(mentionText);

        var updatedInput = _input ?? string.Empty;
        if (!needsSpace && _selectionEnd < updatedInput.Length && char.IsWhiteSpace(updatedInput[_selectionEnd]))
        {
            var caret = Math.Min(updatedInput.Length, _selectionEnd + 1);
            _selectionStart = _selectionEnd = caret;
        }

        DismissMentionDrawer();
    }

    private void DismissMentionDrawer(bool immediate = false, bool suppress = false)
    {
        if (_mentionDrawerState == null)
        {
            return;
        }

        var state = _mentionDrawerState;
        var lastStart = state.TokenStart;
        var lastEnd = state.TokenEnd;
        var lastQuery = state.Query;

        state.Active = false;
        state.Query = string.Empty;
        state.TokenStart = 0;
        state.TokenEnd = 0;
        state.HighlightedIndex = -1;

        if (suppress)
        {
            state.SuppressedStart = lastStart;
            state.SuppressedEnd = lastEnd;
            state.SuppressedQuery = lastQuery;
        }
        else
        {
            state.SuppressedStart = -1;
            state.SuppressedEnd = -1;
            state.SuppressedQuery = null;
        }

        if (immediate)
        {
            state.ClearCandidates();
            state.AnimationProgress = 0f;
        }
    }

    private List<MentionCandidate> BuildMentionCandidates(string query)
    {
        var filter = query?.Trim() ?? string.Empty;
        var presences = _presence?.Presences ?? Array.Empty<PresenceDto>();
        var users = new List<MentionCandidate>();

        foreach (var presence in presences)
        {
            if (presence == null || string.IsNullOrWhiteSpace(presence.Name))
                continue;

            if (!IsMentionMatch(presence.Name, filter))
                continue;

            var candidate = new MentionCandidate(MentionCandidateType.User, presence.Id, presence.Name)
            {
                Presence = presence,
                AccentColor = presence.AccentColor,
                Subtitle = string.IsNullOrWhiteSpace(presence.StatusText) ? presence.Status : presence.StatusText
            };
            users.Add(candidate);
        }

        users.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));

        var results = new List<MentionCandidate>();
        foreach (var candidate in users)
        {
            results.Add(candidate);
            if (results.Count >= MentionResultLimit)
            {
                return results;
            }
        }

        if (_config.MentionRoleIds.Count > 0 && results.Count < MentionResultLimit)
        {
            var allowed = new HashSet<string>(_config.MentionRoleIds);
            var roles = new List<MentionCandidate>();
            foreach (var role in RoleCache.Roles)
            {
                if (!allowed.Contains(role.Id) || string.IsNullOrWhiteSpace(role.Name))
                    continue;

                if (!IsMentionMatch(role.Name, filter))
                    continue;

                roles.Add(new MentionCandidate(MentionCandidateType.Role, role.Id, role.Name) { Role = role });
            }

            roles.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));

            foreach (var candidate in roles)
            {
                results.Add(candidate);
                if (results.Count >= MentionResultLimit)
                {
                    break;
                }
            }
        }

        return results;
    }

    private static bool IsMentionMatch(string name, string query)
    {
        if (string.IsNullOrEmpty(query))
        {
            return true;
        }

        return name.Contains(query, StringComparison.OrdinalIgnoreCase);
    }

    private static MentionToken? FindMentionToken(string text, int caret)
    {
        if (string.IsNullOrEmpty(text) || caret <= 0 || caret > text.Length)
        {
            return null;
        }

        var index = caret - 1;
        while (index >= 0)
        {
            var c = text[index];
            if (c == '@')
            {
                if (index > 0 && !IsMentionBoundary(text[index - 1]))
                {
                    return null;
                }

                var query = text.Substring(index + 1, caret - (index + 1));
                return new MentionToken(index, caret, query);
            }

            if (char.IsWhiteSpace(c) || c == '\r' || c == '\n')
            {
                break;
            }

            index--;
        }

        return null;
    }

    private static bool IsMentionBoundary(char c)
        => char.IsWhiteSpace(c) ||
           c is '(' or '[' or '{' or ')' or ']' or '}' or ',' or '.' or '!' or '?' or ':' or ';' or '"' or '\'' or '/' or '\\' or '>';

    private readonly struct MentionToken
    {
        public MentionToken(int start, int end, string query)
        {
            Start = start;
            End = end;
            Query = query;
        }

        public int Start { get; }
        public int End { get; }
        public string Query { get; }
    }

    private void WrapSelection(string prefix, string suffix)
    {
        var input = _input;
        MarkdownSelectionHelper.WrapSelection(ref input, prefix, suffix, ref _selectionStart, ref _selectionEnd);
        _input = input ?? string.Empty;
    }

    private void InsertTextAtSelection(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return;
        }

        var value = _input ?? string.Empty;
        var start = Math.Min(_selectionStart, _selectionEnd);
        var end = Math.Max(_selectionStart, _selectionEnd);
        start = Math.Clamp(start, 0, value.Length);
        end = Math.Clamp(end, 0, value.Length);

        var builder = new StringBuilder(value.Length + text.Length);
        if (start > 0)
        {
            builder.Append(value.AsSpan(0, start));
        }

        builder.Append(text);

        if (end < value.Length)
        {
            builder.Append(value.AsSpan(end));
        }

        _input = builder.ToString();
        var caret = start + text.Length;
        _selectionStart = _selectionEnd = caret;
        _focusComposerNextFrame = true;
    }

    private void DrawAttachmentChips()
    {
        var removed = new List<int>();

        for (var i = 0; i < _attachments.Count; i++)
        {
            var path = _attachments[i];
            ImGui.PushID(i);
            var shouldRemove = DrawAttachmentChip(path);
            ImGui.PopID();

            if (shouldRemove)
            {
                removed.Add(i);
            }

            if (i < _attachments.Count - 1)
            {
                ImGui.SameLine();
            }
        }

        if (removed.Count == 0)
        {
            return;
        }

        for (var i = removed.Count - 1; i >= 0; i--)
        {
            _attachments.RemoveAt(removed[i]);
        }

        InvalidatePreview();
        CleanupAttachmentPreviews(_attachments);
    }

    private bool DrawAttachmentChip(string path)
    {
        var style = ImGui.GetStyle();
        var scale = ImGuiHelpers.GlobalScale;
        var fileName = Path.GetFileName(path);
        var isImage = IsImageAttachment(path);

        ISharedImmediateTexture? previewTexture = null;
        var previewReady = false;
        var previewLoading = false;

        if (isImage)
        {
            EnsureAttachmentPreview(path);
            if (_attachmentPreviewTextures.TryGetValue(path, out var existingTexture))
            {
                previewTexture = existingTexture;
                previewReady = existingTexture != null;
                previewLoading = existingTexture == null;
            }
            else
            {
                previewLoading = true;
            }
        }

        var errorForAttachment =
            !string.IsNullOrEmpty(_attachmentError) &&
            !string.IsNullOrEmpty(fileName) &&
            _attachmentError.IndexOf(fileName, StringComparison.OrdinalIgnoreCase) >= 0;

        var padding = new Vector2(8f, 6f) * scale;
        var remove = false;
        ImGui.PushStyleVar(ImGuiStyleVar.ChildBorderSize, 1f);
        ImGui.PushStyleVar(ImGuiStyleVar.ChildRounding, style.FrameRounding);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, padding);
        ImGui.PushStyleColor(ImGuiCol.ChildBg, style.Colors[(int)ImGuiCol.FrameBg]);
        var childFlags =
            ImGuiWindowFlags.AlwaysAutoResize |
            ImGuiWindowFlags.NoScrollbar |
            ImGuiWindowFlags.NoScrollWithMouse;
        if (ImGui.BeginChild("##attachmentChip", new Vector2(0, 0), ImGuiChildFlags.Border, childFlags))
        {
            ImGui.PushStyleVar(ImGuiStyleVar.ItemSpacing, new Vector2(6f * scale, style.ItemSpacing.Y));

            var renderedPreview = false;
            if (previewReady && previewTexture != null)
            {
                var wrap = previewTexture.GetWrapOrEmpty();
                var handle = wrap.ToImGuiHandle();
                if (handle != 0 && wrap.Width > 0 && wrap.Height > 0)
                {
                    var maxThumbnail = new Vector2(40f, 40f) * scale;
                    var bounds = GetAttachmentBounds(maxThumbnail, allowAutoStretch: false);
                    var size = CalculateAttachmentDisplaySize(new Vector2(wrap.Width, wrap.Height), bounds, allowAutoStretch: false);
                    ImGui.Image(handle, size);
                    renderedPreview = true;
                }
            }

            if (!renderedPreview)
            {
                using var emojiFont = _emojiManager.PushEmojiFont();
                var icon = isImage ? "🖼️" : "📎";
                ImGui.TextUnformatted(icon);
            }

            ImGui.SameLine();
            ImGui.TextUnformatted(fileName);

            if (errorForAttachment)
            {
                ImGui.SameLine();
                ImGui.PushStyleColor(ImGuiCol.Text, new Vector4(1f, 0.4f, 0.4f, 1f));
                ImGui.TextUnformatted("!");
                ImGui.PopStyleColor();
            }

            ImGui.SameLine();
            if (ImGui.SmallButton("×"))
            {
                remove = true;
            }

            ImGui.PopStyleVar();
        }
        ImGui.EndChild();
        ImGui.PopStyleColor();
        ImGui.PopStyleVar(3);

        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenBlockedByPopup))
        {
            ImGui.BeginTooltip();
            if (!string.IsNullOrEmpty(fileName))
            {
                ImGui.TextUnformatted(fileName);
                ImGui.Separator();
            }

            long fileSize = 0;
            var sizeKnown = false;
            try
            {
                var info = new FileInfo(path);
                fileSize = info.Length;
                sizeKnown = true;
            }
            catch
            {
                // ignore file IO errors when attempting to inspect file size
            }

            ImGui.TextUnformatted(sizeKnown ? $"Size: {FormatFileSize(fileSize)}" : "Size: Unknown");

            string status;
            if (errorForAttachment && !string.IsNullOrEmpty(_attachmentError))
            {
                status = _attachmentError;
            }
            else if (previewReady)
            {
                status = "Preview ready";
            }
            else if (previewLoading && isImage)
            {
                status = "Loading preview…";
            }
            else if (isImage)
            {
                status = "Preview unavailable";
            }
            else
            {
                status = "Ready to upload";
            }

            ImGui.TextUnformatted($"Status: {status}");

            if (errorForAttachment && !string.IsNullOrEmpty(_attachmentError))
            {
                ImGui.Separator();
                ImGui.TextWrapped(_attachmentError);
            }

            ImGui.EndTooltip();
        }

        return remove;
    }

    private static bool IsImageAttachment(string path)
    {
        if (string.IsNullOrEmpty(path))
        {
            return false;
        }

        var ext = Path.GetExtension(path);
        if (string.IsNullOrEmpty(ext))
        {
            return false;
        }

        switch (ext.ToLowerInvariant())
        {
            case ".png":
            case ".jpg":
            case ".jpeg":
            case ".gif":
            case ".bmp":
            case ".tga":
            case ".tif":
            case ".tiff":
            case ".webp":
            case ".heic":
            case ".heif":
            case ".avif":
                return true;
            default:
                return false;
        }
    }

    private static string FormatFileSize(long bytes)
    {
        if (bytes <= 0)
        {
            return "0 B";
        }

        string[] units = { "B", "KB", "MB", "GB", "TB", "PB" };
        double value = bytes;
        var unitIndex = 0;
        while (value >= 1024 && unitIndex < units.Length - 1)
        {
            value /= 1024;
            unitIndex++;
        }

        if (unitIndex == 0)
        {
            return string.Format(CultureInfo.InvariantCulture, "{0} {1}", bytes, units[unitIndex]);
        }

        return string.Format(CultureInfo.InvariantCulture, "{0:0.##} {1}", value, units[unitIndex]);
    }

    private void InvalidatePreview()
    {
        _previewKey = string.Empty;
        _previewMessage = BridgeMessageFormatter.BridgeFormattedMessage.Empty;
        _previewForceLoadEmbeds = false;
    }

    private void UpdatePreviewMessage()
    {
        if (string.IsNullOrEmpty(_input) && _attachments.Count == 0)
        {
            _previewMessage = BridgeMessageFormatter.BridgeFormattedMessage.Empty;
            _previewKey = string.Empty;
            CleanupAttachmentPreviews(Array.Empty<string>());
            return;
        }

        var keyBuilder = new StringBuilder();
        keyBuilder.Append(_input);
        keyBuilder.Append('|');
        keyBuilder.Append(_useCharacterName ? '1' : '0');
        foreach (var att in _attachments)
        {
            keyBuilder.Append('|');
            keyBuilder.Append(att);
        }
        var key = keyBuilder.ToString();
        if (key == _previewKey)
            return;

        var options = BuildFormatterOptions();
        _previewMessage = BridgeMessageFormatter.Format(_input, _attachments, options);
        _previewKey = key;
        CleanupAttachmentPreviews(_previewMessage.Attachments.Select(a => a.Path));
    }

    internal static string? GetPreviewPlainText(BridgeMessageFormatter.BridgeFormattedMessage message)
    {
        var displayContent = message.DisplayContent ?? string.Empty;
        var embeds = message.Embeds ?? Array.Empty<EmbedDto>();
        var hasEmbedChunks = embeds.Any(e => !string.IsNullOrEmpty(e?.Description));

        if (hasEmbedChunks)
        {
            var remainder = GetUnrepresentedPlainText(displayContent, embeds);
            return string.IsNullOrEmpty(remainder) ? null : remainder;
        }

        if (!string.IsNullOrEmpty(displayContent))
        {
            return displayContent;
        }

        if (!string.IsNullOrEmpty(message.Content))
        {
            var fallback = ReplaceMentionTokens(message.Content, message.Mentions);
            return string.IsNullOrEmpty(fallback) ? null : fallback;
        }

        return null;
    }

    private static string GetUnrepresentedPlainText(string displayContent, IReadOnlyList<EmbedDto> embeds)
    {
        if (string.IsNullOrEmpty(displayContent))
            return string.Empty;

        var index = 0;
        foreach (var embed in embeds)
        {
            var description = embed?.Description;
            if (string.IsNullOrEmpty(description))
                continue;

            var remaining = displayContent.AsSpan(index);
            var desc = description.AsSpan();

            if (remaining.Length < desc.Length || !remaining.StartsWith(desc, StringComparison.Ordinal))
                return string.Empty;

            index += desc.Length;
        }

        if (index >= displayContent.Length)
            return string.Empty;

        return displayContent[index..];
    }

    private BridgeMessageFormatter.BridgeFormattedMessage GetFormattedMessage()
    {
        var options = BuildFormatterOptions();
        return BridgeMessageFormatter.Format(_input, _attachments, options);
    }

    private BridgeMessageFormatter.BridgeFormatterOptions BuildFormatterOptions()
    {
        var presences = _presence?.Presences ?? Array.Empty<PresenceDto>();
        var player = PluginServices.Instance?.ClientState?.LocalPlayer;
        var characterName = player?.Name.TextValue ?? player?.Name.ToString();
        string? worldName = null;

        if (player != null)
        {
            var homeWorld = player.HomeWorld.ValueNullable;
            if (homeWorld.HasValue)
            {
                worldName = homeWorld.Value.Name.ToString();
            }
        }

        var authorName = MembershipCache.DisplayName;
        if (string.IsNullOrWhiteSpace(authorName))
        {
            authorName = MembershipCache.DiscordUserId;
        }
        if (string.IsNullOrWhiteSpace(authorName))
        {
            authorName = "You";
        }

        return new BridgeMessageFormatter.BridgeFormatterOptions
        {
            UseCharacterName = _useCharacterName,
            ChannelKind = _channelKind,
            Presences = presences,
            Roles = RoleCache.Roles,
            AllowedRoleIds = _config.MentionRoleIds,
            AuthorName = authorName,
            CharacterName = characterName,
            WorldName = worldName,
            EmbedColor = GetEmbedColorOverride(),
            EmbedBorder = _config.GetEmbedBorderSettingsCopy(_channelKind),
            Timestamp = DateTimeOffset.UtcNow,
            EmojiManager = _emojiManager
        };
    }

    private void RenderAttachmentPreview(BridgeMessageFormatter.BridgeFormattedAttachment attachment)
    {
        var icon = attachment.IsImage ? "🖼️" : "📎";
        using var emojiFont = _emojiManager.PushEmojiFont();
        ImGui.TextUnformatted($"{icon} {attachment.FileName}");
        if (!attachment.IsImage)
            return;

        EnsureAttachmentPreview(attachment.Path);
        if (_attachmentPreviewTextures.TryGetValue(attachment.Path, out var texture) && texture != null)
        {
            var wrap = texture.GetWrapOrEmpty();
            var handle = wrap.ToImGuiHandle();
            if (handle != 0 && wrap.Width > 0 && wrap.Height > 0)
            {
                var attachmentCap = GetConfiguredAttachmentCap();
                var bounds = GetAttachmentBounds(attachmentCap);
                var size = CalculateAttachmentDisplaySize(new Vector2(wrap.Width, wrap.Height), bounds);
                ImGui.Image(handle, size);
            }
        }
    }

    private void EnsureAttachmentPreview(string path)
    {
        if (string.IsNullOrEmpty(path))
            return;
        if (_attachmentPreviewTextures.ContainsKey(path))
            return;
        if (PluginServices.Instance?.TextureProvider == null || PluginServices.Instance?.Framework == null)
            return;

        _attachmentPreviewTextures[path] = null;
        _ = Task.Run(async () =>
        {
            try
            {
                var factory = _textureFactory;
                if (factory == null)
                {
                    var framework = PluginServices.Instance?.Framework;
                    if (framework != null)
                    {
                        _ = framework.RunOnTick(() => _attachmentPreviewTextures.Remove(path));
                    }
                    else
                    {
                        _attachmentPreviewTextures.Remove(path);
                    }
                    return;
                }

                await using var stream = File.OpenRead(path);
                using var image = await Image.LoadAsync<Rgba32>(stream).ConfigureAwait(false);
                ResizeAttachmentImage(image);
                var texture = await factory.CreateAsync(image).ConfigureAwait(false);
                if (texture == null)
                {
                    var framework = PluginServices.Instance?.Framework;
                    if (framework != null)
                    {
                        _ = framework.RunOnTick(() => _attachmentPreviewTextures.Remove(path));
                    }
                    else
                    {
                        _attachmentPreviewTextures.Remove(path);
                    }
                    return;
                }

                var frameworkInstance = PluginServices.Instance?.Framework;
                if (frameworkInstance != null)
                {
                    _ = frameworkInstance.RunOnTick(() => _attachmentPreviewTextures[path] = texture);
                }
                else
                {
                    _attachmentPreviewTextures[path] = texture;
                }
            }
            catch
            {
                var framework = PluginServices.Instance?.Framework;
                if (framework != null)
                {
                    _ = framework.RunOnTick(() => _attachmentPreviewTextures.Remove(path));
                }
                else
                {
                    _attachmentPreviewTextures.Remove(path);
                }
            }
        });
    }

    private void ResizeAttachmentImage(Image<Rgba32> image)
    {
        if (image.Width <= _imageMaxDecodeWidth && image.Height <= _imageMaxDecodeHeight)
        {
            return;
        }

        image.Mutate(ctx => ctx.Resize(new ResizeOptions
        {
            Mode = ResizeMode.Max,
            Size = new Size(_imageMaxDecodeWidth, _imageMaxDecodeHeight),
            Sampler = KnownResamplers.Lanczos3,
            Compand = true
        }));
    }

    private void CleanupAttachmentPreviews(IEnumerable<string> activePaths)
    {
        var active = new HashSet<string>(activePaths ?? Array.Empty<string>(), StringComparer.OrdinalIgnoreCase);
        foreach (var kvp in _attachmentPreviewTextures.ToList())
        {
            if (!active.Contains(kvp.Key))
            {
                if (kvp.Value?.GetWrapOrEmpty() is IDisposable wrap)
                    wrap.Dispose();
                _attachmentPreviewTextures.Remove(kvp.Key);
            }
        }
    }

    protected virtual async Task SendMessage()
    {
        if (!ApiHelpers.ValidateApiBaseUrl(_config))
        {
            PluginServices.Instance!.Log.Warning("Cannot send message: API base URL is not configured.");
            _ = PluginServices.Instance!.Framework.RunOnTick(() => _statusMessage = "Invalid API URL");
            return;
        }
        var channelId = CurrentChannelId;
        if (string.IsNullOrWhiteSpace(channelId) || string.IsNullOrWhiteSpace(_input))
        {
            return;
        }

        if (!EnsureChannelAvailable(channelId, "send message"))
        {
            return;
        }

        var formatted = GetFormattedMessage();
        if (formatted.Errors.Count > 0)
        {
            var message = formatted.Errors[0];
            _ = PluginServices.Instance!.Framework.RunOnTick(() =>
            {
                _statusMessage = message;
                _lastError = message;
            });
            return;
        }

        string logContent = formatted.Content;
        try
        {
            var content = formatted.Content;
            logContent = content;

            HttpRequestMessage request;
            if (_attachments.Count > 0)
            {
                request = await BuildMultipartRequest(channelId, content);
            }
            else
            {
                var messageReference = new MessageBuilder()
                    .WithMessageReference(_replyToId, channelId)
                    .BuildMessageReference();
                request = BuildTextMessageRequest(channelId, content, messageReference);
            }
            ApiHelpers.AddAuthHeader(request, _tokenManager);
            PluginServices.Instance!.Log.Information($"Sending message to channel {channelId}");
            var response = await _httpClient.SendAsync(request);
            if (response.StatusCode == HttpStatusCode.TooManyRequests)
            {
                var retry = response.Headers.RetryAfter?.Delta?.TotalSeconds;
                var msg = retry.HasValue
                    ? $"Rate limited. Try again in {retry.Value:F0}s"
                    : "Rate limited. Please try again later";
                _ = PluginServices.Instance!.Framework.RunOnTick(() => _statusMessage = msg);
                return;
            }
            if (response.IsSuccessStatusCode)
            {
                string? id = null;
                try
                {
                    var bodyText = await response.Content.ReadAsStringAsync();
                    using var doc = JsonDocument.Parse(bodyText);
                    if (doc.RootElement.TryGetProperty("id", out var idProp))
                        id = idProp.GetString();
                }
                catch
                {
                    // ignore parse errors
                }

                var text = formatted.DisplayContent;
                var optimistic = new DiscordMessageDto
                {
                    Id = id ?? Guid.NewGuid().ToString(),
                    ChannelId = channelId,
                    Content = text,
                    Author = new DiscordUserDto { Name = "You" },
                    Timestamp = DateTime.UtcNow,
                    Embeds = formatted.Embeds.Select(e => e).ToList()
                };

                _ = PluginServices.Instance!.Framework.RunOnTick(() =>
                {
                    _messages.Add(optimistic);
                    _input = string.Empty;
                    _statusMessage = string.Empty;
                    _lastError = string.Empty;
                    _replyToId = null;
                    _attachments.Clear();
                    InvalidatePreview();
                    CleanupAttachmentPreviews(Array.Empty<string>());
                });
            }
            else
            {
                var responseBody = await response.Content.ReadAsStringAsync();
                PluginServices.Instance!.Log.Warning($"Failed to send message (channel {channelId}, content '{Truncate(logContent)}'). Status: {response.StatusCode}. Response Body: {responseBody}");
                var msg = $"{(int)response.StatusCode} {response.ReasonPhrase}";
                string detailText = string.Empty;
                string? detailCode = null;
                try
                {
                    using var doc = JsonDocument.Parse(responseBody);
                    if (doc.RootElement.TryGetProperty("detail", out var detail))
                    {
                        if (detail.ValueKind == JsonValueKind.String)
                        {
                            detailText = detail.GetString() ?? string.Empty;
                        }
                        else if (detail.ValueKind == JsonValueKind.Object)
                        {
                            List<string> parts = new();
                            if (detail.TryGetProperty("code", out var codeProp) && codeProp.ValueKind == JsonValueKind.String)
                            {
                                detailCode = codeProp.GetString();
                            }
                            if (detail.TryGetProperty("discord", out var discord) && discord.ValueKind == JsonValueKind.Array)
                            {
                                parts = discord.EnumerateArray()
                                    .Select(e => e.GetString())
                                    .Where(s => !string.IsNullOrEmpty(s))
                                    .Select(s => s!)
                                    .ToList();
                            }
                            if (parts.Count > 0)
                            {
                                detailText = string.Join("\n", parts);
                            }
                            else if (detail.TryGetProperty("message", out var m))
                            {
                                detailText = m.GetString() ?? string.Empty;
                            }
                        }
                    }
                }
                catch
                {
                    // ignore parse errors
                }
                if (!string.IsNullOrEmpty(detailText))
                {
                    msg = detailText;
                }
                _ = PluginServices.Instance!.Framework.RunOnTick(() =>
                {
                    _statusMessage = msg;
                    _lastError = msg;
                });
                if (response.StatusCode == HttpStatusCode.Conflict
                    && string.Equals(MessagesPath, "/api/officer-messages", StringComparison.OrdinalIgnoreCase)
                    && string.Equals(detailCode, "OFFICER_CHANNEL_UNRESOLVED", StringComparison.OrdinalIgnoreCase))
                {
                    _ = PluginServices.Instance!.Framework.RunOnTick(() =>
                        PluginServices.Instance?.ToastGui.ShowError("Officer channel invalid or missing. Re-select a channel and ensure the bot has Manage Webhooks."));
                }
                var lower = detailText.ToLowerInvariant();
                if (lower.Contains("channel not configured") || lower.Contains("unsupported channel type") || response.StatusCode == HttpStatusCode.NotFound)
                {
                    _ = PluginServices.Instance!.Framework.RunOnTick(async () => await RefreshChannels());
                }
            }
        }
        catch (Exception ex)
        {
            PluginServices.Instance!.Log.Error(ex, $"Error sending message (channel {channelId}, content '{Truncate(logContent)}')");
            _ = PluginServices.Instance!.Framework.RunOnTick(() =>
            {
                _statusMessage = "Failed to send message";
                _lastError = "Failed to send message";
            });
        }
    }

    protected virtual HttpRequestMessage BuildTextMessageRequest(string channelId, string content, object? messageReference)
    {
        var url = $"{_config.ApiBaseUrl.TrimEnd('/')}/api/channels/{channelId}/messages";
        var fields = new List<KeyValuePair<string, string>>
        {
            new("content", content),
            new("useCharacterName", _useCharacterName ? "true" : "false")
        };
        if (SupportsEmbedColorSelection())
        {
            var color = GetEffectiveEmbedColor();
            fields.Add(new KeyValuePair<string, string>("embedColor", color.ToString()));
            fields.Add(new KeyValuePair<string, string>("embedBorder", SerializeBorderSettingsForPayload()));
        }
        if (messageReference != null)
        {
            var referenceJson = JsonSerializer.Serialize(messageReference);
            fields.Add(new("message_reference", referenceJson));
        }

        var request = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new FormUrlEncodedContent(fields)
        };
        return request;
    }

    protected virtual async Task<HttpRequestMessage> BuildMultipartRequest(string channelId, string content)
    {
        var url = $"{_config.ApiBaseUrl.TrimEnd('/')}/api/channels/{channelId}/messages";
        var form = new MultipartFormDataContent();
        form.Add(new StringContent(content), "content");
        form.Add(new StringContent(_useCharacterName ? "true" : "false"), "useCharacterName");
        if (SupportsEmbedColorSelection())
        {
            var color = GetEffectiveEmbedColor().ToString();
            form.Add(new StringContent(color), "embedColor");
            form.Add(new StringContent(SerializeBorderSettingsForPayload(), Encoding.UTF8), "embedBorder");
        }
        if (!string.IsNullOrEmpty(_replyToId))
        {
            var reference = new MessageBuilder()
                .WithMessageReference(_replyToId, channelId)
                .BuildMessageReference();
            if (reference != null)
            {
                var refJson = JsonSerializer.Serialize(reference);
                form.Add(new StringContent(refJson, Encoding.UTF8), "message_reference");
            }
        }
        foreach (var path in _attachments)
        {
            try
            {
                var bytes = await File.ReadAllBytesAsync(path);
                var contentPart = new ByteArrayContent(bytes);
                contentPart.Headers.ContentType = MediaTypeHeaderValue.Parse("application/octet-stream");
                form.Add(contentPart, "files", Path.GetFileName(path));
            }
            catch
            {
                // ignore individual file errors
            }
        }
        return new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = form
        };
    }

    private static string Truncate(string? value, int max = 100)
    {
        if (string.IsNullOrEmpty(value)) return string.Empty;
        return value.Length <= max ? value : value[..max] + "...";
    }

    protected virtual async Task React(string messageId, string emoji, bool remove)
    {
        if (!ApiHelpers.ValidateApiBaseUrl(_config))
        {
            PluginServices.Instance!.Log.Warning("Cannot react: API base URL is not configured.");
            return;
        }
        var channelId = CurrentChannelId;
        if (string.IsNullOrWhiteSpace(channelId) || string.IsNullOrWhiteSpace(messageId) || string.IsNullOrWhiteSpace(emoji))
        {
            return;
        }

        if (!EnsureChannelAvailable(channelId, remove ? "remove reaction" : "react to message"))
        {
            return;
        }

        try
        {
            var method = remove ? HttpMethod.Delete : HttpMethod.Put;
            var url = $"{_config.ApiBaseUrl.TrimEnd('/')}/api/channels/{channelId}/messages/{messageId}/reactions/{Uri.EscapeDataString(emoji)}";
            var request = new HttpRequestMessage(method, url);
            ApiHelpers.AddAuthHeader(request, _tokenManager);
            var response = await _httpClient.SendAsync(request);
            if (!response.IsSuccessStatusCode)
            {
                var responseBody = await response.Content.ReadAsStringAsync();
                PluginServices.Instance!.Log.Warning($"Failed to react. Status: {response.StatusCode}. Response Body: {responseBody}");
            }
        }
        catch (Exception ex)
        {
            PluginServices.Instance!.Log.Error(ex, "Error reacting to message");
        }
    }

    protected object CreateEditMessageBody(string content)
    {
        object? borderPayload = null;
        if (_editEmbedBorder != null)
        {
            borderPayload = new
            {
                enabled = _editEmbedBorder.Enabled,
                glyph = _editEmbedBorder.Glyph,
                color = _editEmbedBorder.Color
            };
        }

        return new
        {
            content,
            embedColor = _editEmbedColor,
            embedBorder = borderPayload
        };
    }

    protected virtual async Task EditMessage(string messageId, string channelId, string content)
    {
        if (!ApiHelpers.ValidateApiBaseUrl(_config))
        {
            PluginServices.Instance!.Log.Warning("Cannot edit: API base URL is not configured.");
            return;
        }
        if (string.IsNullOrWhiteSpace(channelId) || string.IsNullOrWhiteSpace(messageId) || string.IsNullOrWhiteSpace(content))
        {
            return;
        }

        try
        {
            var body = CreateEditMessageBody(content);
            var url = $"{_config.ApiBaseUrl.TrimEnd('/')}/api/channels/{channelId}/messages/{messageId}";
            var request = new HttpRequestMessage(HttpMethod.Patch, url);
            request.Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
            ApiHelpers.AddAuthHeader(request, _tokenManager);
            var response = await _httpClient.SendAsync(request);
            if (!response.IsSuccessStatusCode)
            {
                var responseBody = await response.Content.ReadAsStringAsync();
                PluginServices.Instance!.Log.Warning($"Failed to edit message. Status: {response.StatusCode}. Response Body: {responseBody}");
            }
        }
        catch (Exception ex)
        {
            PluginServices.Instance!.Log.Error(ex, "Error editing message");
        }
    }

    protected virtual async Task DeleteMessage(string messageId, string channelId)
    {
        if (!ApiHelpers.ValidateApiBaseUrl(_config))
        {
            PluginServices.Instance!.Log.Warning("Cannot delete: API base URL is not configured.");
            return;
        }
        if (string.IsNullOrWhiteSpace(channelId) || string.IsNullOrWhiteSpace(messageId))
        {
            return;
        }

        try
        {
            var url = $"{_config.ApiBaseUrl.TrimEnd('/')}/api/channels/{channelId}/messages/{messageId}";
            var request = new HttpRequestMessage(HttpMethod.Delete, url);
            ApiHelpers.AddAuthHeader(request, _tokenManager);
            var response = await _httpClient.SendAsync(request);
            if (!response.IsSuccessStatusCode)
            {
                var responseBody = await response.Content.ReadAsStringAsync();
                PluginServices.Instance!.Log.Warning($"Failed to delete message. Status: {response.StatusCode}. Response Body: {responseBody}");
            }
        }
        catch (Exception ex)
        {
            PluginServices.Instance!.Log.Error(ex, "Error deleting message");
        }
    }

    protected virtual async Task Interact(string messageId, string channelId, string customId)
    {
        if (!ApiHelpers.ValidateApiBaseUrl(_config))
        {
            PluginServices.Instance!.Log.Warning("Cannot interact: API base URL is not configured.");
            return;
        }
        if (string.IsNullOrWhiteSpace(messageId) || string.IsNullOrWhiteSpace(customId))
        {
            return;
        }

        try
        {
            long? cid = long.TryParse(channelId, out var parsed) ? parsed : null;
            var body = new { messageId = messageId, channelId = cid, customId = customId };
            var request = new HttpRequestMessage(HttpMethod.Post, $"{_config.ApiBaseUrl.TrimEnd('/')}/api/interactions");
            request.Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
            ApiHelpers.AddAuthHeader(request, _tokenManager);
            var response = await _httpClient.SendAsync(request);
            if (!response.IsSuccessStatusCode)
            {
                var responseBody = await response.Content.ReadAsStringAsync();
                PluginServices.Instance!.Log.Warning($"Failed to send interaction. Status: {response.StatusCode}. Response Body: {responseBody}");
            }

        }
        catch (Exception ex)
        {
            PluginServices.Instance!.Log.Error(ex, "Error sending interaction");
        }
    }

    public Task RequestRefreshMessagesAsync()
        => RefreshMessages();

    public virtual async Task RefreshMessages()
    {
        if (!await _refreshGate.WaitAsync(0).ConfigureAwait(false))
        {
            _refreshQueued = true;
            return;
        }

        try
        {
            do
            {
                _refreshQueued = false;
                await RefreshMessagesCore().ConfigureAwait(false);
            }
            while (_refreshQueued);
        }
        finally
        {
            _refreshGate.Release();
        }
    }

    protected virtual async Task RefreshMessagesCore()
    {
        var channelId = CurrentChannelId;
        if (!ApiHelpers.ValidateApiBaseUrl(_config) || string.IsNullOrEmpty(channelId))
        {
            return;
        }

        try
        {
            var requestedChannelId = channelId;
            const int PageSize = 50;
            var all = new List<DiscordMessageDto>();
            string? before = null;
            var cursorKey = ChannelKeyHelper.BuildCursorKey(_config.GuildId, _channelKind, channelId);
            string? storedAfter = null;
            if (_config.RestChatCursors.TryGetValue(cursorKey, out var since))
            {
                storedAfter = since.ToString(CultureInfo.InvariantCulture);
            }

            var messageState = storedAfter != null
                ? await GetExistingMessageStateAsync(requestedChannelId).ConfigureAwait(false)
                : (hadExistingMessages: false, hasMatchingChannel: false, hasConflictingChannel: false);
            var useAfterForInitialRequest = storedAfter != null &&
                messageState.hadExistingMessages &&
                messageState.hasMatchingChannel &&
                !messageState.hasConflictingChannel;

            var appliedStoredCursor = false;
            while (all.Count < MaxMessages)
            {
                var url = $"{_config.ApiBaseUrl.TrimEnd('/')}{MessagesPath}/{channelId}?limit={PageSize}";
                if (before != null)
                {
                    url += $"&before={before}";
                }
                // Use the stored "after" cursor for true incremental refreshes, and retain it for
                // subsequent pagination requests so we don't lose the existing boundary.
                var shouldApplyAfter = false;
                if (before == null)
                {
                    shouldApplyAfter = useAfterForInitialRequest;
                }
                else if (storedAfter != null)
                {
                    shouldApplyAfter = true;
                }

                if (shouldApplyAfter && storedAfter != null)
                {
                    url += $"&after={storedAfter}";
                    appliedStoredCursor = true;
                }

                using var request = new HttpRequestMessage(HttpMethod.Get, url);
                ApiHelpers.AddAuthHeader(request, _tokenManager);

                using var response = await _httpClient.SendAsync(
                    request,
                    HttpCompletionOption.ResponseHeadersRead
                );
                if (!response.IsSuccessStatusCode)
                {
                    var responseBody = await response.Content.ReadAsStringAsync();
                    PluginServices.Instance!.Log.Warning($"Failed to refresh messages. Status: {response.StatusCode}. Response Body: {responseBody}");
                    if (response.StatusCode == HttpStatusCode.Forbidden)
                    {
                        _ = PluginServices.Instance!.Framework.RunOnTick(() =>
                            _statusMessage = "Forbidden – check API key/roles");
                    }
                    break;
                }

                await using var stream = await response.Content.ReadAsStreamAsync();

                var msgs = await JsonSerializer.DeserializeAsync<List<DiscordMessageDto>>(stream, JsonOpts) ?? new List<DiscordMessageDto>();

                if (msgs.Count == 0)
                {
                    break;
                }

                var oldestMessageId = msgs[0].Id;
                if (all.Count == 0)
                {
                    all.AddRange(msgs);
                }
                else
                {
                    all.InsertRange(0, msgs);
                }
                if (all.Count >= MaxMessages || msgs.Count < PageSize)
                {
                    break;
                }

                if (string.IsNullOrEmpty(oldestMessageId))
                {
                    break;
                }

                before = oldestMessageId;
            }

            if (all.Count > MaxMessages)
            {
                all = all.Skip(all.Count - MaxMessages).ToList();
            }

            _ = PluginServices.Instance!.Framework.RunOnTick(() =>
            {
                if (!string.Equals(requestedChannelId, CurrentChannelId, StringComparison.Ordinal))
                {
                    return;
                }

                var hadExistingMessages = _messages.Count > 0;
                var hasMatchingChannel = _messages.Any(m => string.Equals(m.ChannelId, requestedChannelId, StringComparison.Ordinal));
                var hasConflictingChannel = _messages.Any(m => !string.IsNullOrEmpty(m.ChannelId) && !string.Equals(m.ChannelId, requestedChannelId, StringComparison.Ordinal));
                var canMergeIncremental = appliedStoredCursor && hadExistingMessages && hasMatchingChannel && !hasConflictingChannel;

                if (canMergeIncremental)
                {
                    if (all.Count == 0)
                    {
                        return;
                    }

                    var existingIndices = new Dictionary<string, int>(StringComparer.Ordinal);
                    for (var i = 0; i < _messages.Count; i++)
                    {
                        var id = _messages[i].Id;
                        if (!string.IsNullOrEmpty(id) && !existingIndices.ContainsKey(id))
                        {
                            existingIndices[id] = i;
                        }
                    }

                    foreach (var message in all)
                    {
                        if (string.IsNullOrEmpty(message.Id))
                        {
                            continue;
                        }

                        if (existingIndices.TryGetValue(message.Id, out var index))
                        {
                            DisposeMessageTextures(_messages[index]);
                            _messages[index] = message;
                        }
                        else
                        {
                            _messages.Add(message);
                            existingIndices[message.Id] = _messages.Count - 1;
                        }
                    }

                    TrimMessages();
                    return;
                }

                foreach (var message in _messages)
                {
                    DisposeMessageTextures(message);
                }

                _messages.Clear();
                _messageHoverStates.Clear();
                _messageRectCache.Clear();
                ClearTextureCache();

                if (all.Count > 0)
                {
                    var seenIds = new HashSet<string>(StringComparer.Ordinal);
                    foreach (var m in all)
                    {
                        if (!string.IsNullOrEmpty(m.Id) && !seenIds.Add(m.Id))
                        {
                            continue;
                        }

                        _messages.Add(m);
                    }
                }
                TrimMessages();
                _pendingInitialScroll = true;
                _wasAtBottomLastFrame = true;
                _chatTopPadding = 0f;
            });
        }
        catch (Exception ex)
        {
            PluginServices.Instance!.Log.Error(ex, "Error refreshing messages");
        }
    }

    private void TrimMessages()
    {
        while (_messages.Count > MaxMessages)
        {
            DisposeMessageTextures(_messages[0]);
            _messages.RemoveAt(0);
        }
        UpdateRestCursorBounds();
    }

    private async Task<(bool hadExistingMessages, bool hasMatchingChannel, bool hasConflictingChannel)> GetExistingMessageStateAsync(string requestedChannelId)
    {
        var services = PluginServices.Instance;
        var framework = services?.Framework;
        if (framework != null)
        {
            var tcs = new TaskCompletionSource<(bool, bool, bool)>(TaskCreationOptions.RunContinuationsAsynchronously);
            _ = framework.RunOnTick(() =>
            {
                try
                {
                    var hadExisting = _messages.Count > 0;
                    var hasMatching = _messages.Any(m => string.Equals(m.ChannelId, requestedChannelId, StringComparison.Ordinal));
                    var hasConflicting = _messages.Any(m => !string.IsNullOrEmpty(m.ChannelId) && !string.Equals(m.ChannelId, requestedChannelId, StringComparison.Ordinal));
                    tcs.TrySetResult((hadExisting, hasMatching, hasConflicting));
                }
                catch (Exception ex)
                {
                    tcs.TrySetException(ex);
                }
            });
            return await tcs.Task.ConfigureAwait(false);
        }

        var hadExistingFallback = _messages.Count > 0;
        var hasMatchingFallback = _messages.Any(m => string.Equals(m.ChannelId, requestedChannelId, StringComparison.Ordinal));
        var hasConflictingFallback = _messages.Any(m => !string.IsNullOrEmpty(m.ChannelId) && !string.Equals(m.ChannelId, requestedChannelId, StringComparison.Ordinal));
        return (hadExistingFallback, hasMatchingFallback, hasConflictingFallback);
    }

    private void UpdateRestCursorBounds()
    {
        if (_messages.Count == 0)
        {
            return;
        }

        var channelId = CurrentChannelId;
        if (string.IsNullOrEmpty(channelId))
        {
            return;
        }

        var cursorKey = ChannelKeyHelper.BuildCursorKey(_config.GuildId, _channelKind, channelId);

        if (long.TryParse(_messages[^1].Id, out var latest))
        {
            _config.RestChatCursors[cursorKey] = latest;
        }

        if (long.TryParse(_messages[0].Id, out var oldest))
        {
            _config.RestChatCursors[$"{cursorKey}{OldestRestCursorSuffix}"] = oldest;
        }
    }

    private void DisposeMessageTextures(DiscordMessageDto msg)
    {
        if (!string.IsNullOrEmpty(msg.Id))
        {
            _messageRectCache.Remove(msg.Id);
        }
        msg.AvatarTexture = null;
        if (msg.Attachments != null)
        {
            foreach (var a in msg.Attachments)
            {
                if (a.Texture?.GetWrapOrEmpty() is IDisposable wrapAtt)
                {
                    wrapAtt.Dispose();
                    a.Texture = null;
                }
            }
        }
    }

    public void ClearTextureCache()
    {
        foreach (var entry in _textureCache.Values)
        {
            var texture = entry.Texture;
            if (texture == null)
            {
                continue;
            }

            ReleaseTextureReferences(texture);
            entry.Texture = null;

            if (texture.GetWrapOrEmpty() is IDisposable wrap)
            {
                wrap.Dispose();
            }
        }
        _textureCache.Clear();
        _textureLru.Clear();
        foreach (var e in _emojiCatalog.Values)
        {
            e.Texture = null;
        }
        foreach (var tex in _attachmentPreviewTextures.Values)
        {
            if (tex?.GetWrapOrEmpty() is IDisposable wrap)
                wrap.Dispose();
        }
        _attachmentPreviewTextures.Clear();
        InvalidatePreview();
        EmbedPreviewRenderer.ClearCache();
        EmbedRenderer.ClearCache();
    }

    public void Dispose()
    {
        StopNetworking();
        _imageCts.Cancel();
        _imageCts.Dispose();
        _imageCts = new CancellationTokenSource();
        _channelSelection.ChannelChanged -= HandleChannelSelectionChanged;
        _bridge.Dispose();
        foreach (var message in _messages)
        {
            DisposeMessageTextures(message);
        }
        ClearTextureCache();
        DisposeImageLoader();
    }

    protected void SaveConfig()
    {
        var services = PluginServices.Instance;
        var pluginInterface = services?.PluginInterface;
        if (pluginInterface == null)
        {
            return;
        }

        var framework = services?.Framework;
        if (framework != null)
        {
            framework.RunOnTick(() => pluginInterface.SavePluginConfig(_config));
        }
        else
        {
            pluginInterface.SavePluginConfig(_config);
        }
    }

    public Task RefreshChannels()
    {
        if (!_tokenManager.IsReady())
        {
            return Task.CompletedTask;
        }
        _channelsLoaded = false;
        return FetchChannels();
    }

    internal virtual Task ApplyChannelRefreshResult(ChannelRefreshResult result)
    {
        switch (result.Error)
        {
            case ChannelRefreshError.None:
            case ChannelRefreshError.FeatureDisabled:
            {
                var channels = result.Channels ?? Array.Empty<ChannelDto>();
                return PluginServices.Instance!.Framework.RunOnTick(() =>
                {
                    SetChannels(channels);
                    _channelsLoaded = true;
                    _channelsLoading = false;
                    _channelFetchFailed = false;
                    _channelErrorMessage = string.Empty;
                });
            }
            case ChannelRefreshError.TokenMissing:
            {
                return PluginServices.Instance!.Framework.RunOnTick(() =>
                {
                    _channelsLoaded = false;
                    _channelsLoading = false;
                    _channelFetchFailed = false;
                    _channelErrorMessage = string.Empty;
                    _channels.Clear();
                    UpdateChannelDisplayNames();
                });
            }
            case ChannelRefreshError.InvalidApiUrl:
            {
                return PluginServices.Instance!.Framework.RunOnTick(() =>
                {
                    _channelFetchFailed = true;
                    _channelErrorMessage = "Invalid API URL";
                    _channelsLoaded = true;
                    _channelsLoading = false;
                });
            }
            case ChannelRefreshError.Unauthorized:
            {
                return PluginServices.Instance!.Framework.RunOnTick(() =>
                {
                    _channelFetchFailed = true;
                    _channelErrorMessage = "Authentication failed";
                    _channelsLoaded = true;
                    _channelsLoading = false;
                });
            }
            case ChannelRefreshError.Forbidden:
            {
                return PluginServices.Instance!.Framework.RunOnTick(() =>
                {
                    _channelFetchFailed = true;
                    _channelErrorMessage = "Forbidden – check API key/roles";
                    _channelsLoaded = true;
                    _channelsLoading = false;
                });
            }
            case ChannelRefreshError.Generic:
            {
                return PluginServices.Instance!.Framework.RunOnTick(() =>
                {
                    _channelFetchFailed = true;
                    _channelErrorMessage = "Failed to load channels";
                    _channelsLoaded = true;
                    _channelsLoading = false;
                });
            }
            default:
                return Task.CompletedTask;
        }
    }

    protected virtual async Task FetchChannels(bool refreshed = false)
    {
        if (_channelsLoading && !refreshed)
        {
            return;
        }

        if (!_tokenManager.IsReady())
        {
            _channelsLoaded = true;
            _channelsLoading = false;
            return;
        }

        if (!ApiHelpers.ValidateApiBaseUrl(_config))
        {
            PluginServices.Instance!.Log.Warning("Cannot fetch channels: API base URL is not configured.");
            _ = PluginServices.Instance!.Framework.RunOnTick(() =>
            {
                _channelFetchFailed = true;
                _channelErrorMessage = "Invalid API URL";
                _channelsLoaded = true;
                _channelsLoading = false;
            });
            return;
        }

        _channelsLoading = true;

        try
        {
            var channels = (await _channelService.FetchAsync(_channelKind, CancellationToken.None)).ToList();
            if (await ChannelNameResolver.Resolve(channels, _httpClient, _config, refreshed, () => FetchChannels(true)))
            {
                if (refreshed)
                {
                    _ = PluginServices.Instance!.Framework.RunOnTick(() =>
                    {
                        _channelFetchFailed = true;
                        _channelErrorMessage = "Failed to load channels";
                        _channelsLoaded = true;
                        _channelsLoading = false;
                    });
                }
                return;
            }
            _ = PluginServices.Instance!.Framework.RunOnTick(() =>
            {
                SetChannels(channels);
                _channelsLoaded = true;
                _channelFetchFailed = false;
                _channelErrorMessage = string.Empty;
                _channelsLoading = false;
            });
        }
        catch (HttpRequestException ex)
        {
            PluginServices.Instance!.Log.Warning($"Failed to fetch channels. Status: {ex.StatusCode}");
            _ = PluginServices.Instance!.Framework.RunOnTick(() =>
            {
                _channelFetchFailed = true;
                _channelErrorMessage = ex.StatusCode == HttpStatusCode.Unauthorized
                    ? "Authentication failed"
                    : ex.StatusCode == HttpStatusCode.Forbidden
                        ? "Forbidden – check API key/roles"
                        : "Failed to load channels";
                _channelsLoaded = true;
                _channelsLoading = false;
            });
        }
        catch (Exception ex)
        {
            PluginServices.Instance!.Log.Error(ex, "Error fetching channels");
            _ = PluginServices.Instance!.Framework.RunOnTick(() =>
            {
                _channelFetchFailed = true;
                _channelErrorMessage = "Failed to load channels";
                _channelsLoaded = true;
                _channelsLoading = false;
            });
        }
    }

    private void HandleBridgeMessage(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.TryGetProperty("deletedId", out var delProp))
            {
                var id = delProp.GetString();
                if (!string.IsNullOrEmpty(id))
                {
                    _ = PluginServices.Instance!.Framework.RunOnTick(() =>
                    {
                        var index = _messages.FindIndex(m => m.Id == id);
                        if (index >= 0)
                        {
                            DisposeMessageTextures(_messages[index]);
                            _messages.RemoveAt(index);
                            TrimMessages();
                        }
                    });
                }
            }
            else
            {
                var msg = document.RootElement.Deserialize<DiscordMessageDto>(JsonOpts);
                if (msg != null)
                {
                    var current = CurrentChannelId;
                    _ = PluginServices.Instance!.Framework.RunOnTick(() =>
                    {
                        if (msg.ChannelId == current)
                        {
                            var index = _messages.FindIndex(m => m.Id == msg.Id);
                            if (index >= 0)
                            {
                                DisposeMessageTextures(_messages[index]);
                                _messages[index] = msg;
                            }
                            else
                            {
                                _messages.Add(msg);
                            }
                            TrimMessages();
                        }
                    });
                }
            }
        }
        catch
        {
            // ignored
        }
    }

    private void HandleBridgeTyping(DiscordUserDto user)
    {
        _ = PluginServices.Instance!.Framework.RunOnTick(() =>
        {
            _typingUsers[user.Id] = new TypingUser(user.Name, DateTime.UtcNow.AddSeconds(5));
        });
    }

    private void HandleBridgeLinked()
    {
        _presence?.Reload();
        _ = _presence?.Refresh();
        TrySubscribeCurrentChannel(refreshMessages: false);
    }

    private void HandleBridgeUnlinked()
    {
        // nothing additional
    }

    protected virtual Uri BuildWebSocketUri()
    {
        var baseUri = _config.ApiBaseUrl.TrimEnd('/') + "/ws/chat";
        var builder = new UriBuilder(baseUri);
        if (builder.Scheme == "https")
        {
            builder.Scheme = "wss";
        }
        else if (builder.Scheme == "http")
        {
            builder.Scheme = "ws";
        }
        return builder.Uri;
    }

    protected void LoadTexture(string? url, Action<ISharedImmediateTexture?> set)
    {
        if (string.IsNullOrEmpty(url))
        {
            set(null);
            return;
        }

        if (_lazyLoadEmbedsEnabled && !_allowTextureLoads)
        {
            set(null);
            return;
        }

        if (_textureCache.TryGetValue(url, out var cached))
        {
            _textureLru.Remove(cached.Node);
            _textureLru.AddFirst(cached.Node);
            set(cached.Texture);
            return;
        }

        var loader = _imageLoader;
        if (loader == null || _textureFactory == null)
        {
            set(null);
            return;
        }

        var node = _textureLru.AddFirst(url);
        _textureCache[url] = new TextureCacheEntry(null, node);
        EnforceTextureCacheCapacity();

        var token = _imageCts.Token;
        if (token.IsCancellationRequested)
        {
            RemoveTextureEntry(url);
            return;
        }

        _ = Task.Run(async () =>
        {
            ISharedImmediateTexture? texture = null;
            try
            {
                texture = await loader.LoadIntoTextureAsync(url, token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                return;
            }
            catch
            {
                texture = null;
            }

            if (token.IsCancellationRequested)
            {
                if (texture?.GetWrapOrEmpty() is IDisposable wrapCanceled)
                {
                    wrapCanceled.Dispose();
                }
                RemoveTextureEntry(url);
                return;
            }

            void Complete()
            {
                if (token.IsCancellationRequested)
                {
                    if (texture?.GetWrapOrEmpty() is IDisposable wrapCanceled)
                    {
                        wrapCanceled.Dispose();
                    }
                    RemoveTextureEntry(url);
                    return;
                }

                if (texture == null)
                {
                    RemoveTextureEntry(url);
                    set(null);
                    return;
                }

                if (!_textureCache.TryGetValue(url, out var entry))
                {
                    if (texture.GetWrapOrEmpty() is IDisposable wrap)
                    {
                        wrap.Dispose();
                    }
                    set(null);
                    return;
                }

                entry.Texture = texture;
                set(texture);
            }

            var framework = PluginServices.Instance?.Framework;
            if (framework != null)
            {
                _ = framework.RunOnTick(Complete);
            }
            else
            {
                Complete();
            }
        }, token);
    }

    public virtual void ReconfigureImageLoader()
    {
        var sanitizedMaxMessages = Config.SanitizeChatMaxMessages(_config.ChatMaxMessages);
        if (_config.ChatMaxMessages != sanitizedMaxMessages)
        {
            _config.ChatMaxMessages = sanitizedMaxMessages;
        }
        TrimMessages();

        var sanitizedCache = Config.SanitizeTextureCacheCapacity(_config.TextureCacheCapacity);
        if (_config.TextureCacheCapacity != sanitizedCache)
        {
            _config.TextureCacheCapacity = sanitizedCache;
        }
        EnforceTextureCacheCapacity();

        var sanitizedDecodeWidth = Config.SanitizeImageDecodeDimension(_config.ImageMaxDecodeWidth);
        if (_config.ImageMaxDecodeWidth != sanitizedDecodeWidth)
        {
            _config.ImageMaxDecodeWidth = sanitizedDecodeWidth;
        }
        _imageMaxDecodeWidth = sanitizedDecodeWidth;

        var sanitizedDecodeHeight = Config.SanitizeImageDecodeDimension(_config.ImageMaxDecodeHeight);
        if (_config.ImageMaxDecodeHeight != sanitizedDecodeHeight)
        {
            _config.ImageMaxDecodeHeight = sanitizedDecodeHeight;
        }
        _imageMaxDecodeHeight = sanitizedDecodeHeight;

        var sanitizedDownloadConcurrency = Config.SanitizeImageDownloadConcurrency(_config.ImageDownloadConcurrency);
        if (_config.ImageDownloadConcurrency != sanitizedDownloadConcurrency)
        {
            _config.ImageDownloadConcurrency = sanitizedDownloadConcurrency;
        }

        var sanitizedDecodeConcurrency = Config.SanitizeImageDecodeConcurrency(_config.ImageDecodeConcurrency);
        if (_config.ImageDecodeConcurrency != sanitizedDecodeConcurrency)
        {
            _config.ImageDecodeConcurrency = sanitizedDecodeConcurrency;
        }

        var sanitizedBudget = Config.SanitizeImageBytesInFlightBudget(_config.ImageBytesInFlightBudget);
        if (_config.ImageBytesInFlightBudget != sanitizedBudget)
        {
            _config.ImageBytesInFlightBudget = sanitizedBudget;
        }

        var sanitizedPreload = Config.SanitizePreloadRowsAhead(_config.PreloadRowsAhead);
        if (_config.PreloadRowsAhead != sanitizedPreload)
        {
            _config.PreloadRowsAhead = sanitizedPreload;
        }
        _preloadRowsAhead = sanitizedPreload;

        _lazyLoadEmbedsEnabled = _config.LazyLoadEmbeds;

        RebuildImageLoader(sanitizedDownloadConcurrency, sanitizedDecodeConcurrency, sanitizedBudget);
    }

    private int MaxMessages => Config.SanitizeChatMaxMessages(_config.ChatMaxMessages);

    private int TextureCacheCapacity => Config.SanitizeTextureCacheCapacity(_config.TextureCacheCapacity);

    private void EnforceTextureCacheCapacity()
    {
        var capacity = TextureCacheCapacity;
        if (capacity <= 0)
        {
            return;
        }

        while (_textureCache.Count > capacity)
        {
            var last = _textureLru.Last;
            if (last == null)
            {
                break;
            }

            if (_textureCache.TryGetValue(last.Value, out var toRemove))
            {
                var texture = toRemove.Texture;
                if (texture != null)
                {
                    ReleaseTextureReferences(texture);
                    toRemove.Texture = null;
                    if (texture.GetWrapOrEmpty() is IDisposable wrap)
                    {
                        wrap.Dispose();
                    }
                }
                _textureCache.Remove(last.Value);
            }
            _textureLru.Remove(last);
        }
    }

    private void RemoveTextureEntry(string url)
    {
        if (!_textureCache.TryGetValue(url, out var entry))
        {
            return;
        }

        var texture = entry.Texture;
        if (texture != null)
        {
            ReleaseTextureReferences(texture);
            entry.Texture = null;
            if (texture.GetWrapOrEmpty() is IDisposable wrap)
            {
                wrap.Dispose();
            }
        }

        _textureCache.Remove(url);
        _textureLru.Remove(entry.Node);
    }

    private bool ShouldLoadRowTextures(string? messageId)
    {
        if (!_lazyLoadEmbedsEnabled || string.IsNullOrEmpty(messageId))
        {
            return true;
        }

        if (!_messageRectCache.TryGetValue(messageId, out var rect))
        {
            return true;
        }

        if (ImGui.IsRectVisible(rect.Min, rect.Max))
        {
            return true;
        }

        if (_preloadRowsAhead <= 0)
        {
            return false;
        }

        var windowPos = ImGui.GetWindowPos();
        var scrollY = ImGui.GetScrollY();
        var windowHeight = ImGui.GetWindowHeight();

        var visibleMin = scrollY;
        var visibleMax = scrollY + windowHeight;

        var rowMinY = rect.Min.Y - windowPos.Y + scrollY;
        var rowMaxY = rect.Max.Y - windowPos.Y + scrollY;

        float distance;
        if (rowMaxY < visibleMin)
        {
            distance = visibleMin - rowMaxY;
        }
        else if (rowMinY > visibleMax)
        {
            distance = rowMinY - visibleMax;
        }
        else
        {
            return true;
        }

        var rowHeight = Math.Max(1f, rect.Max.Y - rect.Min.Y);
        var rowsAway = distance / rowHeight;
        return rowsAway <= _preloadRowsAhead;
    }

    private void RebuildImageLoader(int downloadConcurrency, int decodeConcurrency, long byteBudget)
    {
        var services = PluginServices.Instance;
        if (services?.TextureProvider == null)
        {
            DisposeImageLoader();
            return;
        }

        var newFactory = new ImmediateTextureFactory(services.TextureProvider);
        var newLoader = new ImageLoader(
            _httpClient,
            newFactory,
            downloadConcurrency,
            decodeConcurrency,
            byteBudget,
            _imageMaxDecodeWidth,
            _imageMaxDecodeHeight);

        var oldLoader = Interlocked.Exchange(ref _imageLoader, newLoader);
        _textureFactory = newFactory;
        oldLoader?.Dispose();
    }

    private void DisposeImageLoader()
    {
        var oldLoader = Interlocked.Exchange(ref _imageLoader, null);
        oldLoader?.Dispose();
        _textureFactory = null;
    }

    private readonly struct TextureLoadScope : IDisposable
    {
        private readonly ChatWindow _owner;
        private readonly bool _previous;

        public TextureLoadScope(ChatWindow owner, bool allow)
        {
            _owner = owner;
            _previous = owner._allowTextureLoads;
            owner._allowTextureLoads = allow;
        }

        public void Dispose()
        {
            _owner._allowTextureLoads = _previous;
        }
    }
}
