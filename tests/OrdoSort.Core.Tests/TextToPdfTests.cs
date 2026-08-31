using PdfSharp.Pdf;
using PdfSharp.Pdf.IO;

namespace OrdoSort.Core.Tests;

public class TextToPdfTests
{
    private static readonly TextToPdf Converter = new();

    private static int PageCountOf(byte[] pdf)
    {
        using var stream = new MemoryStream(pdf);
        using var doc = PdfReader.Open(stream, PdfDocumentOpenMode.Import);
        return doc.PageCount;
    }

    [Theory]
    [InlineData("txt", true)] [InlineData("log", true)] [InlineData("md", true)] [InlineData("json", true)]
    [InlineData("TXT", true)]
    [InlineData("docx", false)] [InlineData("pdf", false)] [InlineData("csv", false)]
    public void HandlesOnlyTextTypesNotSpreadsheetsOrDocuments(string extension, bool handled) =>
        Assert.Equal(handled, Converter.Handles(extension));

    [Fact]
    public void ATextFileBecomesAReadablePdf()
    {
        var r = Converter.ToPdf(System.Text.Encoding.UTF8.GetBytes("line one\nline two\n"),
            "notes.txt", Array.Empty<string>(), null);
        Assert.Equal("ok", r.Status);
        Assert.Equal(1, PageCountOf(r.Pdf!));
    }

    /// <summary>Review follow-up (2026-08-31): WrapLine's own upfront
    /// `measure(line) &lt;= maxWidth` shortcut was removed as part of the
    /// binary-search fix, on the reasoning that the main loop reaches the
    /// same one-chunk result on its own -- true for every non-empty line,
    /// false for the empty string, since `while (start &lt; line.Length)`
    /// never runs at all when the line has zero characters. Every blank
    /// line in a converted file is exactly that empty string (ToPdf splits
    /// on '\n' with no RemoveEmptyEntries), so the bug silently deleted
    /// every blank line — collapsing a converted .md into a wall of text,
    /// since blank lines ARE Markdown's paragraph separators.
    ///
    /// TablePages' own body-rows-per-page for TextToPdf's geometry is
    /// (int)(792 / 14) = 56, so a document of more than 56 LINES needs a
    /// second page. Two real paragraph lines alone fit comfortably on one
    /// page regardless of anything else in the file; padding the file out
    /// past the 56-line threshold using ONLY blank lines is what makes the
    /// PAGE COUNT ITSELF the proof that every blank line survived as its
    /// own row, not merely that the file converted at all — if a blank line
    /// ever again turned into zero rows instead of one, the effective line
    /// count would silently fall back to 2 and this would stay on a single
    /// page.</summary>
    [Fact]
    public void BlankLinesBetweenParagraphsArePreservedNotCollapsed()
    {
        var lines = new List<string> { "paragraph one" };
        lines.AddRange(Enumerable.Repeat("", 60));
        lines.Add("paragraph two");
        var text = string.Join("\n", lines);   // 62 lines total, 60 of them blank

        var r = new TextToPdf().ToPdf(System.Text.Encoding.UTF8.GetBytes(text),
            "notes.md", Array.Empty<string>(), null);

        Assert.Equal("ok", r.Status);
        Assert.True(PageCountOf(r.Pdf!) > 1,
            $"62 lines (60 of them blank) should need a second page if every blank line is its own row; got {PageCountOf(r.Pdf!)}");
    }

    /// <summary>The other half of the same bug: WITHOUT the empty-line
    /// short-circuit, WrapLine("") returned [] rather than [""], so a file
    /// of nothing but blank lines flattened (SelectMany) into a completely
    /// empty table, TablePages.Paginate paginated that to ZERO pages, and
    /// Render then failed inside PdfSharp's own document.Save on a
    /// page-less PdfDocument — reported as "couldn't lay it out", an
    /// ERROR, not the successful (if visually blank) conversion this file
    /// deserves.</summary>
    [Fact]
    public void AFileOfOnlyBlankLinesConvertsRatherThanErroring()
    {
        var r = new TextToPdf().ToPdf(System.Text.Encoding.UTF8.GetBytes("\n\n\n"),
            "blanks.txt", Array.Empty<string>(), null);
        Assert.Equal("ok", r.Status);
    }

    [Fact]
    public void AVeryLongLineWrapsRatherThanRunningOffThePage()
    {
        var line = new string('w', 5000);
        var r = new TextToPdf().ToPdf(System.Text.Encoding.UTF8.GetBytes(line),
            "wide.txt", Array.Empty<string>(), null);
        Assert.Equal("ok", r.Status);
        // The paginator is column-based, so a single 5000-char "column" would
        // otherwise place the WHOLE line as one row on one page, however
        // absurdly wide that row measures — PdfSharp does not reject an
        // oversized DrawString rect, so a missing hard-wrap does not throw;
        // it just draws off the page. More than one page is what actually
        // proves the line was split into several rows first, rather than
        // merely that nothing crashed.
        var pages = PageCountOf(r.Pdf!);
        Assert.True(pages > 1, $"a 5000-char line should hard-wrap into several rows/pages, got {pages}");
    }

    [Fact]
    public void AVeryLongLineAmongOrdinaryOnesWrapsWithoutDisturbingItsNeighbours()
    {
        var longLine = new string('w', 5000);
        var text = string.Join("\n", "before", longLine, "after");
        var r = new TextToPdf().ToPdf(System.Text.Encoding.UTF8.GetBytes(text),
            "mixed.log", Array.Empty<string>(), null);
        Assert.Equal("ok", r.Status);
        Assert.True(PageCountOf(r.Pdf!) > 1);
    }

    /// <summary>Review Minor 3: the old WrapLine grew a chunk one character
    /// at a time, re-measuring the whole prefix on every step — roughly one
    /// MeasureString call per CHARACTER of the line. ".json" and ".log" are
    /// default-on text types, and a minified JSON export is routinely one
    /// multi-megabyte line with no newlines at all, which turned this into
    /// tens of seconds to minutes of an unresponsive-looking "Merging" that
    /// Cancel() cannot interrupt (checked only between units, and this all
    /// happens inside one). The bound below is deliberately generous — this
    /// pins "did not regress back to a per-character scan", not a tight
    /// performance target — but still comfortably below what the reverted
    /// linear scan takes on a line this size on ordinary hardware.</summary>
    [Fact]
    public void AMultiMegabyteSingleLineWrapsQuickly()
    {
        var line = new string('w', 1_500_000);
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        var r = new TextToPdf().ToPdf(System.Text.Encoding.UTF8.GetBytes(line),
            "huge.json", Array.Empty<string>(), null);
        stopwatch.Stop();

        Assert.Equal("ok", r.Status);
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(20),
            $"wrapping a {line.Length:N0}-character single line took {stopwatch.Elapsed} — " +
            "the binary-search wrap must not regress to one MeasureString call per character");
    }

    [Fact]
    public void ALongTextFileRunsToSeveralPages()
    {
        var text = string.Join("\n", Enumerable.Range(0, 500).Select(i => $"line {i}"));
        var r = new TextToPdf().ToPdf(System.Text.Encoding.UTF8.GetBytes(text),
            "long.log", Array.Empty<string>(), null);
        Assert.True(PageCountOf(r.Pdf!) > 5);
    }

    [Fact]
    public void AnEmptyFileIsAnErrorNotAnEmptyPdf()
    {
        var r = Converter.ToPdf(Array.Empty<byte>(), "empty.txt", Array.Empty<string>(), null);
        Assert.Equal("error", r.Status);
        Assert.Contains("nothing", r.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void AWordDocumentIsNotItsToConvert() =>
        Assert.Equal("unsupported",
            Converter.ToPdf([1, 2, 3], "letter.docx", Array.Empty<string>(), null).Status);

    [Fact]
    public void ItNeverPrompts()
    {
        // There is nothing password-protectable about plain text; unlike
        // TableToPdf's xlsx path there is no format here that could even
        // claim to need one.
        var asked = false;
        new TextToPdf().ToPdf(System.Text.Encoding.UTF8.GetBytes("hello"),
            "x.txt", ["pw"], _ => { asked = true; return "pw"; });
        Assert.False(asked);
    }

    [Fact]
    public void PagesComeOutPortraitNotTablesLandscape()
    {
        // Judgement call #4 (page orientation): prose reads top-to-bottom,
        // so text gets its own portrait constants rather than sharing
        // TableToPdf's landscape ones.
        var r = Converter.ToPdf(System.Text.Encoding.UTF8.GetBytes("hello"),
            "x.txt", Array.Empty<string>(), null);
        using var stream = new MemoryStream(r.Pdf!);
        using var doc = PdfReader.Open(stream, PdfDocumentOpenMode.Import);
        Assert.Equal(TextToPdf.PageWidthPt, doc.Pages[0].Width.Point, 1);
        Assert.Equal(TextToPdf.PageHeightPt, doc.Pages[0].Height.Point, 1);
        Assert.True(TextToPdf.PageHeightPt > TextToPdf.PageWidthPt, "a text page should be portrait");
    }
}
