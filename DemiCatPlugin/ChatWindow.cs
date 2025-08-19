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
using Dalamud.Bindings.ImGui;

namespace DemiCatPlugin;

public class ChatWindow : IDisposable
{
    protected readonly Config _config;
    protected readonly HttpClient _httpClient;
    protected readonly List<ChatMessageDto> _messages = new();
    protected readonly List<string> _channels = new();
    protected int _selectedIndex;
    protected bool _channelsLoaded;
    protected bool _channelFetchFailed;
    protected string _channelId;
    protected string _input = string.Empty;
    protected bool _useCharacterName;
    protected string _statusMessage = string.Empty;
    private ClientWebSocket? _ws;
    private Task? _wsTask;
    private CancellationTokenSource? _wsCts;

    public bool ChannelsLoaded
    {
        get => _channelsLoaded;
        set => _channelsLoaded = value;
    }

    public ChatWindow(Config config, HttpClient httpClient)
    {
        _config = config;
        _httpClient = httpClient;
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

        if (_channels.Count > 0)
        {
            if (ImGui.Combo("Channel", ref _selectedIndex, _channels.ToArray(), _channels.Count))
            {
                _channelId = _channels[_selectedIndex];
                _config.ChatChannelId = _channelId;
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
            ImGui.TextWrapped($"{msg.AuthorName}: {FormatContent(msg)}");
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

    }

    public void SetChannels(List<string> channels)
    {
        _channels.Clear();
        _channels.AddRange(channels);
        if (!string.IsNullOrEmpty(_channelId))
        {
            _selectedIndex = _channels.IndexOf(_channelId);
            if (_selectedIndex < 0) _selectedIndex = 0;
        }
        if (_channels.Count > 0)
        {
            _channelId = _channels[_selectedIndex];
            _ = RefreshMessages();
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
        return text;
    }

    protected virtual async Task SendMessage()
    {
        if (string.IsNullOrWhiteSpace(_channelId) || string.IsNullOrWhiteSpace(_input))
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
        if (string.IsNullOrEmpty(_channelId))
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
                _messages.AddRange(msgs);
            });
        }
        catch (Exception ex)
        {
            PluginServices.Instance!.Log.Error(ex, "Error refreshing messages");
        }
    }

    public void Dispose()
    {
        _wsCts?.Cancel();
        _ws?.Dispose();
    }

    protected void SaveConfig()
    {
        PluginServices.Instance!.PluginInterface.SavePluginConfig(_config);
    }

    protected virtual async Task FetchChannels()
    {
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
                    ChatMessageDto? msg = null;
                    try
                    {
                        msg = JsonSerializer.Deserialize<ChatMessageDto>(json);
                    }
                    catch
                    {
                        // ignored
                    }
                    if (msg != null)
                    {
                        _ = PluginServices.Instance!.Framework.RunOnTick(() =>
                        {
                            if (msg.ChannelId == _channelId)
                            {
                                _messages.Add(msg);
                            }
                        });
                    }
                }
            }
            catch
            {
                // ignored - reconnect
            }
            finally
            {
                _ws?.Dispose();
                _ws = null;
            }

            try
            {
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

    protected class ChatMessageDto
    {
        public string Id { get; set; } = string.Empty;
        public string ChannelId { get; set; } = string.Empty;
        public string AuthorName { get; set; } = string.Empty;
        public string Content { get; set; } = string.Empty;
        public List<MentionDto>? Mentions { get; set; }
    }

    protected class MentionDto
    {
        public string Id { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
    }

    protected class ChannelListDto
    {
        [JsonPropertyName("fc_chat")] public List<string> Chat { get; set; } = new();
    }
}
