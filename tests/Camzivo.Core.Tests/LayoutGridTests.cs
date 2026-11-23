using Camzivo.Core.Layout;
using Xunit;

namespace Camzivo.Core.Tests;

public class LayoutGridTests
{
    [Theory]
    [InlineData(LayoutPreset.Single, 1, 1)]
    [InlineData(LayoutPreset.Two, 1, 2)]
    [InlineData(LayoutPreset.Four, 2, 2)]
    [InlineData(LayoutPreset.Six, 2, 3)]
    [InlineData(LayoutPreset.Eight, 2, 4)]
    [InlineData(LayoutPreset.Nine, 3, 3)]
    [InlineData(LayoutPreset.Twelve, 3, 4)]
    [InlineData(LayoutPreset.Sixteen, 4, 4)]
    [InlineData(LayoutPreset.TwentyFive, 5, 5)]
    [InlineData(LayoutPreset.ThirtySix, 6, 6)]
    [InlineData(LayoutPreset.FortyNine, 7, 7)]
    [InlineData(LayoutPreset.SixtyFour, 8, 8)]
    public void EveryPresetProducesItsAdvertisedSlotCount(LayoutPreset preset, int rows, int columns)
    {
        var grid = LayoutGrid.FromPreset(preset);

        Assert.Equal(rows, grid.Rows);
        Assert.Equal(columns, grid.Columns);
        Assert.Equal((int)preset, grid.SlotCount);
        Assert.Equal(preset, grid.Preset);
    }

    [Fact]
    public void CustomGridIsNotMistakenForAPreset()
    {
        var grid = LayoutGrid.Create(3, 5);

        Assert.Equal(LayoutPreset.Custom, grid.Preset);
        Assert.Equal(15, grid.SlotCount);
    }

    [Fact]
    public void KnownGeometryIsRecognisedWhenBuiltDirectly()
    {
        Assert.Equal(LayoutPreset.Nine, LayoutGrid.Create(3, 3).Preset);
    }

    [Fact]
    public void CellsAreLaidOutInReadingOrder()
    {
        var grid = LayoutGrid.Create(2, 3);

        Assert.Equal(new LayoutCell(0, 0, 0), grid.Cells[0]);
        Assert.Equal(new LayoutCell(2, 0, 2), grid.Cells[2]);
        Assert.Equal(new LayoutCell(3, 1, 0), grid.Cells[3]);
        Assert.Equal(new LayoutCell(5, 1, 2), grid.Cells[5]);
    }

    [Fact]
    public void SlotRectsTileTheSurfaceWithoutGapsOrOverlap()
    {
        var grid = LayoutGrid.Create(4, 4);

        var area = Enumerable.Range(0, grid.SlotCount)
            .Select(grid.GetSlotRect)
            .Sum(rect => rect.Width * rect.Height);

        Assert.Equal(1d, area, 6);

        var first = grid.GetSlotRect(0);
        Assert.Equal(0d, first.X);
        Assert.Equal(0d, first.Y);
        Assert.Equal(0.25, first.Width, 6);

        var last = grid.GetSlotRect(15);
        Assert.Equal(0.75, last.X, 6);
        Assert.Equal(0.75, last.Y, 6);
    }

    [Fact]
    public void HitTestMapsPointsToTheCorrectSlot()
    {
        var grid = LayoutGrid.Create(3, 4);

        Assert.Equal(0, grid.HitTest(0.0, 0.0));
        Assert.Equal(11, grid.HitTest(0.99, 0.99));
        Assert.Equal(5, grid.HitTest(0.3, 0.5));
        Assert.Equal(-1, grid.HitTest(-0.01, 0.5));
        Assert.Equal(-1, grid.HitTest(0.5, 1.0));
    }

    [Theory]
    [InlineData(0, 3)]
    [InlineData(3, 0)]
    [InlineData(9, 1)]
    [InlineData(1, 9)]
    public void OutOfRangeDimensionsAreRejected(int rows, int columns)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => LayoutGrid.Create(rows, columns));
    }

    [Fact]
    public void SlotLookupOutsideTheGridIsRejected()
    {
        var grid = LayoutGrid.Create(2, 2);

        Assert.Throws<ArgumentOutOfRangeException>(() => grid.GetSlotRect(4));
        Assert.Throws<ArgumentOutOfRangeException>(() => grid.GetSlotRect(-1));
    }
}
