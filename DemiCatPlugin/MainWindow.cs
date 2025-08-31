using System;
using System.Net.Http;
using System.Numerics;
using Dalamud.Bindings.ImGui;

namespace DemiCatPlugin;

public class MainWindow : IDisposable
{
    private readonly Config _config;
    private readonly UiRenderer _ui;
    private readonly ChatWindow? _chat;
    private readonly OfficerChatWindow _officer;
    private readonly SettingsWindow _settings;
    private readonly PresenceSidebar? _presenceSidebar;
    private readonly EventCreateWindow _create;
    private readonly TemplatesWindow _templates;
    private readonly RequestBoardWindow _requestBoard;
    private readonly SyncshellWindow _syncshell;
    private readonly HttpClient _httpClient;

    public bool IsOpen;
    public bool HasOfficerRole { get; set; }
    public UiRenderer Ui => _ui;
    public EventCreateWindow EventCreateWindow => _create;

    public MainWindow(Config config, UiRenderer ui, ChatWindow? chat, OfficerChatWindow officer, SettingsWindow settings, PresenceSidebar? presenceSidebar, HttpClient httpClient)
    {
        _config = config;
        _ui = ui;
        _chat = chat;
        _officer = officer;
        _settings = settings;
        _presenceSidebar = presenceSidebar;
        _httpClient = httpClient;
        _create = new EventCreateWindow(config, httpClient);
        _templates = new TemplatesWindow(config, httpClient);
        _requestBoard = new RequestBoardWindow(config, httpClient);
        _syncshell = new SyncshellWindow(config, httpClient);
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

        if (ImGui.BeginTabBar("MainTabs"))
        {
            if (ImGui.BeginTabItem("Events"))
            {
                if (_config.EnableFcChat && _presenceSidebar != null)
                {
                    _presenceSidebar.Draw();
                    ImGui.SameLine();
                }
                ImGui.BeginChild("##eventsArea", ImGui.GetContentRegionAvail(), false);
                _ui.Draw();
                ImGui.EndChild();
                ImGui.EndTabItem();
            }

            if (ImGui.BeginTabItem("Create"))
            {
                _create.Draw();
                ImGui.EndTabItem();
            }

            if (ImGui.BeginTabItem("Templates"))
            {
                _templates.Draw();
                ImGui.EndTabItem();
            }

            if (ImGui.BeginTabItem("Request Board"))
            {
                _requestBoard.Draw();
                ImGui.EndTabItem();
            }

            if (_config.SyncEnabled && ImGui.BeginTabItem("Syncshell"))
            {
                _syncshell.Draw();
                ImGui.EndTabItem();
            }

            if (_config.EnableFcChat && _chat != null && ImGui.BeginTabItem("Chat"))
            {
                if (_presenceSidebar != null)
                {
                    _presenceSidebar.Draw();
                    ImGui.SameLine();
                }
                ImGui.BeginChild("##chatArea", ImGui.GetContentRegionAvail(), false);
                _chat.Draw();
                ImGui.EndChild();
                ImGui.EndTabItem();
            }

            if (HasOfficerRole && ImGui.BeginTabItem("Officer"))
            {
                if (_config.EnableFcChat && _presenceSidebar != null)
                {
                    _presenceSidebar.Draw();
                    ImGui.SameLine();
                }
                ImGui.BeginChild("##officerChatArea", ImGui.GetContentRegionAvail(), false);
                _officer.Draw();
                ImGui.EndChild();
                ImGui.EndTabItem();
            }

            ImGui.EndTabBar();
        }

        if (!_config.SyncEnabled)
        {
            ImGui.TextUnformatted("Feature in development");
        }

        ImGui.End();
        ImGui.PopStyleColor(5);
    }

    public void ResetEventCreateRoles()
    {
        _create.ResetRoles();
    }

    public void ReloadSignupPresets()
    {
        SignupPresetService.Reset();
        _ = SignupPresetService.EnsureLoaded(_httpClient, _config);
    }

    private void SaveConfig()
    {
        PluginServices.Instance!.PluginInterface.SavePluginConfig(_config);
    }

    public void Dispose()
    {
    }
}
