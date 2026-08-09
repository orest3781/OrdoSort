namespace OrdoSort.Core.Tests;

/// <summary>Task 4 (list reformatter tool). ListReformat.Reformat is pure
/// string work — no fixture/temp-dir setup needed, unlike FilenameListTests
/// or PageCountsTests.</summary>
public class ListReformatTests
{
    [Fact]
    public void CrlfSeparatedCellsBecomeSeparateItems()
    {
        var r = ListReformat.Reformat("a\r\nb\r\nc", new ListReformat.Options());
        Assert.Equal("a,b,c", r.Text);
        Assert.Equal(3, r.Items);
    }

    [Fact]
    public void LfSeparatedCellsBecomeSeparateItems()
    {
        var r = ListReformat.Reformat("a\nb\nc", new ListReformat.Options());
        Assert.Equal("a,b,c", r.Text);
        Assert.Equal(3, r.Items);
    }

    [Fact]
    public void BareCrSeparatedCellsBecomeSeparateItems()
    {
        var r = ListReformat.Reformat("a\rb\rc", new ListReformat.Options());
        Assert.Equal("a,b,c", r.Text);
        Assert.Equal(3, r.Items);
    }

    [Fact]
    public void TabSeparatedCellsBecomeSeparateItems()
    {
        // a pasted spreadsheet ROW, not a column
        var r = ListReformat.Reformat("a\tb\tc", new ListReformat.Options());
        Assert.Equal("a,b,c", r.Text);
        Assert.Equal(3, r.Items);
    }

    [Fact]
    public void MixedSeparatorsAllProduceItems()
    {
        var r = ListReformat.Reformat("a\tb\r\nc\nd\re", new ListReformat.Options());
        Assert.Equal("a,b,c,d,e", r.Text);
        Assert.Equal(5, r.Items);
    }

    [Fact]
    public void EachCellIsTrimmedOfSurroundingWhitespace()
    {
        var r = ListReformat.Reformat("  a  \n\tb\t\n c ", new ListReformat.Options());
        Assert.Equal("a,b,c", r.Text);
    }

    [Fact]
    public void BlankLinesAreDroppedNotKeptAsEmptyItems()
    {
        var r = ListReformat.Reformat("a\n\n\nb\n   \nc", new ListReformat.Options());
        Assert.Equal("a,b,c", r.Text);
        Assert.Equal(3, r.Items);
    }

    [Fact]
    public void DedupeDropsCaseInsensitiveDuplicatesKeepingTheFirstSpelling()
    {
        var r = ListReformat.Reformat("Widget\nwidget\nWIDGET\nGadget",
            new ListReformat.Options(Dedupe: true));
        Assert.Equal("Widget,Gadget", r.Text);
        Assert.Equal(2, r.Items);
        Assert.Equal(2, r.DuplicatesDropped);
    }

    [Fact]
    public void DedupeOffKeepsDuplicatesAndReportsZeroDropped()
    {
        var r = ListReformat.Reformat("a\na\na", new ListReformat.Options());
        Assert.Equal("a,a,a", r.Text);
        Assert.Equal(3, r.Items);
        Assert.Equal(0, r.DuplicatesDropped);
    }

    [Fact]
    public void QuoteWrapsEachItemInSingleQuotes()
    {
        var r = ListReformat.Reformat("a\nb", new ListReformat.Options(Quote: true));
        Assert.Equal("'a','b'", r.Text);
    }

    /// <summary>Plain wrap, not SQL escaping — an embedded apostrophe is left
    /// exactly as pasted, per ListReformat's own doc comment.</summary>
    [Fact]
    public void QuoteLeavesAnEmbeddedApostropheUntouched()
    {
        var r = ListReformat.Reformat("O'Brien", new ListReformat.Options(Quote: true));
        Assert.Equal("'O'Brien'", r.Text);
    }

    [Fact]
    public void SpaceAfterCommaAddsASpaceAfterEachSeparator()
    {
        var r = ListReformat.Reformat("a\nb\nc", new ListReformat.Options(SpaceAfterComma: true));
        Assert.Equal("a, b, c", r.Text);
    }

    [Fact]
    public void AllThreeTogglesCombine()
    {
        var r = ListReformat.Reformat("Widget\nwidget\nGadget",
            new ListReformat.Options(Quote: true, SpaceAfterComma: true, Dedupe: true));
        Assert.Equal("'Widget', 'Gadget'", r.Text);
        Assert.Equal(2, r.Items);
        Assert.Equal(1, r.DuplicatesDropped);
    }

    [Fact]
    public void NullInputReturnsAnEmptyResult()
    {
        var r = ListReformat.Reformat(null!, new ListReformat.Options());
        Assert.Equal(new ListReformat.Result("", 0, 0), r);
    }

    [Fact]
    public void EmptyInputReturnsAnEmptyResult()
    {
        var r = ListReformat.Reformat("", new ListReformat.Options());
        Assert.Equal(new ListReformat.Result("", 0, 0), r);
    }

    [Fact]
    public void WhitespaceOnlyInputReturnsAnEmptyResult()
    {
        var r = ListReformat.Reformat("   \n\t\r\n  ", new ListReformat.Options());
        Assert.Equal(new ListReformat.Result("", 0, 0), r);
    }

    [Fact]
    public void SingleItemHasNoTrailingSeparator()
    {
        var r = ListReformat.Reformat("solo", new ListReformat.Options());
        Assert.Equal("solo", r.Text);
        Assert.Equal(1, r.Items);
    }
}
