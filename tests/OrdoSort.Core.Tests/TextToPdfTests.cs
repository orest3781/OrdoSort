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
