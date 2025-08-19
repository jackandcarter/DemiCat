using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Numerics;
using System.Threading.Tasks;
using System.Linq;
using Dalamud.Bindings.ImGui;

namespace DemiCatPlugin;

public class MainWindow : IDisposable
{
    private readonly Config _config;
    private readonly UiRenderer _ui;
    private readonly ChatWindow? _chat;
    private readonly OfficerChatWindow _officer;
    private readonly SettingsWindow _settings;
    private readonly EventCreateWindow _create;
    private readonly TemplatesWindow _templates;
    private readonly PresencePane _presence;
    private readonly HttpClient _httpClient;
    private readonly List<ChannelDto> _channels = new();
    private readonly List<ChannelDto> _fcChatChannels = new();
    private readonly List<ChannelDto> _officerChatChannels = new();
    private bool _channelsLoaded;
    private bool _channelFetchFailed;
    private int _selectedIndex;
    private string _channelId;

    public bool IsOpen;
    public bool HasOfficerRole { get; set; }

    public bool ChannelsLoaded
    {
        get => _channelsLoaded;
        set => _channelsLoaded = value;
    }

    public MainWindow(Config config, UiRenderer ui, ChatWindow? chat, OfficerChatWindow officer, SettingsWindow settings, HttpClient httpClient)
    {
        _config = config;
        _ui = ui;
        _chat = chat;
        _officer = officer;
        _settings = settings;
        _httpClient = httpClient;
        _create = new EventCreateWindow(config, httpClient);
        _templates = new TemplatesWindow(config, httpClient);
        _presence = new PresencePane(config, httpClient);
        _channelId = config.EventChannelId;
        _ui.ChannelId = _channelId;
        _create.ChannelId = _channelId;
        _templates.ChannelId = _channelId;
    }

    public void Draw()
    {
        if (!IsOpen)
        {
            return;
        }

        ImGui.SetNextWindowSize(new Vector2(800, 600), ImGuiCond.FirstUseEver);
        ImGui.PushStyleColor(ImGuiCol.WindowBg, new Vector4(0.11f, 0.11f, 0.12f, 1f));
        ImGui.PushStyleColor(ImGuiCol.ChildBg, new Vector4(0.09f, 0.09f, 0.1f, 1f));
        ImGui.PushStyleColor(ImGuiCol.Tab, new Vector4(0.15f, 0.15f, 0.16f, 1f));
        ImGui.PushStyleColor(ImGuiCol.TabActive, new Vector4(0.2f, 0.2f, 0.21f, 1f));
        ImGui.PushStyleColor(ImGuiCol.TabHovered, new Vector4(0.25f, 0.25f, 0.26f, 1f));

        if (!ImGui.Begin("DemiCat", ref IsOpen, ImGuiWindowFlags.NoCollapse))
        {
            ImGui.End();
            ImGui.PopStyleColor(5);
            return;
        }

        var padding = ImGui.GetStyle().FramePadding;
        var buttonSize = ImGui.GetFrameHeight();
        var cursor = ImGui.GetCursorPos();
        ImGui.SetCursorPos(new Vector2(ImGui.GetWindowContentRegionMax().X - buttonSize - padding.X, padding.Y));
        if (ImGui.Button("\u2699"))
        {
            _settings.IsOpen = true;
        }
        ImGui.SetCursorPos(cursor);

        ImGui.BeginChild("ChannelList", new Vector2(150, 0), true);
        if (!_channelsLoaded)
        {
            _ = FetchChannels();
        }
        if (_channels.Count > 0)
        {
            ImGui.SetNextItemWidth(-1);
            var channelNames = _channels.Select(c => c.Name).ToArray();
            if (ImGui.Combo("Event", ref _selectedIndex, channelNames, channelNames.Length))
            {
                _channelId = _channels[_selectedIndex].Id;
                _config.EventChannelId = _channelId;
                SaveConfig();
                _ui.ChannelId = _channelId;
                _ = _ui.RefreshEmbeds();
                _create.ChannelId = _channelId;
                _templates.ChannelId = _channelId;
            }
        }
        else
        {
            ImGui.TextUnformatted(_channelFetchFailed ? "Failed to load channels" : "No channels available");
        }
        ImGui.EndChild();

        ImGui.SameLine();

        ImGui.BeginChild("MainContent", new Vector2(0, 0), false);
        if (ImGui.BeginTabBar("MainTabs"))
        {
            if (ImGui.BeginTabItem("Events"))
            {
                _ui.Draw();
                ImGui.EndTabItem();
            }

            if (_chat != null && ImGui.BeginTabItem("Chat"))
            {
                _chat.Draw();
                ImGui.EndTabItem();
            }

            if (HasOfficerRole && ImGui.BeginTabItem("Officer"))
            {
                _officer.Draw();
                ImGui.EndTabItem();
            }

            if (HasOfficerRole && ImGui.BeginTabItem("Templates"))
            {
                _templates.ChannelId = _channelId;
                _templates.Draw();
                ImGui.EndTabItem();
            }

            if (HasOfficerRole && ImGui.BeginTabItem("Create"))
            {
                _create.ChannelId = _channelId;
                _create.Draw();
                ImGui.EndTabItem();
            }

            ImGui.EndTabBar();
        }
        ImGui.EndChild();

        ImGui.SameLine();
        _presence.Draw();

        ImGui.End();
        ImGui.PopStyleColor(5);
    }

    private void SaveConfig()
    {
        PluginServices.Instance!.PluginInterface.SavePluginConfig(_config);
    }

    public void Dispose()
    {
        _presence.Dispose();
    }

    private async Task FetchChannels()
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
            ResolveChannelNames(dto.Event);
            ResolveChannelNames(dto.FcChat);
            ResolveChannelNames(dto.OfficerChat);
            ResolveChannelNames(dto.OfficerVisible);
            _ = PluginServices.Instance!.Framework.RunOnTick(() =>
            {
                _channels.Clear();
                _channels.AddRange(dto.Event);
                _fcChatChannels.Clear();
                _fcChatChannels.AddRange(dto.FcChat);
                _officerChatChannels.Clear();
                _officerChatChannels.AddRange(dto.OfficerChat);
                _chat?.SetChannels(_fcChatChannels);
                if (_chat != null) _chat.ChannelsLoaded = true;
                _officer.SetChannels(_officerChatChannels);
                _officer.ChannelsLoaded = true;
                if (!string.IsNullOrEmpty(_channelId))
                {
                    _selectedIndex = _channels.FindIndex(c => c.Id == _channelId);
                    if (_selectedIndex < 0) _selectedIndex = 0;
                }
                if (_channels.Count > 0)
                {
                    _channelId = _channels[_selectedIndex].Id;
                    _ui.ChannelId = _channelId;
                    _create.ChannelId = _channelId;
                    _templates.ChannelId = _channelId;
                }

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

    private static void ResolveChannelNames(List<ChannelDto> channels)
    {
        foreach (var c in channels)
        {
            if (string.IsNullOrWhiteSpace(c.Name))
            {
                PluginServices.Instance!.Log.Warning($"Channel name missing for {c.Id}; using ID as fallback.");
                c.Name = c.Id;
            }
        }
    }

    private class ChannelListDto
    {
        [JsonPropertyName("event")] public List<ChannelDto> Event { get; set; } = new();
        [JsonPropertyName("fc_chat")] public List<ChannelDto> FcChat { get; set; } = new();
        [JsonPropertyName("officer_chat")] public List<ChannelDto> OfficerChat { get; set; } = new();
        [JsonPropertyName("officer_visible")] public List<ChannelDto> OfficerVisible { get; set; } = new();
    }
}
