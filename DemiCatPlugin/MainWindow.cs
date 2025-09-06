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
        _syncshell = config.FCSyncShell ? new SyncshellWindow(config, httpClient) : null;
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

        var linked = TokenManager.Instance?.State == LinkState.Linked;
        if (!linked)
        {
            ImGui.TextColored(new Vector4(1f, 0.85f, 0f, 1f), "Link DemiCat: run `/demibot embed` in Discord and paste the key.");
            ImGui.Separator();
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
                if (_config.SyncedChat && _presenceSidebar != null)
                {
                    if (!linked)
                    {
                        ImGui.BeginDisabled();
                        ImGui.BeginChild("##presence", new Vector2(150, 0), true);
                        ImGui.TextUnformatted("Presence");
                        ImGui.EndChild();
                        ImGui.EndDisabled();
                        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
                            ImGui.SetTooltip("Link DemiCat to show presence.");
                        ImGui.SameLine();
                    }
                    else
                    {
                        _presenceSidebar.Draw();
                        ImGui.SameLine();
                    }
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

            if (ImGui.BeginTabItem("Syncshell"))
            {
                if (_config.FCSyncShell && _syncshell != null)
                {
                    _syncshell.Draw();
                }
                else
                {
                    ImGui.TextUnformatted("Feature disabled");
                }
                ImGui.EndTabItem();
            }

            if (_chat != null)
            {
                if (!linked)
                {
                    ImGui.BeginDisabled();
                    ImGui.TabItemButton("Chat");
                    ImGui.EndDisabled();
                    if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
                        ImGui.SetTooltip("Link DemiCat to use chat.");
                }
                else if (ImGui.BeginTabItem("Chat"))
                {
                    ImGui.BeginChild("##chatArea", ImGui.GetContentRegionAvail(), false);
                    _chat.Draw();
                    ImGui.EndChild();
                    ImGui.EndTabItem();
                }
            }

            if (HasOfficerRole)
            {
                if (!linked)
                {
                    ImGui.BeginDisabled();
                    ImGui.TabItemButton("Officer");
                    ImGui.EndDisabled();
                    if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
                        ImGui.SetTooltip("Link DemiCat to use officer chat.");
                }
                else if (ImGui.BeginTabItem("Officer"))
                {
                    if (_config.SyncedChat && _presenceSidebar != null)
                    {
                        _presenceSidebar.Draw();
                        ImGui.SameLine();
                    }
                    ImGui.BeginChild("##officerChatArea", ImGui.GetContentRegionAvail(), false);
                    _officer.Draw();
                    ImGui.EndChild();
                    ImGui.EndTabItem();
                }
            }

            ImGui.EndTabBar();
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
