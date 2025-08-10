using System.Numerics;
using Dalamud.Bindings.ImGui;

namespace DemiCatPlugin;

public class MainWindow
{
    private readonly Config _config;
    private readonly UiRenderer _ui;
    private readonly ChatWindow _chat;
    private readonly SettingsWindow _settings;

    public bool IsOpen;

    public MainWindow(Config config, UiRenderer ui, ChatWindow chat, SettingsWindow settings)
    {
        _config = config;
        _ui = ui;
        _chat = chat;
        _settings = settings;
        IsOpen = true;
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
        ImGui.TextUnformatted("Channels");
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

            if (ImGui.BeginTabItem("Chat"))
            {
                _chat.Draw();
                ImGui.EndTabItem();
            }

            ImGui.EndTabBar();
        }
        ImGui.EndChild();

        ImGui.End();
        ImGui.PopStyleColor(5);
    }
}
