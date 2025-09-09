using System;
using System.Collections.Generic;
using ImGuiNET;

namespace DemiCat.UI;

public sealed class ButtonRowEditor
{
    private const int MaxRows   = 5;
    private const int MaxPerRow = 5;
    private const int MaxTotal  = 25;

    public class ButtonData
    {
        public string Label { get; set; } = string.Empty;
        public ButtonStyle Style { get; set; } = ButtonStyle.Secondary;
        public string Emoji { get; set; } = string.Empty;
        public int? MaxSignups { get; set; }
        public int? Width { get; set; }
        public int? Height { get; set; }
    }

    public enum ButtonStyle
    {
        Primary = 1,
        Secondary = 2,
        Success = 3,
        Danger = 4,
        Link = 5,
    }

    // Represents a row of buttons with metadata the plugin will later map to actual actions.
    private readonly List<List<ButtonData>> _rows;

    public ButtonRowEditor(List<List<ButtonData>> initial)
    {
        _rows = initial ?? new List<List<ButtonData>>();
        if (_rows.Count == 0) _rows.Add(new List<ButtonData>());
        Normalize();
    }

    public IReadOnlyList<IReadOnlyList<ButtonData>> Value => _rows;

    public void Draw(string id)
    {
        ImGui.PushID(id);

        int total = TotalCount();
        ImGui.Text($"Buttons: {total}/{MaxTotal}");

        for (int r = 0; r < _rows.Count; r++)
        {
            ImGui.PushID(r);
            ImGui.Separator();
            ImGui.Text($"Row {r + 1} ({_rows[r].Count}/{MaxPerRow})");

            // Add button input
            ImGui.SameLine();
            if (CanAddAny() && _rows[r].Count < MaxPerRow && ImGui.Button("+ Add"))
            {
                _rows[r].Add(new ButtonData { Label = "New Button" });
            }

            // Render each button as an editable input
            for (int i = 0; i < _rows[r].Count; i++)
            {
                ImGui.PushID(i);
                var button = _rows[r][i];
                var buf = button.Label;
                if (ImGui.InputText("##label", ref buf, 128))
                    button.Label = buf;

                ImGui.SameLine();
                if (ImGui.Button("Remove"))
                {
                    _rows[r].RemoveAt(i);
                    ImGui.PopID();
                    break;
                }

                ImGui.PopID();
            }

            // Row controls
            if (ImGui.Button("Remove Row") && _rows.Count > 1)
            {
                _rows.RemoveAt(r);
                ImGui.PopID();
                break;
            }

            ImGui.SameLine();
            if (_rows.Count < MaxRows && CanAddAny() && ImGui.Button("Add Row"))
            {
                _rows.Insert(r + 1, new List<ButtonData>());
            }

            ImGui.PopID();
        }

        ImGui.PopID();
        Normalize();
    }

    private int TotalCount()
    {
        int n = 0;
        foreach (var row in _rows) n += row.Count;
        return n;
    }

    private bool CanAddAny() => TotalCount() < MaxTotal;

    private void Normalize()
    {
        // Ensure at least one row, and each row <= MaxPerRow, total <= MaxTotal.
        if (_rows.Count == 0) _rows.Add(new List<ButtonData>());

        // Cap total rows
        if (_rows.Count > MaxRows)
            _rows.RemoveRange(MaxRows, _rows.Count - MaxRows);

        // Cap each row
        for (int r = 0; r < _rows.Count; r++)
        {
            if (_rows[r].Count > MaxPerRow)
                _rows[r].RemoveRange(MaxPerRow, _rows[r].Count - MaxPerRow);
        }

        // Trim overflow globally
        int overflow = Math.Max(0, TotalCount() - MaxTotal);
        if (overflow > 0)
        {
            for (int r = _rows.Count - 1; r >= 0 && overflow > 0; r--)
            {
                int take = Math.Min(_rows[r].Count, overflow);
                if (take > 0)
                {
                    _rows[r].RemoveRange(_rows[r].Count - take, take);
                    overflow -= take;
                }
            }
        }
    }
}
