using System;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.Net.Http;
using System.Net.WebSockets;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Numerics;
using System.Threading;
using System.Threading.Tasks;
using System.Linq;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Textures;
using StbImageSharp;
using System.IO;
using DiscordHelper;
using System.Diagnostics;

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
    protected string _channelId;
    protected string _input = string.Empty;
    protected bool _useCharacterName;
    protected string _statusMessage = string.Empty;
    protected readonly DiscordPresenceService? _presence;
    protected readonly List<string> _attachments = new();
    protected string _newAttachmentPath = string.Empty;
    protected string? _replyToId;
    protected string? _editingMessageId;
    protected string _editingChannelId = string.Empty;
    protected string _editContent = string.Empty;
    private static readonly string[] DefaultReactions = new[] { "👍", "👎", "❤️" };
    private readonly EmojiPicker _emojiPicker;
    private readonly Dictionary<string, EmojiPicker.EmojiDto> _emojiCatalog = new();
    private bool _emojiCatalogLoaded;
    private bool _emojiFetchInProgress;
    private ClientWebSocket? _ws;
    private Task? _wsTask;
    private CancellationTokenSource? _wsCts;
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true
    };
    private const int TextureCacheCapacity = 100;
    private readonly Dictionary<string, TextureCacheEntry> _textureCache = new();
    private readonly LinkedList<string> _textureLru = new();
    private readonly ConcurrentDictionary<string, TaskCompletionSource<bool>> _pendingMessages = new();

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

    public bool ChannelsLoaded
    {
        get => _channelsLoaded;
        set => _channelsLoaded = value;
    }

    public DiscordPresenceService? Presence => _presence;
    public Action<string?, Action<ISharedImmediateTexture?>> TextureLoader => LoadTexture;

    protected virtual string MessagesPath => "/api/messages";

    public ChatWindow(Config config, HttpClient httpClient, DiscordPresenceService? presence)
    {
        _config = config;
        _httpClient = httpClient;
        _presence = presence;
        _emojiPicker = new EmojiPicker(config, httpClient) { TextureLoader = LoadTexture };
        _channelId = config.ChatChannelId;
        _useCharacterName = config.UseCharacterName;
    }

    public void StartNetworking()
    {
        if (!_config.EnableFcChat)
        {
            return;
        }
        _wsCts?.Cancel();
        _ws?.Dispose();
        _ws = null;
        _wsCts = new CancellationTokenSource();
        _wsTask = RunWebSocket(_wsCts.Token);
        _presence?.Reset();
    }

    public void StopNetworking()
    {
        _wsCts?.Cancel();
        _ws?.Dispose();
        _ws = null;
        _presence?.Reset();
    }

    public virtual void Draw()
    {
        if (!_channelsLoaded)
        {
            _ = FetchChannels();
        }

        if (_channels.Count > 0)
        {
            var channelNames = _channels.Select(c => c.Name).ToArray();
            if (ImGui.Combo("Channel", ref _selectedIndex, channelNames, channelNames.Length))
            {
                _channelId = _channels[_selectedIndex].Id;
                _config.ChatChannelId = _channelId;
                ClearTextureCache();
                SaveConfig();
                _ = RefreshMessages();
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

        ImGui.BeginChild("##chatScroll", new Vector2(-1, -30), true);
        foreach (var msg in _messages)
        {
            ImGui.PushID(msg.Id);
            ImGui.BeginGroup();
            if (!string.IsNullOrEmpty(msg.Author.AvatarUrl) && msg.AvatarTexture == null)
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
                    if (att.ContentType != null && att.ContentType.StartsWith("image") )
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
                    Style = c.Style
                }).ToList();
                var pseudo = new EmbedDto { Id = msg.Id + "_components", Buttons = buttons };
                EmbedRenderer.Draw(pseudo, LoadTexture, cid => _ = Interact(msg.Id, msg.ChannelId, cid));
            }
            ImGui.Spacing();
            if (msg.Reactions != null && msg.Reactions.Count > 0)
            {
                for (int i = 0; i < msg.Reactions.Count; i++)
                {
                    var reaction = msg.Reactions[i];
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
                    if (i < msg.Reactions.Count - 1) ImGui.SameLine();
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
        ImGui.EndChild();

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
            ImGui.InputTextMultiline("##editContent", ref _editContent, 1024, new Vector2(400, ImGui.GetTextLineHeight() * 5));
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
            ImGui.OpenPopup("attachFile");
        }
        if (ImGui.BeginPopup("attachFile"))
        {
            ImGui.InputText("Path", ref _newAttachmentPath, 260);
            if (ImGui.Button("Add") && File.Exists(_newAttachmentPath))
            {
                _attachments.Add(_newAttachmentPath);
                _newAttachmentPath = string.Empty;
            }
            ImGui.EndPopup();
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

        var send = ImGui.InputText("##chatInput", ref _input, 512, ImGuiInputTextFlags.EnterReturnsTrue);
        ImGui.SameLine();
        if (ImGui.Button("Send") || send)
        {
            _ = SendMessage();
        }

        if (!string.IsNullOrEmpty(_statusMessage))
        {
            ImGui.TextUnformatted(_statusMessage);
        }
    }

    public void SetChannels(List<ChannelDto> channels)
    {
        _channels.Clear();
        _channels.AddRange(channels);
        if (!string.IsNullOrEmpty(_channelId))
        {
            _selectedIndex = _channels.FindIndex(c => c.Id == _channelId);
            if (_selectedIndex < 0) _selectedIndex = 0;
        }
        if (_channels.Count > 0)
        {
            _channelId = _channels[_selectedIndex].Id;
            _ = RefreshMessages();
        }
    }

    private void EnsureEmojiCatalog()
    {
        if (_emojiCatalogLoaded || _emojiFetchInProgress)
            return;
        _emojiFetchInProgress = true;
        _ = Task.Run(async () =>
        {
            if (!ApiHelpers.ValidateApiBaseUrl(_config))
            {
                _emojiCatalogLoaded = true;
                _emojiFetchInProgress = false;
                return;
            }
            try
            {
                var request = new HttpRequestMessage(HttpMethod.Get, $"{_config.ApiBaseUrl.TrimEnd('/')}/api/emojis");
                ApiHelpers.AddAuthHeader(request, _config);
                var response = await _httpClient.SendAsync(request).ConfigureAwait(false);
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
        if (msg.Mentions != null)
        {
            foreach (var m in msg.Mentions)
            {
                text = text.Replace($"<@{m.Id}>", $"@{m.Name}");
            }
        }
        text = Regex.Replace(text, "<a?:([a-zA-Z0-9_]+):\\d+>", ":$1:");

        text = MarkdownFormatter.Format(text);

        var parts = Regex.Split(text, "(:[a-zA-Z0-9_]+:)");
        ImGui.PushTextWrapPos();
        var first = true;
        foreach (var part in parts)
        {
            if (string.IsNullOrEmpty(part)) continue;
            if (!first)
            {
                ImGui.SameLine(0, 0);
            }
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
                ImGui.TextUnformatted(part);
            }
            first = false;
        }
        ImGui.PopTextWrapPos();
    }

    protected virtual async Task SendMessage()
    {
        if (!ApiHelpers.ValidateApiBaseUrl(_config))
        {
            PluginServices.Instance!.Log.Warning("Cannot send message: API base URL is not configured.");
            _ = PluginServices.Instance!.Framework.RunOnTick(() => _statusMessage = "Invalid API URL");
            return;
        }
        if (string.IsNullOrWhiteSpace(_channelId) || string.IsNullOrWhiteSpace(_input))
        {
            return;
        }

        try
        {
            // Build request body (includes reply threading if set)
            var presences = _presence?.Presences ?? new List<PresenceDto>();
            var content = MentionResolver.Resolve(_input, presences, RoleCache.Roles);

            var body = new
            {
                channelId = _channelId,
                content,
                useCharacterName = _useCharacterName,
                messageReference = _replyToId != null
                    ? new { messageId = _replyToId, channelId = _channelId }
                    : null
            };
            var request = new HttpRequestMessage(HttpMethod.Post, $"{_config.ApiBaseUrl.TrimEnd('/')}/api/messages");
            request.Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
            ApiHelpers.AddAuthHeader(request, _config);
            var response = await _httpClient.SendAsync(request);
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

                _ = PluginServices.Instance!.Framework.RunOnTick(() =>
                {
                    _input = string.Empty;
                    _statusMessage = string.Empty;
                    _replyToId = null; // <-- clear reply state after a successful send
                });

                await WaitForEchoAndRefresh(id);
            }
            else
            {
                var responseBody = await response.Content.ReadAsStringAsync();
                PluginServices.Instance!.Log.Warning($"Failed to send message. Status: {response.StatusCode}. Response Body: {responseBody}");
                var msg = "Failed to send message";
                try
                {
                    using var doc = JsonDocument.Parse(responseBody);
                    if (doc.RootElement.TryGetProperty("detail", out var detail))
                        msg = detail.GetString() ?? msg;
                }
                catch
                {
                    // ignore parse errors
                }
                _ = PluginServices.Instance!.Framework.RunOnTick(() => _statusMessage = msg);
            }
        }
        catch (Exception ex)
        {
            PluginServices.Instance!.Log.Error(ex, "Error sending message");
            _ = PluginServices.Instance!.Framework.RunOnTick(() => _statusMessage = "Failed to send message");
        }
    }

    protected virtual async Task React(string messageId, string emoji, bool remove)
    {
        if (!ApiHelpers.ValidateApiBaseUrl(_config))
        {
            PluginServices.Instance!.Log.Warning("Cannot react: API base URL is not configured.");
            return;
        }
        if (string.IsNullOrWhiteSpace(_channelId) || string.IsNullOrWhiteSpace(messageId) || string.IsNullOrWhiteSpace(emoji))
        {
            return;
        }

        try
        {
            var method = remove ? HttpMethod.Delete : HttpMethod.Put;
            var url = $"{_config.ApiBaseUrl.TrimEnd('/')}/api/channels/{_channelId}/messages/{messageId}/reactions/{Uri.EscapeDataString(emoji)}";
            var request = new HttpRequestMessage(method, url);
            ApiHelpers.AddAuthHeader(request, _config);
            var response = await _httpClient.SendAsync(request);
            if (response.IsSuccessStatusCode)
            {
                await RefreshMessages();
            }
            else
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
            ApiHelpers.AddAuthHeader(request, _config);
            var response = await _httpClient.SendAsync(request);
            if (response.IsSuccessStatusCode)
            {
                await RefreshMessages();
            }
            else
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
            ApiHelpers.AddAuthHeader(request, _config);
            var response = await _httpClient.SendAsync(request);
            if (response.IsSuccessStatusCode)
            {
                await RefreshMessages();
            }
            else
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
            ApiHelpers.AddAuthHeader(request, _config);
            var response = await _httpClient.SendAsync(request);
            if (response.IsSuccessStatusCode)
            {
                await RefreshMessages();
            }
            else
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

    protected async Task WaitForEchoAndRefresh(string? id)
    {
        if (!string.IsNullOrEmpty(id))
        {
            if (_messages.Any(m => m.Id == id))
            {
                await RefreshMessages();
                return;
            }
            var tcs = new TaskCompletionSource<bool>();
            _pendingMessages[id] = tcs;
            await Task.WhenAny(tcs.Task, Task.Delay(TimeSpan.FromSeconds(5)));
            _pendingMessages.TryRemove(id, out _);
        }
        await RefreshMessages();
    }

    public virtual async Task RefreshMessages()
    {
        if (!ApiHelpers.ValidateApiBaseUrl(_config) || string.IsNullOrEmpty(_channelId))
        {
            return;
        }

        try
        {
            const int PageSize = 50;
            var all = new List<DiscordMessageDto>();
            string? before = null;
            while (true)
            {
                var url = $"{_config.ApiBaseUrl.TrimEnd('/')}{MessagesPath}/{_channelId}?limit={PageSize}";
                if (before != null)
                {
                    url += $"&before={before}";
                }

                var request = new HttpRequestMessage(HttpMethod.Get, url);
                ApiHelpers.AddAuthHeader(request, _config);

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
                if (msgs.Count < PageSize)
                {
                    break;
                }
                before = msgs[0].Id;
            }

            _ = PluginServices.Instance!.Framework.RunOnTick(() =>
            {
                _messages.Clear();
                foreach (var m in all)
                {
                    _messages.Add(m);
                    if (!string.IsNullOrEmpty(m.Author.AvatarUrl))
                    {
                        LoadTexture(m.Author.AvatarUrl, t => m.AvatarTexture = t);
                    }
                    if (m.Attachments != null)
                    {
                        foreach (var a in m.Attachments)
                        {
                            if (a.ContentType != null && a.ContentType.StartsWith("image"))
                            {
                                LoadTexture(a.Url, t => a.Texture = t);
                            }
                        }
                    }
                }
            });
        }
        catch (Exception ex)
        {
            PluginServices.Instance!.Log.Error(ex, "Error refreshing messages");
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
        ClearTextureCache();
    }

    protected void SaveConfig()
    {
        PluginServices.Instance!.PluginInterface.SavePluginConfig(_config);
    }

    public Task RefreshChannels()
    {
        _channelsLoaded = false;
        return FetchChannels();
    }

    protected virtual async Task FetchChannels(bool refreshed = false)
    {
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
            var request = new HttpRequestMessage(HttpMethod.Get, $"{_config.ApiBaseUrl.TrimEnd('/')}/api/channels");
            ApiHelpers.AddAuthHeader(request, _config);
            var response = await _httpClient.SendAsync(request);
            if (!response.IsSuccessStatusCode)
            {
                var responseBody = await response.Content.ReadAsStringAsync();
                PluginServices.Instance!.Log.Warning($"Failed to fetch channels. Status: {response.StatusCode}. Response Body: {responseBody}");
                _ = PluginServices.Instance!.Framework.RunOnTick(() =>
                {
                    _channelFetchFailed = true;
                    _channelErrorMessage = response.StatusCode == HttpStatusCode.Forbidden
                        ? "Forbidden – check API key/roles"
                        : "Failed to load channels";
                    _channelsLoaded = true;
                });
                return;
            }
            var stream = await response.Content.ReadAsStreamAsync();
            var dto = await JsonSerializer.DeserializeAsync<ChannelListDto>(stream) ?? new ChannelListDto();
            if (await ChannelNameResolver.Resolve(dto.Chat, _httpClient, _config, refreshed, () => FetchChannels(true))) return;
            _ = PluginServices.Instance!.Framework.RunOnTick(() =>
            {
                SetChannels(dto.Chat);
                _channelsLoaded = true;
                _channelFetchFailed = false;
                _channelErrorMessage = string.Empty;
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

    private async Task RunWebSocket(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            if (!ApiHelpers.ValidateApiBaseUrl(_config))
            {
                _ = PluginServices.Instance!.Framework.RunOnTick(() =>
                    _statusMessage = "Invalid API URL");
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(5), token);
                }
                catch
                {
                    // ignore cancellation
                }
                continue;
            }

            var forbidden = false;
            try
            {
                _ws?.Dispose();
                _ws = new ClientWebSocket();
                ApiHelpers.AddAuthHeader(_ws, _config);
                var uri = BuildWebSocketUri();
                await _ws.ConnectAsync(uri, token);
                // Refresh presence information in case updates were missed while offline.
                _presence?.Reload();
                _ = _presence?.Refresh();
                _ = PluginServices.Instance!.Framework.RunOnTick(() => _statusMessage = string.Empty);

                var buffer = new byte[8192];
                while (_ws.State == WebSocketState.Open && !token.IsCancellationRequested)
                {
                    using var ms = new MemoryStream();
                    WebSocketReceiveResult result;
                    do
                    {
                        result = await _ws.ReceiveAsync(new ArraySegment<byte>(buffer), token);
                        if (result.MessageType == WebSocketMessageType.Close)
                        {
                            break;
                        }

                        ms.Write(buffer, 0, result.Count);

                        if (result.Count == buffer.Length)
                        {
                            Array.Resize(ref buffer, buffer.Length * 2);
                        }

                    } while (!result.EndOfMessage);

                    if (result.MessageType == WebSocketMessageType.Close)
                    {
                        break;
                    }

                    var json = Encoding.UTF8.GetString(ms.ToArray());
                    if (json == "ping")
                    {
                        await _ws.SendAsync(new ArraySegment<byte>(Encoding.UTF8.GetBytes("pong")), WebSocketMessageType.Text, true, token);
                        continue;
                    }
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
                                    }
                                });
                            }
                        }
                        else
                        {

                            var msg = document.RootElement.Deserialize<DiscordMessageDto>(JsonOpts);

                            if (msg != null)
                            {
                                if (_pendingMessages.TryRemove(msg.Id, out var tcs))
                                {
                                    tcs.TrySetResult(true);
                                }
                                _ = PluginServices.Instance!.Framework.RunOnTick(() =>
                                {
                                    if (msg.ChannelId == _channelId)
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
                                        if (!string.IsNullOrEmpty(msg.Author.AvatarUrl))
                                        {
                                            LoadTexture(msg.Author.AvatarUrl, t => msg.AvatarTexture = t);
                                        }
                                        if (msg.Attachments != null)
                                        {
                                            foreach (var a in msg.Attachments)
                                            {
                                                if (a.ContentType != null && a.ContentType.StartsWith("image"))
                                                {
                                                    LoadTexture(a.Url, t => a.Texture = t);
                                                }
                                            }
                                        }
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
            }
            catch (Exception ex)
            {
                PluginServices.Instance!.Log.Error(ex, "WebSocket connection error");
                forbidden = ex is HttpRequestException hre && hre.StatusCode == HttpStatusCode.Forbidden
                    || (ex as WebSocketException)?.Message.Contains("403") == true
                    || (ex.InnerException as HttpRequestException)?.StatusCode == HttpStatusCode.Forbidden;
                var msg = forbidden ? "Forbidden – check API key/roles" : $"Connection failed: {ex.Message}";
                _ = PluginServices.Instance!.Framework.RunOnTick(() => _statusMessage = msg);
            }
            finally
            {
                _ws?.Dispose();
                _ws = null;
            }

            try
            {
                if (!forbidden)
                {
                    _ = PluginServices.Instance!.Framework.RunOnTick(() =>
                        _statusMessage = "Reconnecting...");
                    await Task.Delay(TimeSpan.FromSeconds(5), token);
                }
                else
                {
                    await Task.Delay(TimeSpan.FromSeconds(30), token);
                }
            }
            catch
            {
                // ignore cancellation
            }
        }
    }

    protected virtual Uri BuildWebSocketUri()
    {
        var baseUri = _config.ApiBaseUrl.TrimEnd('/') + "/ws/messages";
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

    protected class ChannelListDto
    {
        [JsonPropertyName(ChannelKind.FcChat)] public List<ChannelDto> Chat { get; set; } = new();
    }
}
