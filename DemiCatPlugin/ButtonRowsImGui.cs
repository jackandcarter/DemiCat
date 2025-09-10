using Dalamud.Bindings.ImGui;
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

                var width = state.Rows[r][c].Width ?? 0;
                if (ImGui.InputInt("Width", ref width))
                    state.Rows[r][c].Width = width > 0 ? Math.Min(width, ButtonSizeHelper.Max) : null;
                ImGui.SameLine();
                var autoW = ButtonSizeHelper.ComputeWidth(state.Rows[r][c].Label);
                ImGui.Text($"Auto: {autoW}");

                var height = state.Rows[r][c].Height ?? 0;
                if (ImGui.InputInt("Height", ref height))
                    state.Rows[r][c].Height = height > 0 ? Math.Min(height, ButtonSizeHelper.Max) : null;
                ImGui.SameLine();
                ImGui.Text($"Auto: {ButtonSizeHelper.DefaultHeight}");

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
