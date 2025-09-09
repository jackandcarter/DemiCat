using ImGuiNET;
using DemiCat.UI;

namespace DemiCatPlugin;

public static class ButtonRowsImGui
{
    public static void Draw(ButtonRows state, string id)
    {
        ImGui.PushID(id);

        ImGui.Text($"Buttons: {state.TotalCount}/{ButtonRows.MaxTotal}");

        for (int r = 0; r < state.Rows.Count; r++)
        {
            ImGui.PushID(r);
            ImGui.Separator();
            ImGui.Text($"Row {r + 1} ({state.Rows[r].Count}/{ButtonRows.MaxPerRow})");

            ImGui.SameLine();
            if (state.CanAddToRow(r) && ImGui.Button("+ Add"))
                state.AddButton(r);

            for (int c = 0; c < state.Rows[r].Count; c++)
            {
                ImGui.PushID(c);
                var buf = state.Rows[r][c].Label;
                if (ImGui.InputText("##label", ref buf, 128))
                    state.SetLabel(r, c, buf);

                ImGui.SameLine();
                if (ImGui.Button("Remove"))
                {
                    state.RemoveButton(r, c);
                    ImGui.PopID();
                    break;
                }
                ImGui.PopID();
            }

            if (ImGui.Button("Remove Row") && state.Rows.Count > 1)
            {
                state.RemoveRow(r);
                ImGui.PopID();
                break;
            }

            ImGui.SameLine();
            if (state.CanAddRow && ImGui.Button("Add Row"))
                state.AddRow(r);

            ImGui.PopID();
        }

        ImGui.PopID();
    }
}
