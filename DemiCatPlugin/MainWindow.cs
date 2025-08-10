using System.Collections.Generic;
using System.Net.Http;
using System.Text.Json;
using System.Numerics;
using Dalamud.Bindings.ImGui;

namespace DemiCatPlugin;

public class MainWindow
{
    private readonly Config _config;
    private readonly UiRenderer _ui;
    private readonly ChatWindow? _chat;
    private readonly OfficerChatWindow _officer;
    private readonly SettingsWindow _settings;
    private readonly EventCreateWindow _create;
    private readonly HttpClient _httpClient = new();
    private readonly List<string> _channels = new();
    private bool _channelsLoaded;
    private int _selectedIndex;
    private string _channelId;

    public bool IsOpen;
    public bool HasOfficerRole { get; set; }
    public bool HasChatRole { get; set; }

    public MainWindow(Config config, UiRenderer ui, ChatWindow? chat, OfficerChatWindow officer, SettingsWindow settings)
    {
        _config = config;
        _ui = ui;
        _chat = chat;
        _officer = officer;
        _settings = settings;
        _create = new EventCreateWindow(config);
        _channelId = config.EventChannelId;
        _ui.ChannelId = _channelId;
        _create.ChannelId = _channelId;
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
            FetchChannels();
        }
        if (_channels.Count > 0)
        {
            ImGui.SetNextItemWidth(-1);
            if (ImGui.Combo("Event", ref _selectedIndex, _channels.ToArray(), _channels.Count))
            {
                _channelId = _channels[_selectedIndex];
                _config.EventChannelId = _channelId;
                SaveConfig();
                _ui.ChannelId = _channelId;
                _ui.RefreshEmbeds();
                _create.ChannelId = _channelId;
            }
        }
        else
        {
            ImGui.TextUnformatted("No channels available");
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

            if (HasChatRole && _chat != null && ImGui.BeginTabItem("Chat"))
            {
                _chat.Draw();
                ImGui.EndTabItem();
            }

            if (HasOfficerRole && ImGui.BeginTabItem("Officer"))
            {
                _officer.Draw();
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

        ImGui.End();
        ImGui.PopStyleColor(5);
    }

    private void SaveConfig()
    {
        PluginServices.PluginInterface.SavePluginConfig(_config);
    }

    private async void FetchChannels()
    {
        _channelsLoaded = true;
        try
        {
            var response = await _httpClient.GetAsync($"{_config.HelperBaseUrl.TrimEnd('/')}/channels");
            if (!response.IsSuccessStatusCode)
            {
                return;
            }
            var stream = await response.Content.ReadAsStreamAsync();
            var dto = await JsonSerializer.DeserializeAsync<ChannelListDto>(stream) ?? new ChannelListDto();
            _channels.Clear();
            _channels.AddRange(dto.Event);
            if (!string.IsNullOrEmpty(_channelId))
            {
                _selectedIndex = _channels.IndexOf(_channelId);
                if (_selectedIndex < 0) _selectedIndex = 0;
            }
            if (_channels.Count > 0)
            {
                _channelId = _channels[_selectedIndex];
                _ui.ChannelId = _channelId;
                _create.ChannelId = _channelId;
            }
        }
        catch
        {
            // ignored
        }
    }

    private class ChannelListDto
    {
        public List<string> Event { get; set; } = new();
        public List<string> Chat { get; set; } = new();
    }
}
