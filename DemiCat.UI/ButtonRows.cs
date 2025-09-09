using System;
using System.Collections.Generic;
using System.Linq;

namespace DemiCat.UI;

public sealed class ButtonRows
{
    public const int MaxRows   = 5;
    public const int MaxPerRow = 5;
    public const int MaxTotal  = 25;

    private readonly List<List<ButtonData>> _rows;

    public ButtonRows(List<List<ButtonData>> initial)
    {
        _rows = initial ?? new();
        if (_rows.Count == 0) _rows.Add(new());
        Normalize();
    }

    public IReadOnlyList<IReadOnlyList<ButtonData>> Rows => _rows;

    public int TotalCount => _rows.Sum(r => r.Count);

    public bool CanAddRow => _rows.Count < MaxRows && TotalCount < MaxTotal;
    public bool CanAddToRow(int row) =>
        row >= 0 && row < _rows.Count &&
        _rows[row].Count < MaxPerRow &&
        TotalCount < MaxTotal;

    public void AddRow(int afterRow)
    {
        if (!CanAddRow) return;
        if (afterRow < -1 || afterRow >= _rows.Count) afterRow = _rows.Count - 1;
        _rows.Insert(afterRow + 1, new());
        Normalize();
    }

    public void RemoveRow(int row)
    {
        if (_rows.Count <= 1) return;
        if (row < 0 || row >= _rows.Count) return;
        _rows.RemoveAt(row);
        Normalize();
    }

    public void AddButton(int row, ButtonData? data = null)
    {
        if (!CanAddToRow(row)) return;
        _rows[row].Add(data ?? new ButtonData { Label = "New Button" });
        Normalize();
    }

    public void RemoveButton(int row, int col)
    {
        if (row < 0 || row >= _rows.Count) return;
        if (col < 0 || col >= _rows[row].Count) return;
        _rows[row].RemoveAt(col);
        Normalize();
    }

    public void SetLabel(int row, int col, string newLabel)
    {
        if (row < 0 || row >= _rows.Count) return;
        if (col < 0 || col >= _rows[row].Count) return;
        _rows[row][col].Label = newLabel ?? string.Empty;
    }

    public IEnumerable<(int RowIndex, int ColIndex, ButtonData Data)> FlattenNonEmpty() =>
        _rows.SelectMany((row, r) => row
            .Select((data, c) => (RowIndex: r, ColIndex: c, Data: data))
            .Where(x => !string.IsNullOrWhiteSpace(x.Data.Label)));

    private void Normalize()
    {
        if (_rows.Count == 0) _rows.Add(new());

        // cap rows
        if (_rows.Count > MaxRows)
            _rows.RemoveRange(MaxRows, _rows.Count - MaxRows);

        // cap each row
        for (int r = 0; r < _rows.Count; r++)
            if (_rows[r].Count > MaxPerRow)
                _rows[r].RemoveRange(MaxPerRow, _rows[r].Count - MaxPerRow);

        // cap global total (trim from end)
        int overflow = Math.Max(0, TotalCount - MaxTotal);
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
