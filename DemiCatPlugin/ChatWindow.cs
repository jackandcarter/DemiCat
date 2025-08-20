using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Net.WebSockets;
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
    protected readonly List<ChatMessageDto> _messages = new();
    protected readonly List<ChannelDto> _channels = new();
    protected int _selectedIndex;
    protected bool _channelsLoaded;
    protected bool _channelFetchFailed;
    protected string _channelId;
    protected string _input = string.Empty;
    protected bool _useCharacterName;
    protected string _statusMessage = string.Empty;
    protected readonly PresenceSidebar _presence;
    private ClientWebSocket? _ws;
    private Task? _wsTask;
    private CancellationTokenSource? _wsCts;
    private const int TextureCacheCapacity = 100;
    private readonly Dictionary<string, TextureCacheEntry> _textureCache = new();
    private readonly LinkedList<string> _textureLru = new();

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

    public PresenceSidebar Presence => _presence;

    public ChatWindow(Config config, HttpClient httpClient, PresenceSidebar presence)
    {
        _config = config;
        _httpClient = httpClient;
        _presence = presence;
        _channelId = config.ChatChannelId;
        _useCharacterName = config.UseCharacterName;
    }

    public virtual void Draw()
    {
        if (_wsTask == null)
        {
            _wsCts = new CancellationTokenSource();
            _wsTask = RunWebSocket(_wsCts.Token);
        }

        if (!_channelsLoaded)
        {
            _ = FetchChannels();
        }

        ImGui.BeginChild("##presence", new Vector2(150, 0), true);
        _presence.Draw();
        ImGui.EndChild();
        ImGui.SameLine();

        ImGui.BeginChild("##chatArea", new Vector2(0, 0), false);

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
            ImGui.TextUnformatted(_channelFetchFailed ? "Failed to load channels" : "No channels available");
        }
        if (ImGui.Checkbox("Use Character Name", ref _useCharacterName))
        {
            _config.UseCharacterName = _useCharacterName;
            SaveConfig();
        }

        ImGui.BeginChild("##chatScroll", new Vector2(0, -30), true);
        foreach (var msg in _messages)
        {
            ImGui.BeginGroup();
            if (!string.IsNullOrEmpty(msg.AuthorAvatarUrl) && msg.AvatarTexture == null)
            {
                LoadTexture(msg.AuthorAvatarUrl, t => msg.AvatarTexture = t);
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
            ImGui.TextUnformatted(msg.AuthorName);
            ImGui.SameLine();
            ImGui.TextUnformatted(msg.Timestamp.ToLocalTime().ToString());
            ImGui.TextWrapped(FormatContent(msg));
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
            ImGui.EndGroup();

            ImGui.EndGroup();
        }
        ImGui.EndChild();

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

        ImGui.EndChild();
    }

    public void SetChannels(List<ChannelDto> channels)
    {
        ResolveChannelNames(channels);
        channels.RemoveAll(c => string.IsNullOrWhiteSpace(c.Name) || c.Name == c.Id);
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

    protected void ResolveChannelNames(List<ChannelDto> channels)
    {
        foreach (var c in channels)
        {
            if (string.IsNullOrWhiteSpace(c.Name))
            {
                PluginServices.Instance!.Log.Warning($"Channel name missing for {c.Id}.");
                c.Name = c.Id;
            }
        }
    }

    protected string FormatContent(ChatMessageDto msg)
    {
        var text = msg.Content;
        if (msg.Mentions != null)
        {
            foreach (var m in msg.Mentions)
            {
                text = text.Replace($"<@{m.Id}>", $"@{m.Name}");
            }
        }
        text = Regex.Replace(text, "<a?:([a-zA-Z0-9_]+):\\d+>", ":$1:");

        text = MarkdownFormatter.Format(text);
        return text;
    }

    protected virtual async Task SendMessage()
    {
        if (!ApiHelpers.ValidateApiBaseUrl(_config) || string.IsNullOrWhiteSpace(_channelId) || string.IsNullOrWhiteSpace(_input))
        {
            return;
        }

        try
        {
            var body = new { channelId = _channelId, content = _input, useCharacterName = _useCharacterName };
            var request = new HttpRequestMessage(HttpMethod.Post, $"{_config.ApiBaseUrl.TrimEnd('/')}/api/messages");
            request.Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
            if (!string.IsNullOrEmpty(_config.AuthToken))
            {
                request.Headers.Add("X-Api-Key", _config.AuthToken);
            }
            var response = await _httpClient.SendAsync(request);
            if (response.IsSuccessStatusCode)
            {
                _ = PluginServices.Instance!.Framework.RunOnTick(() =>
                {
                    _input = string.Empty;
                    _statusMessage = string.Empty;
                });
                await RefreshMessages();
            }
            else
            {
                var responseBody = await response.Content.ReadAsStringAsync();
                PluginServices.Instance!.Log.Warning($"Failed to send message. Status: {response.StatusCode}. Response Body: {responseBody}");
                _ = PluginServices.Instance!.Framework.RunOnTick(() => _statusMessage = "Failed to send message");
            }
        }
        catch (Exception ex)
        {
            PluginServices.Instance!.Log.Error(ex, "Error sending message");
            _ = PluginServices.Instance!.Framework.RunOnTick(() => _statusMessage = "Failed to send message");
        }
    }

    public async Task RefreshMessages()
    {
        if (!ApiHelpers.ValidateApiBaseUrl(_config) || string.IsNullOrEmpty(_channelId))
        {
            return;
        }

        try
        {
            var request = new HttpRequestMessage(HttpMethod.Get, $"{_config.ApiBaseUrl.TrimEnd('/')}/api/messages/{_channelId}");
            if (!string.IsNullOrEmpty(_config.AuthToken))
            {
                request.Headers.Add("X-Api-Key", _config.AuthToken);
            }
            var response = await _httpClient.SendAsync(request);
            if (!response.IsSuccessStatusCode)
            {
                var responseBody = await response.Content.ReadAsStringAsync();
                PluginServices.Instance!.Log.Warning($"Failed to refresh messages. Status: {response.StatusCode}. Response Body: {responseBody}");
                return;
            }
            var stream = await response.Content.ReadAsStreamAsync();
            var msgs = await JsonSerializer.DeserializeAsync<List<ChatMessageDto>>(stream) ?? new List<ChatMessageDto>();
            _ = PluginServices.Instance!.Framework.RunOnTick(() =>
            {
                _messages.Clear();
                foreach (var m in msgs)
                {
                    _messages.Add(m);
                    if (!string.IsNullOrEmpty(m.AuthorAvatarUrl))
                    {
                        LoadTexture(m.AuthorAvatarUrl, t => m.AvatarTexture = t);
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

    private void DisposeMessageTextures(ChatMessageDto msg)
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
    }

    public void Dispose()
    {
        _wsCts?.Cancel();
        _ws?.Dispose();
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

    protected virtual async Task FetchChannels()
    {
        if (!ApiHelpers.ValidateApiBaseUrl(_config))
        {
            PluginServices.Instance!.Log.Warning("Cannot fetch channels: API base URL is not configured.");
            _channelFetchFailed = true;
            _channelsLoaded = true;
            return;
        }

        try
        {
            var request = new HttpRequestMessage(HttpMethod.Get, $"{_config.ApiBaseUrl.TrimEnd('/')}/api/channels");
            if (!string.IsNullOrEmpty(_config.AuthToken))
            {
                request.Headers.Add("X-Api-Key", _config.AuthToken);
            }
            var response = await _httpClient.SendAsync(request);
            if (!response.IsSuccessStatusCode)
            {
                var responseBody = await response.Content.ReadAsStringAsync();
                PluginServices.Instance!.Log.Warning($"Failed to fetch channels. Status: {response.StatusCode}. Response Body: {responseBody}");
                _channelFetchFailed = true;
                _channelsLoaded = true;
                return;
            }
            var stream = await response.Content.ReadAsStreamAsync();
            var dto = await JsonSerializer.DeserializeAsync<ChannelListDto>(stream) ?? new ChannelListDto();
            ResolveChannelNames(dto.Chat);
            _ = PluginServices.Instance!.Framework.RunOnTick(() =>
            {
                SetChannels(dto.Chat);
                _channelsLoaded = true;
                _channelFetchFailed = false;
            });
        }
        catch (Exception ex)
        {
            PluginServices.Instance!.Log.Error(ex, "Error fetching channels");
            _channelFetchFailed = true;
            _channelsLoaded = true;
        }
    }

    private async Task RunWebSocket(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            if (!ApiHelpers.ValidateApiBaseUrl(_config))
            {
                _ = PluginServices.Instance!.Framework.RunOnTick(() =>
                    _statusMessage = "Invalid API base URL");
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

            try
            {
                _ws?.Dispose();
                _ws = new ClientWebSocket();
                if (!string.IsNullOrEmpty(_config.AuthToken))
                {
                    _ws.Options.SetRequestHeader("X-Api-Key", _config.AuthToken);
                }
                var uri = BuildWebSocketUri();
                await _ws.ConnectAsync(uri, token);
                _ = PluginServices.Instance!.Framework.RunOnTick(() => _statusMessage = string.Empty);

                var buffer = new byte[8192];
                while (_ws.State == WebSocketState.Open && !token.IsCancellationRequested)
                {
                    var result = await _ws.ReceiveAsync(new ArraySegment<byte>(buffer), token);
                    if (result.MessageType == WebSocketMessageType.Close)
                    {
                        break;
                    }
                    var count = result.Count;
                    while (!result.EndOfMessage)
                    {
                        result = await _ws.ReceiveAsync(new ArraySegment<byte>(buffer, count, buffer.Length - count), token);
                        count += result.Count;
                    }
                    var json = Encoding.UTF8.GetString(buffer, 0, count);
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
                            var msg = document.RootElement.Deserialize<ChatMessageDto>();
                            if (msg != null)
                            {
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
                                        if (!string.IsNullOrEmpty(msg.AuthorAvatarUrl))
                                        {
                                            LoadTexture(msg.AuthorAvatarUrl, t => msg.AvatarTexture = t);
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
                _ = PluginServices.Instance!.Framework.RunOnTick(() =>
                    _statusMessage = $"Connection failed: {ex.Message}");
            }
            finally
            {
                _ws?.Dispose();
                _ws = null;
            }

            try
            {
                _ = PluginServices.Instance!.Framework.RunOnTick(() =>
                    _statusMessage = "Reconnecting...");
                await Task.Delay(TimeSpan.FromSeconds(5), token);
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

    private void LoadTexture(string? url, Action<ISharedImmediateTexture?> set)
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

    protected class ChatMessageDto
    {
        public string Id { get; set; } = string.Empty;
        public string ChannelId { get; set; } = string.Empty;
        public string AuthorName { get; set; } = string.Empty;
        public string? AuthorAvatarUrl { get; set; }
        public DateTime Timestamp { get; set; }
        public string Content { get; set; } = string.Empty;
        public List<AttachmentDto>? Attachments { get; set; }
        public List<MentionDto>? Mentions { get; set; }
        [JsonIgnore] public ISharedImmediateTexture? AvatarTexture { get; set; }
    }

    protected class MentionDto
    {
        public string Id { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
    }

    protected class AttachmentDto
    {
        public string Url { get; set; } = string.Empty;
        public string? Filename { get; set; }
        public string? ContentType { get; set; }
        [JsonIgnore] public ISharedImmediateTexture? Texture { get; set; }
    }

    protected class ChannelListDto
    {
        [JsonPropertyName("fc_chat")] public List<ChannelDto> Chat { get; set; } = new();
    }
}
