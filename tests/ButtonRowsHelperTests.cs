using System.Collections.Generic;
using System.Linq;
using DemiCat.UI;
using Xunit;

public class ButtonRowsHelperTests
{
    [Fact]
    public void NormalizesMaxPerRowAndTotal()
    {
        var data = Enumerable.Range(0, 10)
            .Select(_ => Enumerable.Range(0, 10).Select(i => new ButtonData { Label = $"Btn {i}" }).ToList())
            .ToList();
        var rows = new ButtonRows(data);
        Assert.Equal(ButtonRows.MaxRows, rows.Rows.Count);
        Assert.All(rows.Rows, r => Assert.True(r.Count <= ButtonRows.MaxPerRow));
        Assert.Equal(ButtonRows.MaxTotal, rows.TotalCount);
    }

    [Fact]
    public void MakeCustomId_CapsLengthAndRemainsUnique()
    {
        var longLabel = new string('a', 200);
        var id1 = IdHelpers.MakeCustomId(longLabel, 0, 0);
        Assert.True(id1.Length <= 100);

        var label2 = new string('a', 199) + "b";
        var id2 = IdHelpers.MakeCustomId(label2, 0, 0);
        Assert.True(id2.Length <= 100);
        Assert.NotEqual(id1, id2);
    }

    [Fact]
    public void MakeCustomId_IncludesRowAndColInHash()
    {
        var label = "Click";
        var id1 = IdHelpers.MakeCustomId(label, 0, 0);
        var id2 = IdHelpers.MakeCustomId(label, 0, 1);
        var id3 = IdHelpers.MakeCustomId(label, 1, 0);
        Assert.NotEqual(id1, id2);
        Assert.NotEqual(id1, id3);
        Assert.NotEqual(id2, id3);
    }
}
