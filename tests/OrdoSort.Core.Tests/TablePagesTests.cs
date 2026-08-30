namespace OrdoSort.Core.Tests;

public class TablePagesTests
{
    private static readonly Func<string, double> Measure = s => s.Length;

    private static List<List<string>> Table(params string[][] rows) =>
        rows.Select(r => r.ToList()).ToList();

    [Fact]
    public void ASmallTableIsOnePageWithColumnsSizedToTheirWidestCell()
    {
        var pages = TablePages.Paginate(Table(["id", "name"], ["1", "Alice"], ["2", "Bo"]),
            pageWidth: 100, pageHeight: 100, rowHeight: 10, Measure);
        var page = Assert.Single(pages);
        Assert.Equal(new[] { 0, 1 }, page.Columns);
        Assert.Equal(new[] { 2.0, 5.0 }, page.Widths);
        Assert.Equal(new[] { 1, 2 }, page.Rows);
    }

    [Fact]
    public void ATableTallerThanThePageSplitsWithEveryRowAppearingExactlyOnce()
    {
        List<List<string>> rows = [["h"]];
        for (var i = 0; i < 20; i++) rows.Add([$"r{i}"]);
        var pages = TablePages.Paginate(rows, 100, 100, 10, Measure);
        Assert.Equal(3, pages.Count);              // 9 body rows a page
        Assert.Equal(Enumerable.Range(1, 20), pages.SelectMany(p => p.Rows));
    }

    [Fact]
    public void EveryPageCarriesTheHeaderRow()
    {
        List<List<string>> rows = [["h"]];
        for (var i = 0; i < 20; i++) rows.Add([$"r{i}"]);
        Assert.All(TablePages.Paginate(rows, 100, 100, 10, Measure), p => Assert.Equal(0, p.HeaderRow));
    }

    [Fact]
    public void ATableWiderThanThePageSplitsIntoColumnGroups()
    {
        var wide = new string('x', 30);
        var pages = TablePages.Paginate(Table([wide, wide, wide, wide], [wide, wide, wide, wide]),
            100, 1000, 10, Measure);
        Assert.Equal(2, pages.Count);
        Assert.Equal(new[] { 0, 1, 2 }, pages[0].Columns);
        Assert.Equal(new[] { 3 }, pages[1].Columns);
    }

    [Fact]
    public void ATableBothTooTallAndTooWideGivesAPagePerGroupPerRowRange()
    {
        var wide = new string('x', 30);
        List<List<string>> rows = [[wide, wide, wide, wide]];
        for (var i = 0; i < 20; i++) rows.Add([wide, wide, wide, wide]);
        Assert.Equal(6, TablePages.Paginate(rows, 100, 100, 10, Measure).Count);
    }

    [Fact]
    public void AColumnWiderThanThePageStillGetsAPageToItself()
    {
        // The guard against an infinite loop: a group is never empty.
        var huge = new string('x', 500);
        var pages = TablePages.Paginate(Table([huge, "b"], [huge, "b"]), 100, 1000, 10, Measure);
        Assert.Equal(2, pages.Count);
        Assert.Equal(new[] { 0 }, pages[0].Columns);
    }

    [Fact]
    public void RaggedRowsArePaddedRatherThanCrashing()
    {
        var pages = TablePages.Paginate(Table(["a", "b", "c"], ["1"], ["1", "2", "3"]),
            1000, 1000, 10, Measure);
        Assert.Equal(3, Assert.Single(pages).Columns.Count);
    }

    [Fact]
    public void AnEmptyTableProducesNoPages() =>
        Assert.Empty(TablePages.Paginate(new List<List<string>>(), 100, 100, 10, Measure));

    [Fact]
    public void AHeaderOnlyTableStillProducesOnePage() =>
        Assert.Empty(Assert.Single(TablePages.Paginate(Table(["a", "b"]), 100, 100, 10, Measure)).Rows);
}
