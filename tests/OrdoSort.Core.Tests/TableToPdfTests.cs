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
