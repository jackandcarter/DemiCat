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
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Textures;
using Dalamud.Interface.Utility;
using StbImageSharp;
using System.IO;
using DiscordHelper;
using System.Diagnostics;
using Dalamud.Interface.ImGuiFileDialog;
using DemiCatPlugin.Emoji;
using DemiCatPlugin.Avatars;

namespace DemiCatPlugin;

public class ChatWindow : IDisposable
{
    protected readonly Config _config;
    protected readonly HttpClient _httpClient;
    protected readonly List<DiscordMessageDto> _messages = new();
    protected readonly List<ChannelDto> _channels = new();
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
    private const int TextureCacheCapacity = 100;
    private const int MaxMessages = 100;
    private const float DefaultComposeSplitRatio = 0.35f;
    private const float MinComposeSplitRatio = 0.2f;
    private const float MaxComposeSplitRatio = 0.8f;
    private const int MinInputLines = 1;
    private const int MaxInputLines = 8;
    private readonly Dictionary<string, TextureCacheEntry> _textureCache = new();
    private readonly LinkedList<string> _textureLru = new();
    private readonly Dictionary<string, TypingUser> _typingUsers = new();
    private bool _networkingActive;
    private string? _lastSubscribedChannelId;
    private string? _lastSubscribedGuildId;
    private bool _pendingRefreshAfterSubscribe;
    private bool _pendingInitialScroll = true;
    private bool _wasAtBottomLastFrame = true;
    private float _chatTopPadding;
    private int _selectionStart;
    private int _selectionEnd;
    private bool _focusComposerNextFrame;
    private BridgeMessageFormatter.BridgeFormattedMessage _previewMessage = BridgeMessageFormatter.BridgeFormattedMessage.Empty;
    private string _previewKey = string.Empty;
    private readonly Dictionary<string, ISharedImmediateTexture?> _attachmentPreviewTextures = new(StringComparer.OrdinalIgnoreCase);
    private const string OldestRestCursorSuffix = ":oldest";

    protected string CurrentChannelId => _channelSelection.GetChannel(_channelKind, _config.GuildId);
    protected string ChannelKind => _channelKind;

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
        _emojiPicker = new EmojiPicker(_emojiManager);
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
                if (ch == CurrentChannelId) await RefreshMessages();
            });

        _channelSelection.ChannelChanged += HandleChannelSelectionChanged;
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
            ChannelKind.Chat,
            null,
            new EmojiManager(httpClient, tokenManager, config))
    {
    }
#endif

    protected virtual void OnSubscriptionStateChanged(bool isSubscribed)
    {
    }

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
                _ = PluginServices.Instance!.Framework.RunOnTick(async () => await RefreshMessages());
                _pendingRefreshAfterSubscribe = false;
            }

            OnSubscriptionStateChanged(true);
            return true;
        }

        if (!string.IsNullOrEmpty(_lastSubscribedChannelId))
        {
            _bridge.Unsubscribe(_lastSubscribedChannelId);
        }

        _bridge.Subscribe(channelId, guildId, _channelKind);

        if (_pendingRefreshAfterSubscribe)
        {
            _ = PluginServices.Instance!.Framework.RunOnTick(async () => await RefreshMessages());
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
            _pendingInitialScroll = true;
            _wasAtBottomLastFrame = true;
            _chatTopPadding = 0f;

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
            var channelNames = _channels.Select(c => c.ParentId == null ? c.Name : "  " + c.Name).ToArray();
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
                ClearTextureCache();
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
        ImGui.BeginChild("##chatScroll", new Vector2(-1, scrollRegionHeight), true);

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

        var clipper = new ImGuiListClipper();
        clipper.Begin(_messages.Count);
        while (clipper.Step())
        {
            for (int i = clipper.DisplayStart; i < clipper.DisplayEnd; i++)
            {
                var msg = _messages[i];
                ImGui.PushID(msg.Id);
                using var emojiFont = _emojiManager.PushEmojiFont();
                ImGui.BeginGroup();
                if (msg.Author != null && msg.AvatarTexture == null && _avatarCache != null)
                {
                    _ = _avatarCache.GetAsync(msg.Author.AvatarUrl, msg.Author.Id)
                        .ContinueWith(t => PluginServices.Instance!.Framework.RunOnTick(() => msg.AvatarTexture = t.Result));
                }
                if (msg.AvatarTexture != null)
                {
                    var wrap = msg.AvatarTexture.GetWrapOrEmpty();
                    ImGui.Image(wrap.Handle, new Vector2(20, 20));
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

                        if (preview.Length > 50) preview = preview.Substring(0, 50) + "...";
                        ImGui.PushStyleColor(ImGuiCol.Text, new Vector4(0.7f, 0.7f, 0.7f, 1f));
                        ImGui.TextUnformatted($"> {refMsg.Author?.Name ?? "Unknown"}: {preview}");
                        ImGui.PopStyleColor();
                    }
                }

                FormatContent(msg);
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
                    var attachmentCap = new Vector2(480f, 360f) * ImGuiHelpers.GlobalScale;
                    var availableForAttachments = MathF.Max(1f, ImGui.GetContentRegionAvail().X);
                    var maxAttachmentBounds = new Vector2(MathF.Max(1f, MathF.Min(availableForAttachments, attachmentCap.X)), MathF.Max(1f, attachmentCap.Y));

                    foreach (var att in msg.Attachments)
                    {
                        if (att.ContentType != null && att.ContentType.StartsWith("image"))
                        {
                            if (att.Texture == null)
                            {
                                LoadTexture(att.Url, t => att.Texture = t);
                            }
                            if (att.Texture != null)
                            {
                                var wrapAtt = att.Texture.GetWrapOrEmpty();
                                var originalSize = new Vector2(wrapAtt.Width, wrapAtt.Height);
                                var displaySize = CalculateAttachmentDisplaySize(originalSize, maxAttachmentBounds);
                                ImGui.Image(wrapAtt.Handle, displaySize);
                                if (ImGui.IsItemHovered())
                                {
                                    ImGui.BeginTooltip();
                                    ImGui.TextUnformatted("Open original");
                                    ImGui.EndTooltip();
                                }
                                if (ImGui.IsItemClicked())
                                {
                                    try { Process.Start(new ProcessStartInfo(att.Url) { UseShellExecute = true }); } catch { }
                                }
                            }
                        }
                        else
                        {
                            ImGui.PushStyleColor(ImGuiCol.Text, new Vector4(0.2f, 0.6f, 1f, 1f));
                            ImGui.TextUnformatted(att.Filename ?? att.Url);
                            if (ImGui.IsItemClicked())
                            {
                                try { Process.Start(new ProcessStartInfo(att.Url) { UseShellExecute = true }); } catch { }
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
                    for (int j = 0; j < msg.Reactions.Count; j++)
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
                            if (reaction.Texture != null)
                            {
                                var wrap = reaction.Texture.GetWrapOrEmpty();
                                if (ImGui.ImageButton(wrap.Handle, new Vector2(20, 20)))
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
                        if (j < msg.Reactions.Count - 1) ImGui.SameLine();
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
                        ImGui.OpenPopup("editMessage");
                    }
                    if (ImGui.MenuItem("Delete"))
                    {
                        _ = DeleteMessage(msg.Id, msg.ChannelId);
                    }
                    ImGui.EndPopup();
                }

                if (i < _messages.Count - 1)
                {
                    ImGui.Separator();
                }

                ImGui.PopID();
            }
        }
        clipper.End();

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
            var editBuf = ImGuiTextUtil.MakeUtf8Buffer(_editContent, 2048);
            ImGui.InputTextMultiline("##editContent", editBuf, new Vector2(400, ImGui.GetTextLineHeight() * 5));
            _editContent = ImGuiTextUtil.ReadUtf8Buffer(editBuf);

            if (ImGui.Button("Save"))
            {
                if (_editingMessageId != null)
                {
                    _ = EditMessage(_editingMessageId, _editingChannelId, _editContent);
                }
                _editingMessageId = null;
                ImGui.CloseCurrentPopup();
            }
            ImGui.SameLine();
            if (ImGui.Button("Cancel"))
            {
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

        if (ImGui.SmallButton("B")) WrapSelection("**", "**");
        ImGui.SameLine();
        if (ImGui.SmallButton("I")) WrapSelection("*", "*");
        ImGui.SameLine();
        if (ImGui.SmallButton("Code")) WrapSelection("`", "`");
        ImGui.SameLine();
        if (ImGui.SmallButton("Spoiler")) WrapSelection("||", "||");
        ImGui.SameLine();
        if (ImGui.SmallButton("Link")) WrapSelection("[", "](url)");

        var inputBuf = ImGuiTextUtil.MakeUtf8Buffer(_input, 2048);
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
        _ = ImGui.InputTextMultiline(
            "##chatInput",
            inputBuf,
            new Vector2(inputWidth, inputHeight),
            ImGuiInputTextFlags.EnterReturnsTrue | ImGuiInputTextFlags.CallbackAlways,
            new ImGui.ImGuiInputTextCallbackDelegate(OnInputEdited)
        );
        _input = ImGuiTextUtil.ReadUtf8Buffer(inputBuf);

        io = ImGui.GetIO();
        var send = false;
        if (ImGui.IsItemActive() && ImGui.IsKeyPressed(ImGuiKey.Enter))
        {
            if (io.KeyShift)
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
            _ = SendMessage();
        }

        if (!string.IsNullOrEmpty(_input) || _attachments.Count > 0)
        {
            UpdatePreviewMessage();

            var plainTextPreview = GetPreviewPlainText(_previewMessage);

            using (var emojiFont = _emojiManager.PushEmojiFont())
            {
                ImGui.BeginChild("##inputPreview", new Vector2(0, ImGui.GetTextLineHeight() * 6), true);
                if (!string.IsNullOrEmpty(plainTextPreview))
                {
                    ImGui.TextWrapped(plainTextPreview);
                }

                foreach (var embed in _previewMessage.Embeds)
                {
                    EmbedPreviewRenderer.Draw(embed, LoadTexture, _emojiManager);
                }

                foreach (var att in _previewMessage.Attachments)
                {
                    RenderAttachmentPreview(att);
                }

                ImGui.EndChild();
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

    private static Vector2 CalculateAttachmentDisplaySize(Vector2 originalSize, Vector2 maxSize)
    {
        var width = MathF.Max(1f, originalSize.X);
        var height = MathF.Max(1f, originalSize.Y);
        var maxWidth = MathF.Max(1f, maxSize.X);
        var maxHeight = MathF.Max(1f, maxSize.Y);

        var widthScale = maxWidth / width;
        var heightScale = maxHeight / height;
        var scale = MathF.Min(1f, MathF.Min(widthScale, heightScale));

        return new Vector2(width * scale, height * scale);
    }

    protected void FormatContent(DiscordMessageDto msg)
    {
        using var emojiFont = _emojiManager.PushEmojiFont();
        var text = msg.Content ?? string.Empty;
        text = ReplaceMentionTokens(text, msg.Mentions);
        text = MarkdownFormatter.Format(text);
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
                if (emoji.Texture != null)
                {
                    var wrap = emoji.Texture.GetWrapOrEmpty();
                    ImGui.Image(wrap.Handle, new Vector2(20, 20));
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
            ImGui.BeginChild($"codeblock{GetHashCode()}{text.GetHashCode()}", new Vector2(0, 0), true);
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

    // Correct signature for Dalamud's callback: ref struct, returns int
    private int OnInputEdited(ref ImGuiInputTextCallbackData data)
    {
        _selectionStart = data.SelectionStart;
        _selectionEnd = data.SelectionEnd;
        return 0;
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
        if (ImGui.BeginChild("##attachmentChip", new Vector2(0, 0), true, childFlags))
        {
            ImGui.PushStyleVar(ImGuiStyleVar.ItemSpacing, new Vector2(6f * scale, style.ItemSpacing.Y));

            var renderedPreview = false;
            if (previewReady && previewTexture != null)
            {
                var wrap = previewTexture.GetWrapOrEmpty();
                if (wrap.Handle != IntPtr.Zero && wrap.Width > 0 && wrap.Height > 0)
                {
                    var maxThumbnail = new Vector2(40f, 40f) * scale;
                    var size = CalculateAttachmentDisplaySize(new Vector2(wrap.Width, wrap.Height), maxThumbnail);
                    ImGui.Image(wrap.Handle, size);
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
            Timestamp = DateTimeOffset.UtcNow
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
            if (wrap.Handle != IntPtr.Zero && wrap.Width > 0 && wrap.Height > 0)
            {
                var maxWidth = Math.Max(1f, ImGui.GetContentRegionAvail().X);
                var scale = Math.Min(1f, maxWidth / wrap.Width);
                var size = new Vector2(wrap.Width * scale, wrap.Height * scale);
                ImGui.Image(wrap.Handle, size);
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
        _ = Task.Run(() =>
        {
            try
            {
                var bytes = File.ReadAllBytes(path);
                using var stream = new MemoryStream(bytes);
                var image = ImageResult.FromStream(stream, ColorComponents.RedGreenBlueAlpha);
                var wrap = PluginServices.Instance!.TextureProvider.CreateFromRaw(
                    RawImageSpecification.Rgba32(image.Width, image.Height),
                    image.Data);
                var texture = new ForwardingSharedImmediateTexture(wrap);
                PluginServices.Instance!.Framework.RunOnTick(() => _attachmentPreviewTextures[path] = texture);
            }
            catch
            {
                PluginServices.Instance?.Framework.RunOnTick(() => _attachmentPreviewTextures.Remove(path));
            }
        });
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
            var body = new { content };
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

    public virtual async Task RefreshMessages()
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

                var request = new HttpRequestMessage(HttpMethod.Get, url);
                ApiHelpers.AddAuthHeader(request, _tokenManager);

                var response = await _httpClient.SendAsync(request);
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

                var stream = await response.Content.ReadAsStreamAsync();

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
            if (entry.Texture?.GetWrapOrEmpty() is IDisposable wrap)
                wrap.Dispose();
        }
        _textureCache.Clear();
        _textureLru.Clear();
        foreach (var m in _messages)
        {
            DisposeMessageTextures(m);
        }
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
        _previewMessage = BridgeMessageFormatter.BridgeFormattedMessage.Empty;
        _previewKey = string.Empty;
        EmbedRenderer.ClearCache();
    }

    public void Dispose()
    {
        StopNetworking();
        _channelSelection.ChannelChanged -= HandleChannelSelectionChanged;
        _bridge.Dispose();
        ClearTextureCache();
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
        TrySubscribeCurrentChannel(force: true, refreshMessages: false);
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

        if (_textureCache.TryGetValue(url, out var cached))
        {
            _textureLru.Remove(cached.Node);
            _textureLru.AddFirst(cached.Node);
            set(cached.Texture);
            return;
        }

        var node = _textureLru.AddFirst(url);
        _textureCache[url] = new TextureCacheEntry(null, node);

        if (_textureCache.Count > TextureCacheCapacity)
        {
            var last = _textureLru.Last;
            if (last != null)
            {
                if (_textureCache.TryGetValue(last.Value, out var toRemove))
                {
                    if (toRemove.Texture?.GetWrapOrEmpty() is IDisposable wrap)
                        wrap.Dispose();
                    _textureCache.Remove(last.Value);
                }
                _textureLru.RemoveLast();
            }
        }

        _ = Task.Run(async () =>
        {
            try
            {
                var bytes = await _httpClient.GetByteArrayAsync(url).ConfigureAwait(false);
                using var stream = new MemoryStream(bytes);
                var image = ImageResult.FromStream(stream, ColorComponents.RedGreenBlueAlpha);
                var wrap = PluginServices.Instance!.TextureProvider.CreateFromRaw(
                    RawImageSpecification.Rgba32(image.Width, image.Height),
                    image.Data);
                var texture = new ForwardingSharedImmediateTexture(wrap);
                if (_textureCache.TryGetValue(url, out var entry))
                {
                    entry.Texture = texture;
                }
                _ = PluginServices.Instance!.Framework.RunOnTick(() => set(texture));
            }
            catch
            {
                _ = PluginServices.Instance!.Framework.RunOnTick(() => set(null));
            }
        });
    }
}
