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
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Textures;
using Dalamud.Interface.Utility;
using StbImageSharp;
using System.IO;
using DiscordHelper;
using System.Diagnostics;
using Dalamud.Interface.ImGuiFileDialog;

namespace DemiCatPlugin;

public class ChatWindow : IDisposable
{
    protected readonly Config _config;
    protected readonly HttpClient _httpClient;
    protected readonly List<DiscordMessageDto> _messages = new();
    protected readonly List<ChannelDto> _channels = new();
    protected int _selectedIndex;
    protected bool _channelsLoaded;
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
    private readonly EmojiPicker _emojiPicker;
    private readonly Dictionary<string, EmojiPicker.EmojiDto> _emojiCatalog = new();
    private bool _emojiCatalogLoaded;
    private bool _emojiFetchInProgress;
    protected readonly ChatBridge _bridge;
    private readonly ChannelSelectionService _channelSelection;
    private readonly string _channelKind;
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true
    };
    private const int TextureCacheCapacity = 100;
    private const int MaxMessages = 100;
    private readonly Dictionary<string, TextureCacheEntry> _textureCache = new();
    private readonly LinkedList<string> _textureLru = new();
    private readonly Dictionary<string, TypingUser> _typingUsers = new();
    private int _selectionStart;
    private int _selectionEnd;

    protected string CurrentChannelId => _channelSelection.GetChannel(_channelKind);

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

    public bool ChannelsLoaded
    {
        get => _channelsLoaded;
        set => _channelsLoaded = value;
    }

    public DiscordPresenceService? Presence => _presence;
    public Action<string?, Action<ISharedImmediateTexture?>> TextureLoader => LoadTexture;

    protected virtual string MessagesPath => "/api/messages";

    public ChatWindow(Config config, HttpClient httpClient, DiscordPresenceService? presence, TokenManager tokenManager, ChannelService channelService, ChannelSelectionService channelSelection, string channelKind)
    {
        _config = config;
        _httpClient = httpClient;
        _presence = presence;
        _tokenManager = tokenManager;
        _channelService = channelService;
        _channelSelection = channelSelection;
        _channelKind = channelKind;
        _emojiPicker = new EmojiPicker(config, httpClient) { TextureLoader = LoadTexture };
        _useCharacterName = config.UseCharacterName;
        _bridge = new ChatBridge(config, httpClient, tokenManager, BuildWebSocketUri);
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

        EnsureEmojiCatalog();
    }

    public virtual void StartNetworking()
    {
        _bridge.Start();
        var chan = CurrentChannelId;
        if (!string.IsNullOrEmpty(chan))
        {
            _bridge.Unsubscribe(chan);
            _bridge.Subscribe(chan);
        }
        _presence?.Reset();
    }

    public void StopNetworking()
    {
        _bridge.Stop();
        _presence?.Stop();
    }

    // ---- UTF-8 helpers for ImGui byte-buffer overloads ----
    private static byte[] MakeUtf8Buffer(string? text, int capacity)
    {
        if (capacity < 1) capacity = 1;
        var buf = new byte[capacity];
        if (!string.IsNullOrEmpty(text))
        {
            var encoded = Encoding.UTF8.GetBytes(text);
            var len = Math.Min(encoded.Length, capacity - 1); // leave room for NUL
            Array.Copy(encoded, 0, buf, 0, len);
            buf[len] = 0;
        }
        else
        {
            buf[0] = 0;
        }
        return buf;
    }

    private static string ReadUtf8Buffer(byte[] buf)
    {
        var len = Array.IndexOf(buf, (byte)0);
        if (len < 0) len = buf.Length;
        return Encoding.UTF8.GetString(buf, 0, len);
    }

    private void HandleChannelSelectionChanged(string kind, string oldId, string newId)
    {
        if (kind != _channelKind) return;
        PluginServices.Instance!.Framework.RunOnTick(async () =>
        {
            if (!string.IsNullOrEmpty(oldId))
                _bridge.Unsubscribe(oldId);
            if (!string.IsNullOrEmpty(newId))
                _bridge.Subscribe(newId);
            await RefreshMessages();
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

        if (!_channelsLoaded)
        {
            _ = FetchChannels();
        }

        if (_channels.Count > 0)
        {
            var channelNames = _channels.Select(c => c.Name).ToArray();
            if (ImGui.Combo("Channel", ref _selectedIndex, channelNames, channelNames.Length))
            {
                var newId = _channels[_selectedIndex].Id;
                ClearTextureCache();
                _channelSelection.SetChannel(_channelKind, newId);
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
        }

        // Calculate available space and reserve room for the input section so it remains visible
        var availableHeight = ImGui.GetContentRegionAvail().Y;
        var inputSectionHeight = ImGui.GetFrameHeightWithSpacing() * 8;
        var scrollRegionHeight = MathF.Max(1f, availableHeight - inputSectionHeight);
        ImGui.BeginChild("##chatScroll", new Vector2(-1, scrollRegionHeight), true);
        var clipper = new ImGuiListClipper();
        clipper.Begin(_messages.Count);
        while (clipper.Step())
        {
            for (int i = clipper.DisplayStart; i < clipper.DisplayEnd; i++)
            {
                var msg = _messages[i];
                ImGui.PushID(msg.Id);
                ImGui.BeginGroup();
                if (msg.Author != null &&
                    !string.IsNullOrEmpty(msg.Author.AvatarUrl) &&
                    msg.AvatarTexture == null)
                {
                    LoadTexture(msg.Author.AvatarUrl, t => msg.AvatarTexture = t);
                }
                if (msg.AvatarTexture != null)
                {
                    var wrap = msg.AvatarTexture.GetWrapOrEmpty();
                    ImGui.Image(wrap.Handle, new Vector2(32, 32));
                }
                else
                {
                    ImGui.Dummy(new Vector2(32, 32));
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
                        EmbedRenderer.Draw(embed, LoadTexture, cid => _ = Interact(msg.Id, msg.ChannelId, cid));
                    }
                }
                if (msg.Attachments != null)
                {
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
                                var size = new Vector2(wrapAtt.Width, wrapAtt.Height);
                                ImGui.Image(wrapAtt.Handle, size);
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
                    EmbedRenderer.Draw(pseudo, LoadTexture, cid => _ = Interact(msg.Id, msg.ChannelId, cid));
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
                    _emojiPicker.Draw(selection =>
                    {
                        _ = React(msg.Id, selection, false);
                        ImGui.CloseCurrentPopup();
                    });
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

                ImGui.PopID();
                ImGui.EndGroup();
            }
        }
        clipper.End();
        ImGui.EndChild();
        _bridge.Ack(CurrentChannelId);
        SaveConfig();

        if (_replyToId != null)
        {
            var refMsg = _messages.Find(m => m.Id == _replyToId);
            if (refMsg != null)
            {
                var preview = refMsg.Content ?? string.Empty;
                if (preview.Length > 50) preview = preview.Substring(0, 50) + "...";
                ImGui.TextUnformatted($"Replying to {refMsg.Author?.Name ?? "Unknown"}: {preview}");
                ImGui.SameLine();
                if (ImGui.SmallButton("Cancel Reply"))
                {
                    _replyToId = null;
                }
            }
        }

        if (ImGui.BeginPopup("editMessage"))
        {
            var editBuf = MakeUtf8Buffer(_editContent, 2048);
            ImGui.InputTextMultiline("##editContent", editBuf, new Vector2(400, ImGui.GetTextLineHeight() * 5));
            _editContent = ReadUtf8Buffer(editBuf);

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
        foreach (var att in _attachments.ToArray())
        {
            ImGui.TextUnformatted(Path.GetFileName(att));
            ImGui.SameLine();
            if (ImGui.SmallButton($"X##{att}"))
            {
                _attachments.Remove(att);
            }
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
            ImGui.TextUnformatted($"{names} {verb} typing...");
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

        if (!string.IsNullOrEmpty(_input))
        {
            var preview = new DiscordMessageDto { Content = _input };
            FormatContent(preview);
        }

        var inputBuf = MakeUtf8Buffer(_input, 2048);
        ImGui.PushItemWidth(ImGui.GetContentRegionAvail().X - 72f);
        var send = ImGui.InputText(
            "##chatInput",
            inputBuf,
            ImGuiInputTextFlags.EnterReturnsTrue | ImGuiInputTextFlags.CallbackAlways,
            new ImGui.ImGuiInputTextCallbackDelegate(OnInputEdited)
        );
        ImGui.PopItemWidth();
        _input = ReadUtf8Buffer(inputBuf);

        ImGui.SameLine();
        if (ImGui.Button("😊")) ImGui.OpenPopup("##dc_emoji_picker");
        if (ImGui.BeginPopup("##dc_emoji_picker"))
        {
            _emojiPicker.Draw(selection => _input += selection);
            ImGui.EndPopup();
        }

        ImGui.SameLine();
        if (ImGui.Button("Send") || send)
        {
            _ = SendMessage();
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

    public void SetChannels(List<ChannelDto> channels)
    {
        _channels.Clear();
        _channels.AddRange(channels);
        var current = CurrentChannelId;
        if (!string.IsNullOrEmpty(current))
        {
            _selectedIndex = _channels.FindIndex(c => c.Id == current);
            if (_selectedIndex < 0) _selectedIndex = 0;
        }
        if (_channels.Count > 0)
        {
            var newId = _channels[_selectedIndex].Id;
            _channelSelection.SetChannel(_channelKind, newId);
        }
    }

    private void EnsureEmojiCatalog()
    {
        if (_emojiCatalogLoaded || _emojiFetchInProgress)
            return;
        _emojiFetchInProgress = true;
        _ = Task.Run(async () =>
        {
            if (!ApiHelpers.ValidateApiBaseUrl(_config) || !_tokenManager.IsReady())
            {
                _emojiFetchInProgress = false;
                return;
            }
            try
            {
                var request = new HttpRequestMessage(HttpMethod.Get, $"{_config.ApiBaseUrl.TrimEnd('/')}/api/emojis");
                ApiHelpers.AddAuthHeader(request, _tokenManager);
                var response = await _httpClient.SendAsync(request).ConfigureAwait(false);
                if (response.StatusCode == HttpStatusCode.Unauthorized)
                {
                    _tokenManager.Clear("Invalid API key");
                    _emojiFetchInProgress = false;
                    return;
                }
                if (!response.IsSuccessStatusCode)
                {
                    _emojiFetchInProgress = false;
                    return;
                }
                var stream = await response.Content.ReadAsStreamAsync().ConfigureAwait(false);
                var list = await JsonSerializer.DeserializeAsync<List<EmojiPicker.EmojiDto>>(stream).ConfigureAwait(false) ?? new List<EmojiPicker.EmojiDto>();
                _ = PluginServices.Instance!.Framework.RunOnTick(() =>
                {
                    _emojiCatalog.Clear();
                    foreach (var e in list)
                    {
                        _emojiCatalog[e.Id] = e;
                        _emojiCatalog[e.Name] = e;
                        LoadTexture(e.ImageUrl, t => e.Texture = t);
                    }
                    _emojiCatalogLoaded = true;
                    _emojiFetchInProgress = false;
                });
            }
            catch
            {
                _emojiFetchInProgress = false;
            }
        });
    }

    protected void FormatContent(DiscordMessageDto msg)
    {
        EnsureEmojiCatalog();
        var text = msg.Content ?? string.Empty;
        text = ReplaceMentionTokens(text, msg.Mentions);
        text = MarkdownFormatter.Format(text);
        var parts = Regex.Split(text, "(<a?:[a-zA-Z0-9_]+:\\d+>|:[a-zA-Z0-9_]+:)");
        ImGui.PushTextWrapPos();
        var first = true;
        foreach (var part in parts)
        {
            if (string.IsNullOrEmpty(part)) continue;
            if (!first)
            {
                ImGui.SameLine(0, 0);
            }
            var guildMatch = Regex.Match(part, "^<a?:([a-zA-Z0-9_]+):(\\d+)>$");
            if (guildMatch.Success)
            {
                var name = guildMatch.Groups[1].Value;
                var id = guildMatch.Groups[2].Value;
                var animated = part.StartsWith("<a:");
                if (!_emojiCatalog.TryGetValue(id, out var emoji))
                {
                    if (!_emojiCatalog.TryGetValue(name, out emoji))
                    {
                        var ext = animated ? "gif" : "png";
                        emoji = new EmojiPicker.EmojiDto
                        {
                            Id = id,
                            Name = name,
                            IsAnimated = animated,
                            ImageUrl = $"https://cdn.discordapp.com/emojis/{id}.{ext}"
                        };
                    }
                    _emojiCatalog[id] = emoji;
                    _emojiCatalog[name] = emoji;
                }
                if (emoji.Texture == null)
                {
                    LoadTexture(emoji.ImageUrl, t => emoji.Texture = t);
                }
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
                var match = Regex.Match(part, "^:([a-zA-Z0-9_]+):$");
                if (match.Success)
                {
                    var name = match.Groups[1].Value;
                    if (_emojiCatalog.TryGetValue(name, out var emoji))
                    {
                        if (emoji.Texture == null)
                        {
                            LoadTexture(emoji.ImageUrl, t => emoji.Texture = t);
                        }
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
                        ImGui.TextUnformatted($":{name}:");
                    }
                }
                else
                {
                    RenderMarkdown(part);
                }
            }
            first = false;
        }
        ImGui.PopTextWrapPos();
    }

    internal static string ReplaceMentionTokens(string text, List<DiscordMentionDto>? mentions)
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
        _input ??= string.Empty;
        var len = _input.Length;
        var s = Math.Clamp(_selectionStart, 0, len);
        var e = Math.Clamp(_selectionEnd, 0, len);
        var start = Math.Min(s, e);
        var end = Math.Max(s, e);

        if (start == end)
        {
            while (start > 0 && char.IsLetterOrDigit(_input[start - 1]))
                start--;
            while (end < len && char.IsLetterOrDigit(_input[end]))
                end++;
        }

        var selected = _input.Substring(start, end - start);
        _input = _input[..start] + prefix + selected + suffix + _input[end..];

        var cursor = start + prefix.Length + selected.Length + suffix.Length;
        _selectionStart = _selectionEnd = cursor;
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

        if (_input.Length > 2000)
        {
            _ = PluginServices.Instance!.Framework.RunOnTick(() => _statusMessage = "Message exceeds 2000 characters");
            return;
        }

        const int maxAttachments = 10;
        const long maxAttachmentSize = 25 * 1024 * 1024; // 25MB
        if (_attachments.Count > maxAttachments)
        {
            _ = PluginServices.Instance!.Framework.RunOnTick(() => _statusMessage = $"Too many attachments (max {maxAttachments})");
            return;
        }
        foreach (var att in _attachments)
        {
            try
            {
                var size = new FileInfo(att).Length;
                if (size > maxAttachmentSize)
                {
                    _ = PluginServices.Instance!.Framework.RunOnTick(() => _statusMessage = $"Attachment {Path.GetFileName(att)} too large (max 25MB)");
                    return;
                }
            }
            catch
            {
                // ignore file errors
            }
        }

        try
        {
            var presences = _presence?.Presences ?? new List<PresenceDto>();
            var content = MentionResolver.Resolve(_input, presences, RoleCache.Roles);

            HttpRequestMessage request;
            if (_attachments.Count > 0)
            {
                request = await BuildMultipartRequest(content);
            }
            else
            {
                var body = new
                {
                    channelId = channelId,
                    content,
                    useCharacterName = _useCharacterName,
                    messageReference = _replyToId != null
                        ? new { messageId = _replyToId, channelId = channelId }
                        : null
                };
                request = new HttpRequestMessage(HttpMethod.Post, $"{_config.ApiBaseUrl.TrimEnd('/')}{MessagesPath}");
                request.Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
            }
            ApiHelpers.AddAuthHeader(request, _tokenManager);
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

                var text = _input;
                var optimistic = new DiscordMessageDto
                {
                    Id = id ?? Guid.NewGuid().ToString(),
                    ChannelId = channelId,
                    Content = text,
                    Author = new DiscordUserDto { Name = "You" },
                    Timestamp = DateTime.UtcNow
                };

                _ = PluginServices.Instance!.Framework.RunOnTick(() =>
                {
                    _messages.Add(optimistic);
                    _input = string.Empty;
                    _statusMessage = string.Empty;
                    _lastError = string.Empty;
                    _replyToId = null;
                    _attachments.Clear();
                });
            }
            else
            {
                var responseBody = await response.Content.ReadAsStringAsync();
                PluginServices.Instance!.Log.Warning($"Failed to send message. Status: {response.StatusCode}. Response Body: {responseBody}");
                var msg = $"{(int)response.StatusCode} {response.ReasonPhrase}";
                try
                {
                    using var doc = JsonDocument.Parse(responseBody);
                    if (doc.RootElement.TryGetProperty("detail", out var detail))
                    {
                        if (detail.ValueKind == JsonValueKind.String)
                        {
                            msg = detail.GetString() ?? msg;
                        }
                        else if (detail.ValueKind == JsonValueKind.Object)
                        {
                            List<string> parts = new();
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
                                msg = string.Join("\n", parts);
                            }
                            else if (detail.TryGetProperty("message", out var m))
                            {
                                msg = m.GetString() ?? msg;
                            }
                        }
                    }
                }
                catch
                {
                    // ignore parse errors
                }
                _ = PluginServices.Instance!.Framework.RunOnTick(() =>
                {
                    _statusMessage = msg;
                    _lastError = msg;
                });
                if (response.StatusCode == HttpStatusCode.NotFound)
                {
                    _ = PluginServices.Instance!.Framework.RunOnTick(async () => await RefreshChannels());
                }
            }
        }
        catch (Exception ex)
        {
            PluginServices.Instance!.Log.Error(ex, "Error sending message");
            _ = PluginServices.Instance!.Framework.RunOnTick(() =>
            {
                _statusMessage = "Failed to send message";
                _lastError = "Failed to send message";
            });
        }
    }

    protected virtual async Task<HttpRequestMessage> BuildMultipartRequest(string content)
    {
        var channelId = CurrentChannelId;
        var url = $"{_config.ApiBaseUrl.TrimEnd('/')}/api/channels/{channelId}/messages";
        var form = new MultipartFormDataContent();
        form.Add(new StringContent(content), "content");
        form.Add(new StringContent(_useCharacterName ? "true" : "false"), "useCharacterName");
        if (!string.IsNullOrEmpty(_replyToId))
        {
            var refJson = JsonSerializer.Serialize(new { messageId = _replyToId });
            form.Add(new StringContent(refJson, Encoding.UTF8), "message_reference");
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
            const int PageSize = 50;
            var all = new List<DiscordMessageDto>();
            string? before = null;
            var hasCursor = _config.ChatCursors.TryGetValue(channelId, out var since);
            var after = hasCursor ? since.ToString() : null;
            while (all.Count < MaxMessages)
            {
                var url = $"{_config.ApiBaseUrl.TrimEnd('/')}{MessagesPath}/{channelId}?limit={PageSize}";
                if (before != null)
                {
                    url += $"&before={before}";
                }
                if (after != null)
                {
                    url += $"&after={after}";
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

                all.InsertRange(0, msgs);
                if (all.Count >= MaxMessages || msgs.Count < PageSize)
                {
                    break;
                }
                before = msgs[0].Id;
            }

            if (all.Count > MaxMessages)
            {
                all = all.Skip(all.Count - MaxMessages).ToList();
            }

            _ = PluginServices.Instance!.Framework.RunOnTick(() =>
            {
                if (since == 0)
                {
                    _messages.Clear();
                }
                foreach (var m in all)
                {
                    _messages.Add(m);
                }
                TrimMessages();
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
        if (_messages.Count > 0 && long.TryParse(_messages[^1].Id, out var last))
        {
            var channelId = CurrentChannelId;
            _config.ChatCursors[channelId] = last;
        }
    }

    private void DisposeMessageTextures(DiscordMessageDto msg)
    {
        if (msg.AvatarTexture?.GetWrapOrEmpty() is IDisposable wrap)
        {
            wrap.Dispose();
            msg.AvatarTexture = null;
        }
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
        PluginServices.Instance!.PluginInterface.SavePluginConfig(_config);
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
        if (!_tokenManager.IsReady())
        {
            _channelsLoaded = true;
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
            });
            return;
        }

        try
        {
            var channels = (await _channelService.FetchAsync(_channelKind, CancellationToken.None)).ToList();
            if (await ChannelNameResolver.Resolve(channels, _httpClient, _config, refreshed, () => FetchChannels(true))) return;
            _ = PluginServices.Instance!.Framework.RunOnTick(() =>
            {
                SetChannels(channels);
                _channelsLoaded = true;
                _channelFetchFailed = false;
                _channelErrorMessage = string.Empty;
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
        var chan = CurrentChannelId;
        _bridge.Unsubscribe(chan);
        _bridge.Subscribe(chan);
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
