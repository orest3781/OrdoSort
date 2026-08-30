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
    public void APageWithRoomForBarelyOneRowStillMakesProgressInsteadOfLoopingForever()
    {
        // 15pt of page against 10pt rows leaves room for one row after the
        // header, and (int)(15/10) - 1 == 0 without the floor — a loop that
        // never advances.
        List<List<string>> rows = [["h"]];
        for (var i = 0; i < 3; i++) rows.Add([$"r{i}"]);
        var pages = TablePages.Paginate(rows, pageWidth: 100, pageHeight: 15, rowHeight: 10,
            measure: s => s.Length);
        Assert.Equal(3, pages.Count);                              // one body row each
        Assert.Equal(Enumerable.Range(1, 3), pages.SelectMany(p => p.Rows));
    }

    [Fact]
    public void AColumnWiderThanThePageStillGetsAPageToItself()
    {
        // A group is never empty — a page with zero columns would show
        // nothing. (Unlike bodyRowsPerPage above, this loop is bounded by
        // columnCount and can't run forever regardless; see
        // APageWithRoomForBarelyOneRowStillMakesProgressInsteadOfLoopingForever
        // for the fact covering the real infinite-loop guard.)
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

    [Fact]
    public void RepeatHeaderFalseKeepsRowZeroAsOrdinaryBodyContent()
    {
        // TextToPdf's reason for repeatHeader: a text file's first line is
        // content, not a heading. Ten rows at ten rows-per-page fit on one
        // page ONLY if row 0 is counted as body — the header-repeating
        // default would drop it (treating it as the heading) and reserve a
        // row for a header line that does not exist here, leaving 9.
        var rows = Enumerable.Range(0, 10).Select(i => new List<string> { $"r{i}" }).ToList();
        var page = Assert.Single(TablePages.Paginate(rows, 100, 100, 10, Measure, repeatHeader: false));
        Assert.Equal(Enumerable.Range(0, 10), page.Rows);
    }
}
