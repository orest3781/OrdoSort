using OrdoSort.Core;

namespace OrdoSort.Core.Tests;

/// <summary>Export and copy-text are the two surfaces where a formatting
/// slip silently misreports the SLA numbers to leadership — so the exact
/// strings and cell values are pinned here against a small computed summary
/// (built through Compute, not hand-assembled, so these tests break if the
/// engine's shapes drift). 2026-07-06 is a Monday.</summary>
public class TurnaroundExportTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("ordotx_").FullName;
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { /* best effort */ } }

    private const string R1 = "20260706-0900-PECF Report.xlsx";

    private static SweptTable.Row Row(string fileName, string source) =>
        new(new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["FileName"] = fileName, ["SourceType"] = source,
            ["Pagecount"] = "10", ["Destination"] = "MIX",
        }, R1);

    private static readonly TurnaroundSummary.Summary Summary = TurnaroundSummary.Compute(
        new SweptTable.Table(
            new[] { "FileName", "SourceType", "Pagecount", "Destination" },
            new[]
            {
                Row("20260706-A.pdf", "Email"),   // Same day
                Row("20260703-B.pdf", "Email"),   // Fri→Mon = 1
                Row("20260702-C.pdf", "FAX"),     // Thu→Mon = 2
                Row("20260701-D.pdf", "Paper"),   // Wed→Mon = 3+
                Row("07022026 E.pdf", "ECAA"),    // ignored
                Row("20260707-F.pdf", "Email"),   // future-dated
            },
            FilesRead: 1, FileErrors: Array.Empty<string>()),
        new IgnoreList(new[] { "ECAA" }));

    private static readonly UploadReportFeed.LoadReport Report = new(
        FilesFound: 1, Skipped: Array.Empty<string>(),
        FirstUpload: new DateOnly(2026, 7, 6), LastUpload: new DateOnly(2026, 7, 6),
        RowCount: 6);

    [Fact]
    public void MonthNameIsInvariantThreeLetter()
    {
        Assert.Equal("Jul", TurnaroundExport.MonthName("2026-07"));
        Assert.Equal("Dec", TurnaroundExport.MonthName("2026-12"));
    }

    [Fact]
    public void CopyTextCarriesTheHeadlineEveryBucketAndEverySetAside()
    {
        var text = TurnaroundExport.BuildCopyText(Summary, Report);
        var lines = text.Split('\n').Select(l => l.TrimEnd('\r')).ToArray();
        Assert.Equal("Turn-around time — 2026-07-06 to 2026-07-06 (1 files, 6 rows)", lines[0]);
        Assert.Equal("0-1 business days: 50.0% (2 of 4) · 2 days: 25.0% (1) · 3+ days: 25.0% (1)", lines[1]);
        Assert.Equal("Jul: 50.0% in 0-1", lines[2]);
        // Email holds A (Same day) and B (1 day) — both inside 0-1, so 100.0%.
        Assert.Equal("By source (0-1 share): Email 100.0% · FAX 0.0% · Paper 0.0%", lines[3]);
        Assert.Equal("Set aside: 0 duplicates · 1 future-dated · 0 without a date · ECAA 1 ignored", lines[4]);
        Assert.Equal(5, lines.Length);
    }

    [Fact]
    public void WorkbookSheetOneCarriesFiguresAndSheetTwoCarriesTheDocuments()
    {
        var path = Path.Combine(_dir, "t.xlsx");
        TurnaroundExport.Write(path, Summary, Report, @"\\server\share");

        var summarySheet = XlsxTable.Read(path);   // reads the FIRST sheet
        // Row 0 is the title block; find pinned rows by their labels.
        Assert.Contains(summarySheet, r => r.Count >= 2 && r[0] == "Source folder" && r[1] == @"\\server\share");
        Assert.Contains(summarySheet, r => r.Count >= 3 && r[0] == "0-1 business days" && r[1] == "2" && r[2] == "50");
        Assert.Contains(summarySheet, r => r.Count >= 2 && r[0] == "Future-dated" && r[1] == "1");
        Assert.Contains(summarySheet, r => r.Count >= 2 && r[0] == "Ignored: ECAA" && r[1] == "1");
        Assert.Contains(summarySheet, r => r.Count >= 2 && r[0] == "Upload span" && r[1] == "2026-07-06 to 2026-07-06");

        // Sheet 2: header + one row per measurable doc, dates pre-formatted.
        using var zip = System.IO.Compression.ZipFile.OpenRead(path);
        Assert.NotNull(zip.GetEntry("xl/worksheets/sheet2.xml"));
    }

    [Fact]
    public void DetailRowsMatchTheMeasurableDocs()
    {
        var path = Path.Combine(_dir, "d.xlsx");
        TurnaroundExport.Write(path, Summary, Report, "x");
        // Rewrite sheet 2 alone through the writer to read it back via
        // XlsxTable (which reads only the first sheet): instead, assert via
        // the builder's own row source — the Docs list drives sheet 2 1:1.
        Assert.Equal(4, Summary.Docs.Count);
        var detail = TurnaroundExport.DetailRows(Summary);
        Assert.Equal("FileName", detail[0][0]);
        Assert.Equal(5, detail.Count);   // header + 4 docs
        Assert.Contains(detail, r => (string?)r[0] == "20260706-A.pdf" && (string?)r[4] == "2026-07-06"
            && (string?)r[6] == "0" && (string?)r[7] == "Same day");
    }
}
