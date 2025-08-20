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
    private readonly EventCreateWindow _create;
    private readonly TemplatesWindow _templates;

    public bool IsOpen;
    public bool HasOfficerRole { get; set; }
    public UiRenderer Ui => _ui;

    public MainWindow(Config config, UiRenderer ui, ChatWindow? chat, OfficerChatWindow officer, SettingsWindow settings, HttpClient httpClient)
    {
        _config = config;
        _ui = ui;
        _chat = chat;
        _officer = officer;
        _settings = settings;
        _create = new EventCreateWindow(config, httpClient);
        _templates = new TemplatesWindow(config, httpClient);
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
                _ui.Draw();
                ImGui.EndTabItem();
            }

            if (HasOfficerRole && ImGui.BeginTabItem("Create"))
            {
                _create.Draw();
                ImGui.EndTabItem();
            }

            if (HasOfficerRole && ImGui.BeginTabItem("Templates"))
            {
                _templates.Draw();
                ImGui.EndTabItem();
            }

            if (_config.EnableFcChat && _chat != null && ImGui.BeginTabItem("Chat"))
            {
                _chat.Draw();
                ImGui.EndTabItem();
            }

            if (HasOfficerRole && ImGui.BeginTabItem("Officer"))
            {
                _officer.Draw();
                ImGui.EndTabItem();
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

    private void SaveConfig()
    {
        PluginServices.Instance!.PluginInterface.SavePluginConfig(_config);
    }

    public void Dispose()
    {
    }
}
