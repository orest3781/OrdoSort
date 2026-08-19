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

    // ---- blank-row accounting (cycle 1) ----

    /// <summary>The point of the counts line: a paste with gaps should say
    /// how many gaps it closed. One blank row between two values is ONE
    /// removal — not two, even though splitting a CRLF pair on '\r' and '\n'
    /// separately yields two empty entries for it.</summary>
    [Fact]
    public void ABlankRowBetweenTwoItemsIsReportedAsOneRemoval()
    {
        var r = ListReformat.Reformat("a\r\n\r\nb", new ListReformat.Options());
        Assert.Equal("a,b", r.Text);
        Assert.Equal(2, r.Items);
        Assert.Equal(1, r.BlanksRemoved);
    }

    [Fact]
    public void SeveralBlankRowsInARowAreEachCounted()
    {
        var r = ListReformat.Reformat("a\n\n\n\nb", new ListReformat.Options());
        Assert.Equal(3, r.BlanksRemoved);
    }

    [Fact]
    public void ABlankRowBeforeTheFirstItemIsCounted()
    {
        var r = ListReformat.Reformat("\na\nb", new ListReformat.Options());
        Assert.Equal("a,b", r.Text);
        Assert.Equal(1, r.BlanksRemoved);
    }

    /// <summary>Excel ends a copied column with a trailing newline, and a
    /// dragged selection often runs past the data — those trailing empties
    /// are not gaps the user can see, so counting them would make every
    /// clean paste claim it removed a row.</summary>
    [Fact]
    public void TrailingBlankRowsAreNotCounted()
    {
        var r = ListReformat.Reformat("a\r\nb\r\n\r\n\r\n", new ListReformat.Options());
        Assert.Equal("a,b", r.Text);
        Assert.Equal(0, r.BlanksRemoved);
    }

    [Fact]
    public void APasteWithNoGapsReportsNoRemovals()
    {
        var r = ListReformat.Reformat("a\r\nb\r\nc", new ListReformat.Options());
        Assert.Equal(0, r.BlanksRemoved);
    }

    /// <summary>A pasted ROW with an empty cell in the middle: same rule,
    /// tabs delimit cells exactly as newlines do.</summary>
    [Fact]
    public void AnEmptyCellInAPastedRowIsCounted()
    {
        var r = ListReformat.Reformat("a\t\tb", new ListReformat.Options());
        Assert.Equal("a,b", r.Text);
        Assert.Equal(1, r.BlanksRemoved);
    }

    [Fact]
    public void AWhitespaceOnlyRowCountsAsABlankRow()
    {
        var r = ListReformat.Reformat("a\n   \nb", new ListReformat.Options());
        Assert.Equal(1, r.BlanksRemoved);
    }

    [Fact]
    public void AnAllBlankPasteReportsNoRemovalsBecauseEveryRowIsTrailing()
    {
        var r = ListReformat.Reformat("\n\n\n", new ListReformat.Options());
        Assert.Equal(new ListReformat.Result("", 0, 0, 0), r);
    }

    // ---- invisible-character scrub (cycle 2) ----

    /// <summary>The phantom empty row. A cell holding only a zero-width space
    /// survives string.Trim (U+200B is category Format, not whitespace), so it
    /// used to reach the output as an item you cannot see — a stray comma with
    /// nothing between it and the next one. Data pasted into a spreadsheet from
    /// a web page or a PDF is full of these.</summary>
    [Fact]
    public void ACellHoldingOnlyAZeroWidthSpaceIsABlankRow()
    {
        var r = ListReformat.Reformat("a\n​\nb", new ListReformat.Options());
        Assert.Equal("a,b", r.Text);
        Assert.Equal(2, r.Items);
        Assert.Equal(1, r.BlanksRemoved);
    }

    [Fact]
    public void ACellHoldingOnlyAByteOrderMarkIsABlankRow()
    {
        var r = ListReformat.Reformat("a\n﻿\nb", new ListReformat.Options());
        Assert.Equal("a,b", r.Text);
        Assert.Equal(1, r.BlanksRemoved);
    }

    [Fact]
    public void ACellHoldingOnlyASoftHyphenIsABlankRow()
    {
        var r = ListReformat.Reformat("a\n­\nb", new ListReformat.Options());
        Assert.Equal("a,b", r.Text);
        Assert.Equal(1, r.BlanksRemoved);
    }

    [Fact]
    public void ACellMixingSpacesAndZeroWidthCharactersIsABlankRow()
    {
        var r = ListReformat.Reformat("a\n ​ ﻿ \nb", new ListReformat.Options());
        Assert.Equal("a,b", r.Text);
        Assert.Equal(1, r.BlanksRemoved);
    }

    /// <summary>A BOM glued to the front of the first cell is the classic
    /// symptom of data that came through a UTF-8 file; left in place it makes
    /// the item silently fail every later exact-match. Trimming the ends is
    /// safe in a way that stripping throughout is not — see the next test.</summary>
    [Fact]
    public void InvisibleCharactersAreTrimmedFromBothEndsOfAKeptItem()
    {
        var r = ListReformat.Reformat("﻿Widget​\nGadget", new ListReformat.Options());
        Assert.Equal("Widget,Gadget", r.Text);
    }

    /// <summary>Interior format characters are left exactly as pasted: the
    /// same Unicode category carries the zero-width joiner that holds an emoji
    /// sequence together and the marks that set text direction, so scrubbing
    /// inside a real value would corrupt it.</summary>
    [Fact]
    public void AFormatCharacterInsideAKeptItemIsLeftAlone()
    {
        var r = ListReformat.Reformat("Wid‍get", new ListReformat.Options());
        Assert.Equal("Wid‍get", r.Text);
        Assert.Equal(1, r.Items);
    }

    [Fact]
    public void AnInputOfNothingButZeroWidthCharactersReturnsAnEmptyResult()
    {
        var r = ListReformat.Reformat("​\n﻿", new ListReformat.Options());
        Assert.Equal(new ListReformat.Result("", 0, 0, 0), r);
    }

    // ---- output shape (cycle 3) ----

    /// <summary>The whole point of the upgrade: paste a column with gaps in
    /// it, get the same column back with the gaps closed, ready to paste
    /// straight into the spreadsheet. CRLF, because that is what Excel reads
    /// back as one cell per row.</summary>
    [Fact]
    public void OnePerLineJoinsWithCrlfSoTheResultPastesBackAsAColumn()
    {
        var r = ListReformat.Reformat("a\n\nb\n\n\nc",
            new ListReformat.Options(Shape: ListReformat.OutputShape.OnePerLine));
        Assert.Equal("a\r\nb\r\nc", r.Text);
        Assert.Equal(3, r.Items);
        Assert.Equal(3, r.BlanksRemoved);
    }

    [Fact]
    public void OnePerLineIgnoresSpaceAfterSeparator()
    {
        var r = ListReformat.Reformat("a\nb", new ListReformat.Options(
            SpaceAfterComma: true, Shape: ListReformat.OutputShape.OnePerLine));
        Assert.Equal("a\r\nb", r.Text);
    }

    [Fact]
    public void OnePerLineStillQuotesEachItemWhenAsked()
    {
        var r = ListReformat.Reformat("a\nb", new ListReformat.Options(
            Quote: true, Shape: ListReformat.OutputShape.OnePerLine));
        Assert.Equal("'a'\r\n'b'", r.Text);
    }

    [Fact]
    public void CustomDelimiterJoinsWithTheGivenText()
    {
        var r = ListReformat.Reformat("a\nb\nc", new ListReformat.Options(
            Shape: ListReformat.OutputShape.CustomDelimiter, CustomDelimiter: "|"));
        Assert.Equal("a|b|c", r.Text);
    }

    [Fact]
    public void CustomDelimiterCanBeMoreThanOneCharacter()
    {
        var r = ListReformat.Reformat("a\nb", new ListReformat.Options(
            Shape: ListReformat.OutputShape.CustomDelimiter, CustomDelimiter: " OR "));
        Assert.Equal("a OR b", r.Text);
    }

    [Fact]
    public void CustomDelimiterHonoursSpaceAfterSeparator()
    {
        var r = ListReformat.Reformat("a\nb\nc", new ListReformat.Options(
            SpaceAfterComma: true, Shape: ListReformat.OutputShape.CustomDelimiter,
            CustomDelimiter: ";"));
        Assert.Equal("a; b; c", r.Text);
    }

    /// <summary>Verbatim means verbatim — an empty delimiter runs the items
    /// together rather than quietly falling back to a comma, so the output
    /// shows the user what they actually asked for.</summary>
    [Fact]
    public void AnEmptyCustomDelimiterRunsTheItemsTogether()
    {
        var r = ListReformat.Reformat("a\nb\nc", new ListReformat.Options(
            Shape: ListReformat.OutputShape.CustomDelimiter, CustomDelimiter: ""));
        Assert.Equal("abc", r.Text);
    }

    [Fact]
    public void ACustomDelimiterIsIgnoredWhileTheShapeIsACommaLine()
    {
        var r = ListReformat.Reformat("a\nb", new ListReformat.Options(CustomDelimiter: "|"));
        Assert.Equal("a,b", r.Text);
    }

    [Fact]
    public void TheDefaultShapeIsStillACommaLine()
    {
        Assert.Equal(ListReformat.OutputShape.CommaLine, new ListReformat.Options().Shape);
    }
}
