using System.IO.Compression;
using PdfSharp.Pdf;
using PdfSharp.Pdf.IO;

namespace OrdoSort.Core.Tests;

public class TableToPdfTests
{
    private static readonly TableToPdf Converter = new();

    private static int PageCountOf(byte[] pdf)
    {
        using var stream = new MemoryStream(pdf);
        using var doc = PdfReader.Open(stream, PdfDocumentOpenMode.Import);
        return doc.PageCount;
    }

    // No helper in this test project returns xlsx bytes (rather than a path)
    // or builds more than one sheet — XlsxTableTests.WriteXlsx is private,
    // path-based and single-sheet-only — so this is a new, minimal one, local
    // to this file. No workbook.xml/rels: XlsxTable.Read and CountSheets both
    // fall back to scanning xl/worksheets/sheet*.xml directly, the same
    // fallback WriteXlsx itself already relies on.
    private const string XlsxNs = "xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\"";

    private static byte[] XlsxBytes(int sheetCount)
    {
        using var stream = new MemoryStream();
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            for (var i = 1; i <= sheetCount; i++)
            {
                using var w = new StreamWriter(zip.CreateEntry($"xl/worksheets/sheet{i}.xml").Open(),
                    System.Text.Encoding.UTF8);
                w.Write($"<worksheet {XlsxNs}><sheetData><row r=\"1\">" +
                    $"<c r=\"A1\" t=\"inlineStr\"><is><t>Sheet {i}</t></is></c>" +
                    "</row></sheetData></worksheet>");
            }
        }
        return stream.ToArray();
    }

    [Theory]
    [InlineData("csv", true)] [InlineData("tsv", true)] [InlineData("xlsx", true)]
    [InlineData("CSV", true)]
    [InlineData("docx", false)] [InlineData("pdf", false)] [InlineData("png", false)]
    public void HandlesOnlyWhatItCanRead(string extension, bool handled) =>
        Assert.Equal(handled, Converter.Handles(extension));

    [Fact]
    public void ACsvBecomesAReadablePdf()
    {
        var r = Converter.ToPdf("id,name\n1,Alice\n2,Bo\n"u8.ToArray(),
            "people.csv", Array.Empty<string>(), null);
        Assert.Equal("ok", r.Status);
        Assert.Equal(1, PageCountOf(r.Pdf!));
    }

    [Fact]
    public void AQuotedFieldWithACommaAndANewlineSurvives()
    {
        var r = Converter.ToPdf("id,note\n1,\"Smith, John\nsecond line\"\n"u8.ToArray(),
            "notes.csv", Array.Empty<string>(), null);
        Assert.Equal("ok", r.Status);
    }

    [Fact]
    public void ALongCsvRunsToSeveralPages()
    {
        var rows = new List<string> { "id,name" };
        for (var i = 0; i < 500; i++) rows.Add($"{i},Name {i}");
        var r = Converter.ToPdf(System.Text.Encoding.UTF8.GetBytes(string.Join("\n", rows)),
            "long.csv", Array.Empty<string>(), null);
        Assert.True(PageCountOf(r.Pdf!) > 5, $"500 rows fitted on {PageCountOf(r.Pdf!)} page(s)");
    }

    [Fact]
    public void AnEmptyFileIsAnErrorNotAnEmptyPdf()
    {
        var r = Converter.ToPdf(Array.Empty<byte>(), "empty.csv", Array.Empty<string>(), null);
        Assert.Equal("error", r.Status);
        Assert.Contains("nothing", r.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void AWordDocumentIsNotItsToConvert() =>
        Assert.Equal("unsupported",
            Converter.ToPdf([1, 2, 3], "letter.docx", Array.Empty<string>(), null).Status);

    [Fact]
    public void AProtectedSpreadsheetSaysSoRatherThanAskingForAPasswordItCannotUse()
    {
        // An encrypted xlsx is an OLE compound file, not a zip — the reader
        // cannot open it, and no password would help HERE, so this must not
        // come back as needs_password (which would prompt for nothing).
        var ole = new byte[] { 0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1, 0, 0, 0, 0 };
        var asked = false;
        var r = Converter.ToPdf(ole, "locked.xlsx", ["hunter2"], _ => { asked = true; return "x"; });
        Assert.Equal("error", r.Status);
        Assert.False(asked, "the fallback must never prompt — it has no decryptor");
        Assert.Contains("Excel", r.Message);
    }

    [Fact]
    public void GarbageComesBackAsAnErrorRatherThanThrowing() =>
        Assert.Equal("error",
            Converter.ToPdf([0xFF, 0xFE, 0x00], "junk.xlsx", Array.Empty<string>(), null).Status);

    [Fact]
    public void AMultiSheetWorkbookSaysWhatItIsLeavingBehind()
    {
        var result = Converter.ToPdf(XlsxBytes(2), "book.xlsx", Array.Empty<string>(), null);
        Assert.Equal("ok", result.Status);
        Assert.Contains("first of 2", result.Message);
    }

    [Fact]
    public void ASingleSheetWorkbookSaysNothingBecauseNothingIsLost()
    {
        var result = Converter.ToPdf(XlsxBytes(1), "book.xlsx", Array.Empty<string>(), null);
        Assert.Equal("ok", result.Status);
        Assert.Empty(result.Message);
    }

    /// <summary>Review Minor 4: `Render`'s `if (repeatHeader) DrawRow(header
    /// …)` block can be deleted without changing any page count —
    /// TablePages.Paginate decides pagination from `repeatHeader`
    /// independently of whether Render's OWN drawing loop goes on to call
    /// DrawRow for the header — and no existing fact reads page CONTENT,
    /// only page COUNT (see PageCountOf throughout this file), so nothing
    /// would catch that deletion.
    ///
    /// Comparing repeatHeader:true against repeatHeader:false on the SAME
    /// table would not isolate it either: Paginate's own row budget and
    /// body-row set both depend on repeatHeader too (one fewer body row per
    /// page when a header is reserved, and row 0 excluded from the body
    /// range entirely), so such a comparison would keep passing for the
    /// wrong reason even with the header-draw line deleted — the
    /// "already-true predicate" trap.
    ///
    /// Isolating the header line specifically: both variants below draw the
    /// SAME two body rows, in the same font, at the same position; the only
    /// structural difference between them is whether "header" is ALSO drawn
    /// once, in bold, ahead of those two rows. Both fit one page (asserted,
    /// so a pagination difference is not silently the source of any length
    /// difference either). If the header-draw block is deleted, `withHeader`
    /// stops drawing row 0 at all (Paginate still treats it as the header
    /// row and excludes it from the body range, since that call is
    /// untouched) and its output shrinks to be indistinguishable in
    /// substance from `bodyOnly` — the margin below is generous enough to
    /// clear ordinary PdfSharp output jitter (its trailer's own
    /// timestamp-derived document ID) while still catching that.</summary>
    [Fact]
    public void TheHeaderRowIsActuallyDrawnNotJustReservedInThePageLayout()
    {
        var header = new string('H', 60);
        var withHeader = new List<List<string>>
            { new() { header }, new() { "body one" }, new() { "body two" } };
        var bodyOnly = new List<List<string>> { new() { "body one" }, new() { "body two" } };

        var pdfWithHeader = TableToPdf.Render(withHeader, TableToPdf.PageWidthPt, TableToPdf.PageHeightPt,
            TableToPdf.MarginPt, TableToPdf.RowHeightPt, TableToPdf.FontSizePt, repeatHeader: true);
        var pdfBodyOnly = TableToPdf.Render(bodyOnly, TableToPdf.PageWidthPt, TableToPdf.PageHeightPt,
            TableToPdf.MarginPt, TableToPdf.RowHeightPt, TableToPdf.FontSizePt, repeatHeader: false);

        Assert.Equal(1, PageCountOf(pdfWithHeader));
        Assert.Equal(1, PageCountOf(pdfBodyOnly));
        Assert.True(pdfWithHeader.Length > pdfBodyOnly.Length + 20,
            $"expected drawing the header row to add meaningfully more content than the {pdfBodyOnly.Length}-byte header-less output; got {pdfWithHeader.Length}");
    }

    [Fact]
    public void PagesComeOutLandscapeNotTextsPortrait()
    {
        // Judgement call #4 (page orientation): a table reads left-to-right,
        // so it gets its own landscape constants rather than sharing
        // TextToPdf's portrait ones.
        var r = Converter.ToPdf("a,b\n1,2\n"u8.ToArray(), "small.csv", Array.Empty<string>(), null);
        using var stream = new MemoryStream(r.Pdf!);
        using var doc = PdfReader.Open(stream, PdfDocumentOpenMode.Import);
        Assert.Equal(TableToPdf.PageWidthPt, doc.Pages[0].Width.Point, 1);
        Assert.Equal(TableToPdf.PageHeightPt, doc.Pages[0].Height.Point, 1);
        Assert.True(TableToPdf.PageWidthPt > TableToPdf.PageHeightPt, "a table page should be landscape");
    }
}
