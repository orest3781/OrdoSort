using System.Globalization;
using System.Text;

namespace OrdoSort.Core.Tests;

/// <summary>TurnaroundTime is pure computation on top of SweptTable rows —
/// these tests build rows and DocRows directly rather than round-tripping
/// through Csv/SweptTable.Load, since that plumbing is already pinned by
/// CsvTests and SweptTableTests.</summary>
public class TurnaroundTimeTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "tattest_" + Guid.NewGuid());
    public TurnaroundTimeTests() => Directory.CreateDirectory(_dir);
    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private static void UnderCulture(string culture, Action body)
    {
        var prev = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo(culture);
            body();
        }
        finally
        {
            CultureInfo.CurrentCulture = prev;
        }
    }

    // ---- UploadTimeFromReportName ----

    [Fact]
    public void ReportNameWithDateAndTimeParsesBoth()
    {
        var upload = TurnaroundTime.UploadTimeFromReportName("20250303-1144-PECF Report.xlsx");
        Assert.Equal(new DateTime(2025, 3, 3, 11, 44, 0), upload);
    }

    [Fact]
    public void ReportNameAsAFullPathStillParses()
    {
        var upload = TurnaroundTime.UploadTimeFromReportName(
            @"C:\shares\pecf\20250303-1144-PECF Report.xlsx");
        Assert.Equal(new DateTime(2025, 3, 3, 11, 44, 0), upload);
    }

    [Fact]
    public void ReportNameWithNoTimeFallsBackToDateAtMidnight()
    {
        var upload = TurnaroundTime.UploadTimeFromReportName("20250303-PECF.xlsx");
        Assert.Equal(new DateTime(2025, 3, 3, 0, 0, 0), upload);
    }

    [Fact]
    public void ReportNameWithNoLeadingDateIsNull()
    {
        Assert.Null(TurnaroundTime.UploadTimeFromReportName("PECF Report.xlsx"));
    }

    [Fact]
    public void ReportNameWithAnInvalidCalendarDateIsNull()
    {
        Assert.Null(TurnaroundTime.UploadTimeFromReportName("20251332-1144-x.xlsx"));
    }

    /// <summary>Bad time half (hour 99) falls back to the date-at-midnight
    /// cascade rather than going all the way to null — the date itself is
    /// still valid, so the row is still worth a TAT number even if the
    /// upload's exact minute is unknowable.</summary>
    [Fact]
    public void ReportNameWithAnInvalidTimeFallsBackToDateAtMidnight()
    {
        var upload = TurnaroundTime.UploadTimeFromReportName("20250303-9999-x.xlsx");
        Assert.Equal(new DateTime(2025, 3, 3, 0, 0, 0), upload);
    }

    // ---- ExtractDocDate ----

    [Fact]
    public void DocFileNameWithSingleDashParses()
    {
        var date = TurnaroundTime.ExtractDocDate("20250303-JASMIN-KRISTEN-COPR020525-15350.pdf");
        Assert.Equal(new DateOnly(2025, 3, 3), date);
    }

    [Fact]
    public void DocFileNameWithDoubleDashScannerFormParses()
    {
        var date = TurnaroundTime.ExtractDocDate("20250401--12345.pdf");
        Assert.Equal(new DateOnly(2025, 4, 1), date);
    }

    [Fact]
    public void DocFileNameAsAFullPathStillParses()
    {
        var date = TurnaroundTime.ExtractDocDate(
            @"S:\scans\20250303-JASMIN-KRISTEN-COPR020525-15350.pdf");
        Assert.Equal(new DateOnly(2025, 3, 3), date);
    }

    [Fact]
    public void DocFileNameWithNoLeadingDateIsNull()
    {
        Assert.Null(TurnaroundTime.ExtractDocDate("NOTADATE-x.pdf"));
    }

    [Fact]
    public void DocFileNameWithAnInvalidCalendarDateIsNull()
    {
        Assert.Null(TurnaroundTime.ExtractDocDate("20251332-x.pdf"));
    }

    // ---- Compute / ComputeAll ----

    [Fact]
    public void ComputeWithBothDatesPresentGivesTheDayGap()
    {
        var row = new SweptTable.Row(
            new Dictionary<string, string>
            {
                ["FileName"] = "20250228-HELTON-EMILY-KYPT2024-11-63094.pdf",
                ["Category"] = "DRG"
            },
            "20250303-1144-PECF Report.xlsx");

        var doc = TurnaroundTime.Compute(row, "FileName", "Category");

        Assert.Equal(new DateOnly(2025, 2, 28), doc.DocDate);
        Assert.Equal(new DateTime(2025, 3, 3, 11, 44, 0), doc.UploadDate);
        Assert.Equal(3, doc.TatDays);
        Assert.Equal("DRG", doc.Category);
    }

    [Fact]
    public void ComputeWithDocDateAfterUploadGivesANegativeTatDays()
    {
        var row = new SweptTable.Row(
            new Dictionary<string, string> { ["FileName"] = "20250310-x.pdf" },
            "20250303-1144-PECF Report.xlsx");

        var doc = TurnaroundTime.Compute(row, "FileName", null);

        Assert.Equal(-7, doc.TatDays);
    }

    [Fact]
    public void ComputeWithAnUnparseableDocDateGivesNullTatDaysButKeepsTheRow()
    {
        var row = new SweptTable.Row(
            new Dictionary<string, string> { ["FileName"] = "NOTADATE.pdf" },
            "20250303-1144-PECF Report.xlsx");

        var doc = TurnaroundTime.Compute(row, "FileName", null);

        Assert.Null(doc.DocDate);
        Assert.NotNull(doc.UploadDate);
        Assert.Null(doc.TatDays);
        Assert.Equal("NOTADATE.pdf", doc.FileName);
    }

    [Fact]
    public void ComputeWithAnUnparseableReportNameGivesNullTatDaysButKeepsTheRow()
    {
        var row = new SweptTable.Row(
            new Dictionary<string, string> { ["FileName"] = "20250303-x.pdf" },
            "PECF Report.xlsx");

        var doc = TurnaroundTime.Compute(row, "FileName", null);

        Assert.NotNull(doc.DocDate);
        Assert.Null(doc.UploadDate);
        Assert.Null(doc.TatDays);
    }

    [Fact]
    public void ComputeWithNoCategoryColumnPickedGivesAnEmptyCategory()
    {
        var row = new SweptTable.Row(
            new Dictionary<string, string> { ["FileName"] = "20250303-x.pdf", ["Category"] = "DRG" },
            "20250303-1144-x.xlsx");

        var doc = TurnaroundTime.Compute(row, "FileName", categoryColumn: null);

        Assert.Equal("", doc.Category);
    }

    [Fact]
    public void ComputeWithACategoryColumnNameNotInTheRowGivesAnEmptyCategory()
    {
        var row = new SweptTable.Row(
            new Dictionary<string, string> { ["FileName"] = "20250303-x.pdf" },
            "20250303-1144-x.xlsx");

        var doc = TurnaroundTime.Compute(row, "FileName", "Category");

        Assert.Equal("", doc.Category);
    }

    [Fact]
    public void ComputeWithAFilenameColumnNameNotInTheRowGivesAnEmptyFileNameAndNullDocDate()
    {
        var row = new SweptTable.Row(
            new Dictionary<string, string> { ["Other"] = "x" },
            "20250303-1144-x.xlsx");

        var doc = TurnaroundTime.Compute(row, "FileName", null);

        Assert.Equal("", doc.FileName);
        Assert.Null(doc.DocDate);
    }

    [Fact]
    public void ComputeAllPreservesInputOrderAndNeverDropsARow()
    {
        var table = new SweptTable.Table(
            new[] { "FileName" },
            new[]
            {
                new SweptTable.Row(
                    new Dictionary<string, string> { ["FileName"] = "20250228-a.pdf" },
                    "20250303-1144-x.xlsx"),
                new SweptTable.Row(
                    new Dictionary<string, string> { ["FileName"] = "NOTADATE.pdf" },
                    "20250303-1144-x.xlsx")
            },
            2, Array.Empty<string>());

        var rows = TurnaroundTime.ComputeAll(table, "FileName", null);

        Assert.Equal(2, rows.Count);
        Assert.Equal("20250228-a.pdf", rows[0].FileName);
        Assert.NotNull(rows[0].TatDays);
        Assert.Equal("NOTADATE.pdf", rows[1].FileName);
        Assert.Null(rows[1].TatDays);
    }

    // ---- DailyAverages / WeeklyAverages ----

    [Fact]
    public void DailyAveragesGroupsByUploadDateAscendingAndExcludesNullTatDays()
    {
        var rows = new[]
        {
            new TurnaroundTime.DocRow("r1.xlsx", "a.pdf", new DateOnly(2025, 3, 1),
                new DateTime(2025, 3, 3), 2, "DRG"),
            new TurnaroundTime.DocRow("r1.xlsx", "b.pdf", new DateOnly(2025, 2, 28),
                new DateTime(2025, 3, 3), 4, "DRG"),
            new TurnaroundTime.DocRow("r2.xlsx", "c.pdf", new DateOnly(2025, 3, 4),
                new DateTime(2025, 3, 5), 1, "DRG"),
            new TurnaroundTime.DocRow("r2.xlsx", "d.pdf", null, new DateTime(2025, 3, 5), null, "DRG")
        };

        var daily = TurnaroundTime.DailyAverages(rows);

        Assert.Equal(2, daily.Count);
        Assert.Equal("2025-03-03", daily[0].Period);
        Assert.Equal(3.0, daily[0].AverageDays);
        Assert.Equal(2, daily[0].Count);
        Assert.Equal("2025-03-05", daily[1].Period);
        Assert.Equal(1.0, daily[1].AverageDays);
        Assert.Equal(1, daily[1].Count);
    }

    /// <summary>March 3 2025 and March 10 2025 are both Mondays, one ISO
    /// week apart, landing in ISO weeks 10 and 11 of 2025 — pinning the
    /// "2025-W10" label format the brief specifies, not just the grouping
    /// arithmetic.</summary>
    [Fact]
    public void WeeklyAveragesGroupsByIsoWeekAscendingWithTheYyyyWwwLabel()
    {
        var week1 = new DateTime(2025, 3, 3);
        var week2 = new DateTime(2025, 3, 10);
        var rows = new[]
        {
            new TurnaroundTime.DocRow("r.xlsx", "a.pdf", DateOnly.FromDateTime(week1), week1, 2, "DRG"),
            new TurnaroundTime.DocRow("r.xlsx", "b.pdf", DateOnly.FromDateTime(week1), week1, 4, "DRG"),
            new TurnaroundTime.DocRow("r.xlsx", "c.pdf", DateOnly.FromDateTime(week2), week2, 1, "DRG")
        };

        var weekly = TurnaroundTime.WeeklyAverages(rows);

        Assert.Equal(2, weekly.Count);
        Assert.Equal("2025-W10", weekly[0].Period);
        Assert.Equal(3.0, weekly[0].AverageDays);
        Assert.Equal(2, weekly[0].Count);
        Assert.Equal("2025-W11", weekly[1].Period);
        Assert.Equal(1.0, weekly[1].AverageDays);
        Assert.Equal(1, weekly[1].Count);
    }

    // ---- ByCategory ----

    [Fact]
    public void ByCategoryGroupsSortsOrdinallyAndCountsStrictlyOverThreshold()
    {
        var rows = new[]
        {
            new TurnaroundTime.DocRow("r.xlsx", "a.pdf", new DateOnly(2025, 3, 1),
                new DateTime(2025, 3, 3), 2, "DRG"),
            new TurnaroundTime.DocRow("r.xlsx", "b.pdf", new DateOnly(2025, 3, 1),
                new DateTime(2025, 3, 3), 10, "DRG"),
            new TurnaroundTime.DocRow("r.xlsx", "c.pdf", new DateOnly(2025, 3, 1),
                new DateTime(2025, 3, 3), 1, "COPR"),
            new TurnaroundTime.DocRow("r.xlsx", "d.pdf", null, null, null, "COPR")
        };

        var byCategory = TurnaroundTime.ByCategory(rows, thresholdDays: 5);

        Assert.Equal(2, byCategory.Count);
        Assert.Equal("COPR", byCategory[0].Category);
        Assert.Equal(1.0, byCategory[0].AverageDays);
        Assert.Equal(1, byCategory[0].Count);
        Assert.Equal(0, byCategory[0].OverThreshold);
        Assert.Equal("DRG", byCategory[1].Category);
        Assert.Equal(6.0, byCategory[1].AverageDays);
        Assert.Equal(2, byCategory[1].Count);
        Assert.Equal(1, byCategory[1].OverThreshold);
    }

    // ---- ExceedingThreshold ----

    [Fact]
    public void ExceedingThresholdExcludesExactlyAtThresholdAndIncludesOneOver()
    {
        var atThreshold = new TurnaroundTime.DocRow("r.xlsx", "a.pdf", new DateOnly(2025, 3, 1),
            new DateTime(2025, 3, 6), 5, "DRG");
        var overThreshold = new TurnaroundTime.DocRow("r.xlsx", "b.pdf", new DateOnly(2025, 3, 1),
            new DateTime(2025, 3, 7), 6, "DRG");

        var result = TurnaroundTime.ExceedingThreshold(new[] { atThreshold, overThreshold }, 5);

        Assert.Single(result);
        Assert.Equal("b.pdf", result[0].FileName);
    }

    // ---- ExportCsv ----

    [Fact]
    public void ExportCsvWritesTheExactHeaderLine()
    {
        var dest = Path.Combine(_dir, "header.csv");
        TurnaroundTime.ExportCsv(Array.Empty<TurnaroundTime.DocRow>(), dest);

        var lines = File.ReadAllLines(dest, Encoding.UTF8);
        Assert.Equal("source_report,file_name,category,doc_date,upload_date,tat_days", lines[0]);
    }

    [Fact]
    public void ExportCsvHasAUtf8Bom()
    {
        var dest = Path.Combine(_dir, "bom.csv");
        TurnaroundTime.ExportCsv(Array.Empty<TurnaroundTime.DocRow>(), dest);

        var bytes = File.ReadAllBytes(dest);
        Assert.Equal(new byte[] { 0xEF, 0xBB, 0xBF }, bytes.Take(3));
    }

    [Fact]
    public void ExportCsvReturnsTheRowCount()
    {
        var rows = new[]
        {
            new TurnaroundTime.DocRow("r.xlsx", "a.pdf", new DateOnly(2025, 3, 1),
                new DateTime(2025, 3, 3), 2, "DRG"),
            new TurnaroundTime.DocRow("r.xlsx", "b.pdf", new DateOnly(2025, 3, 1),
                new DateTime(2025, 3, 3), 4, "DRG")
        };
        var count = TurnaroundTime.ExportCsv(rows, Path.Combine(_dir, "count.csv"));
        Assert.Equal(2, count);
    }

    [Fact]
    public void ExportCsvNullDatesBecomeEmptyFields()
    {
        var rows = new[] { new TurnaroundTime.DocRow("r.xlsx", "x.pdf", null, null, null, "") };
        var dest = Path.Combine(_dir, "nulls.csv");

        TurnaroundTime.ExportCsv(rows, dest);

        var lines = File.ReadAllLines(dest, Encoding.UTF8);
        Assert.Equal("r.xlsx,x.pdf,,,,", lines[1]);
    }

    /// <summary>A file name with a comma and an embedded quote must survive
    /// Csv.EscapeField's guard round-trip, not corrupt the row.</summary>
    [Fact]
    public void ExportCsvEscapesCommasAndQuotesInTheFileName()
    {
        var rows = new[]
        {
            new TurnaroundTime.DocRow("20250303-1144-PECF Report.xlsx", "20250228-\"weird\", name.pdf",
                new DateOnly(2025, 2, 28), new DateTime(2025, 3, 3, 11, 44, 0), 3, "DRG")
        };
        var dest = Path.Combine(_dir, "escaped.csv");

        TurnaroundTime.ExportCsv(rows, dest);

        var lines = File.ReadAllLines(dest, Encoding.UTF8);
        Assert.Equal(
            "20250303-1144-PECF Report.xlsx,\"20250228-\"\"weird\"\", name.pdf\",DRG,2025-02-28,2025-03-03 11:44,3",
            lines[1]);
    }

    // ---- Culture invariance ----

    /// <summary>Mirrors CultureInvariantDatesTests' UnderCulture swap: parsing
    /// and export both route every format/parse through InvariantCulture, so
    /// de-DE's comma decimal separator and th-TH's Buddhist-era calendar
    /// must never change a single byte of the result.</summary>
    [Theory]
    [InlineData("de-DE")]
    [InlineData("th-TH")]
    public void ParseAndExportAreByteIdenticalUnderAnyCulture(string culture)
    {
        var rows = new[]
        {
            new TurnaroundTime.DocRow("20250303-1144-PECF Report.xlsx",
                "20250228-HELTON-EMILY-KYPT2024-11-63094.pdf",
                new DateOnly(2025, 2, 28), new DateTime(2025, 3, 3, 11, 44, 0), 3, "DRG")
        };

        var invariantUpload = TurnaroundTime.UploadTimeFromReportName("20250303-1144-PECF Report.xlsx");
        var invariantDoc = TurnaroundTime.ExtractDocDate("20250228-HELTON-EMILY-KYPT2024-11-63094.pdf");
        var invariantDest = Path.Combine(_dir, "invariant.csv");
        TurnaroundTime.ExportCsv(rows, invariantDest);
        var invariantBytes = File.ReadAllBytes(invariantDest);

        DateTime? swappedUpload = null;
        DateOnly? swappedDoc = null;
        byte[] swappedBytes = Array.Empty<byte>();
        UnderCulture(culture, () =>
        {
            swappedUpload = TurnaroundTime.UploadTimeFromReportName("20250303-1144-PECF Report.xlsx");
            swappedDoc = TurnaroundTime.ExtractDocDate("20250228-HELTON-EMILY-KYPT2024-11-63094.pdf");
            var swappedDest = Path.Combine(_dir, $"swapped-{culture}.csv");
            TurnaroundTime.ExportCsv(rows, swappedDest);
            swappedBytes = File.ReadAllBytes(swappedDest);
        });

        Assert.Equal(invariantUpload, swappedUpload);
        Assert.Equal(invariantDoc, swappedDoc);
        Assert.Equal(invariantBytes, swappedBytes);
    }
}
