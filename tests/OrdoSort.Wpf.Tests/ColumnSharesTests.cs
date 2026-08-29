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
    public void WidthsThatFitExactlyTakeTheFittingBranchRatherThanBeingSplit()
    {
        // 100 + 200 == 300: the boundary between the two branches, where a
        // < instead of <= would needlessly split widths that already fit.
        var shares = ColumnShares.Compute(300, new[] { 100.0, 200.0 }, new[] { 20.0, 20.0 });
        Assert.Equal(new[] { 100.0, 200.0 }, shares);
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
        // Shares go by WANTED width — max(natural, floor) — so the first
        // column's first-pass share is 600 × 180/980 = 110, under its own
        // 180 floor: it is held at 180, and the 420 that leaves goes to the
        // other column rather than the 533 a straight 800/980 split would.
        var shares = ColumnShares.Compute(600, new[] { 100.0, 800.0 }, new[] { 180.0, 20.0 });
        Assert.Equal(180.0, shares[0], 6);
        Assert.Equal(420.0, shares[1], 6);
    }

    [Fact]
    public void EveryColumnUnderItsFloorIsHeldAndTheRestTakesWhatIsLeft()
    {
        // First pass: 400 × 150/1100 = 54.5 for each short column (their
        // wanted width is their floor) and 290.9 for the third. Both short
        // columns are under their 150 floors, so both are held in that one
        // pass, and the 100 left over is all the third can have.
        var shares = ColumnShares.Compute(400, new[] { 100.0, 100.0, 800.0 }, new[] { 150.0, 150.0, 20.0 });
        Assert.Equal(new[] { 150.0, 150.0, 100.0 }, shares.Select(s => Math.Round(s, 6)).ToArray());
    }

    [Fact]
    public void HoldingOneColumnCanPushAnotherUnderItsFloorInALaterPass()
    {
        // wanted = 700 / 300 / 300 / 5000 against 1000.
        // Pass 1: 111 / 47.6 / 47.6 / 793.7 — only the first is under its
        //         floor (700), so only it is held. The second clears its 40.
        // Pass 2: 300 left over 5600 of pool — the second is now 16.1, under
        //         the 40 it cleared a pass ago, so it is held too.
        // Pass 3: 260 left over 5300 of pool — 14.7 and 245.3, both above
        //         their floors, so the loop stops. This is the case a single
        //         proportional pass with floors clamped afterwards gets wrong.
        var shares = ColumnShares.Compute(
            1000, new[] { 10.0, 300.0, 300.0, 5000.0 }, new[] { 700.0, 40.0, 5.0, 10.0 });
        Assert.Equal(700.0, shares[0], 6);
        Assert.Equal(40.0, shares[1], 6);
        Assert.Equal(14.716981, shares[2], 6);
        Assert.Equal(245.283019, shares[3], 6);
        Assert.Equal(1000.0, shares.Sum(), 6);
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
