using OrdoSort.Wpf.Views;

namespace OrdoSort.Wpf.Tests;

/// <summary>The arithmetic DataGridColumnCap applies, proven without a
/// DataGrid: what each text column gets of the width the viewport has
/// left. "Natural" is a column's content width; a "floor" is its MinWidth.
/// Numbers are chosen so the expected shares are exact.</summary>
public class ColumnSharesTests
{
    [Fact]
    public void WhenTheWidthsFitEachColumnGetsItsNaturalWidth()
    {
        var shares = ColumnShares.Compute(1000, new[] { 300.0, 200.0 }, new[] { 20.0, 20.0 });
        Assert.Equal(new[] { 300.0, 200.0 }, shares);
    }

    [Fact]
    public void AFloorAboveTheNaturalWidthWinsEvenWhenEverythingFits()
    {
        var shares = ColumnShares.Compute(1000, new[] { 50.0, 200.0 }, new[] { 180.0, 20.0 });
        Assert.Equal(new[] { 180.0, 200.0 }, shares);
    }

    [Fact]
    public void WhenTheyDoNotFitTheShortfallIsSharedInProportion()
    {
        var shares = ColumnShares.Compute(600, new[] { 400.0, 800.0 }, new[] { 20.0, 20.0 });
        Assert.Equal(200.0, shares[0], 6);
        Assert.Equal(400.0, shares[1], 6);
    }

    [Fact]
    public void AColumnWhoseShareWouldFallUnderItsFloorIsHeldThereAndTheRestIsResplit()
    {
        // 600 × 100/900 = 67 for the first column, under its 180 floor: it
        // is held at 180 and the other gets what remains, not 600 × 800/900.
        var shares = ColumnShares.Compute(600, new[] { 100.0, 800.0 }, new[] { 180.0, 20.0 });
        Assert.Equal(180.0, shares[0], 6);
        Assert.Equal(420.0, shares[1], 6);
    }

    [Fact]
    public void HoldingOneColumnCanPushAnotherUnderItsFloorToo()
    {
        // First pass: 40 / 40 / 320 — both short columns fall under 150 and
        // are held; the 100 left goes to the third.
        var shares = ColumnShares.Compute(400, new[] { 100.0, 100.0, 800.0 }, new[] { 150.0, 150.0, 20.0 });
        Assert.Equal(new[] { 150.0, 150.0, 100.0 }, shares.Select(s => Math.Round(s, 6)).ToArray());
    }

    [Fact]
    public void FloorsThatAloneExceedTheWidthAreReturnedAsTheyAre()
    {
        // 300px of floors in 200px: the floors stand and the overflow is
        // WPF's to resolve (a horizontal scrollbar), exactly as the window's
        // own MinWidth is supposed to make impossible.
        var shares = ColumnShares.Compute(200, new[] { 500.0, 500.0 }, new[] { 150.0, 150.0 });
        Assert.Equal(new[] { 150.0, 150.0 }, shares);
    }

    [Fact]
    public void ColumnsWithNoContentSitAtTheirFloors()
    {
        var shares = ColumnShares.Compute(100, new[] { 0.0, 0.0 }, new[] { 20.0, 20.0 });
        Assert.Equal(new[] { 20.0, 20.0 }, shares);
    }

    [Fact]
    public void NoColumnsMeansNoShares()
    {
        Assert.Empty(ColumnShares.Compute(500, Array.Empty<double>(), Array.Empty<double>()));
    }

    [Fact]
    public void MismatchedListsAreRefused()
    {
        Assert.Throws<ArgumentException>(() =>
            ColumnShares.Compute(500, new[] { 1.0, 2.0 }, new[] { 1.0 }));
    }
}
