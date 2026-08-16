using OrdoSort.Core;

namespace OrdoSort.Core.Tests;

/// <summary>Compute is pure computation on SweptTable rows, so these tests
/// build rows directly rather than round-tripping through disk — the same
/// stance TurnaroundTimeTests takes. Report filenames are the upload clock:
/// "20260706-0900-PECF Report.xlsx" uploads on Monday 2026-07-06.</summary>
public class TurnaroundSummaryComputeTests
{
    private const string R1 = "20260706-0900-PECF Report.xlsx";   // Mon Jul 6
    private const string R2 = "20260803-0900-PECF Report.xlsx";   // Mon Aug 3

    private static readonly string[] Headers =
        { "FileName", "SourceType", "Pagecount", "Destination" };

    private static SweptTable.Row Row(string report, string fileName,
        string sourceType = "Email", string pages = "10", string dest = "MIX") =>
        new(new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["FileName"] = fileName, ["SourceType"] = sourceType,
            ["Pagecount"] = pages, ["Destination"] = dest,
        }, report);

    private static SweptTable.Table Table(params SweptTable.Row[] rows) =>
        new(Headers, rows, FilesRead: 1, FileErrors: Array.Empty<string>());

    private static readonly IgnoreList NoIgnores = new(Array.Empty<string>());

    [Fact]
    public void ADocumentInTwoReportsCountsOnceAndTheEarliestUploadWins()
    {
        // Listed in R2's rows first — input order must not decide the winner.
        var summary = TurnaroundSummary.Compute(Table(
            Row(R2, "20260706-A.pdf"),
            Row(R1, "20260706-A.pdf")), NoIgnores);

        Assert.Equal(1, summary.DuplicateRows);
        var duplicate = Assert.Single(summary.DuplicateRowsDetail);
        Assert.Equal("20260706-A.pdf", duplicate.Cells[TurnaroundSummary.FileNameColumn]);
        var doc = Assert.Single(summary.Docs);
        Assert.Equal(new DateOnly(2026, 7, 6), doc.UploadDate);   // R1, the earlier report
        Assert.Equal(TurnaroundSummary.Bucket.SameDay, doc.Bucket);
    }

    [Fact]
    public void BlankFileNamesAreNeverMergedWithEachOther()
    {
        var summary = TurnaroundSummary.Compute(Table(
            Row(R1, ""), Row(R1, "")), NoIgnores);

        Assert.Equal(0, summary.DuplicateRows);
        Assert.Equal(2, summary.NoDate);   // both count, neither is a "duplicate"
    }

    [Fact]
    public void IgnoredSourcesAreSetAsideWholeWithPerValueCounts()
    {
        var summary = TurnaroundSummary.Compute(Table(
            Row(R1, "20260706-A.pdf"),
            Row(R1, "07022026 B.pdf", sourceType: "ECAA"),
            Row(R1, "07.03.2026 C.pdf", sourceType: "ECAA")), new IgnoreList(new[] { "ECAA" }));

        var ignored = Assert.Single(summary.Ignored);
        Assert.Equal(new TurnaroundSummary.IgnoredSource("ECAA", 2), ignored);
        Assert.Equal(2, summary.IgnoredDetail.Count);
        Assert.Single(summary.Docs);                       // only the Email doc measures
        Assert.Equal(100.0, summary.Overall.ZeroToOnePercent);   // percentages over the remainder
    }

    [Fact]
    public void WithoutTheIgnoreListEcaaDatesStillParse()
    {
        // Re-including ECAA later must yield real dates (spec rule 1).
        var summary = TurnaroundSummary.Compute(Table(
            Row(R1, "07022026 B.pdf", sourceType: "ECAA")), NoIgnores);

        var doc = Assert.Single(summary.Docs);
        Assert.Equal(new DateOnly(2026, 7, 2), doc.DocDate);   // Thu → Mon = 2 business days
        Assert.Equal(TurnaroundSummary.Bucket.TwoDays, doc.Bucket);
    }

    [Fact]
    public void FutureDatedDocumentsAreExcludedAndCountedNeverCoerced()
    {
        var summary = TurnaroundSummary.Compute(Table(
            Row(R1, "20260707-A.pdf"),      // dated the day after its upload
            Row(R1, "20260706-B.pdf")), NoIgnores);

        Assert.Equal(1, summary.FutureDated);
        var futureDated = Assert.Single(summary.FutureDatedDetail);
        Assert.Equal("20260707-A.pdf", futureDated.Cells[TurnaroundSummary.FileNameColumn]);
        Assert.Single(summary.Docs);
    }

    [Fact]
    public void UndatedNamesAreExcludedAndCountedNeverGuessed()
    {
        var summary = TurnaroundSummary.Compute(Table(
            Row(R1, "NODATE.pdf"),
            Row(R1, "20260706-B.pdf")), NoIgnores);

        Assert.Equal(1, summary.NoDate);
        Assert.Single(summary.NoDateDetail);
        Assert.Single(summary.Docs);
    }

    [Fact]
    public void AggregatesGroupByUploadMonthSourceAndIsoWeek()
    {
        var summary = TurnaroundSummary.Compute(Table(
            Row(R1, "20260706-A.pdf", sourceType: "Email"),
            Row(R1, "20260703-B.pdf", sourceType: "FAX"),     // Fri → Mon = 1
            Row(R2, "20260803-C.pdf", sourceType: "Email")), NoIgnores);

        Assert.Equal(new[] { "2026-07", "2026-08" }, summary.ByMonth.Select(m => m.Month));
        Assert.Equal(2, summary.ByMonth[0].Counts.Total);
        Assert.Equal(1, summary.ByMonth[1].Counts.Total);

        // Source order: count descending, then ordinal.
        Assert.Equal(new[] { "Email", "FAX" }, summary.BySource.Select(s => s.SourceType));
        Assert.Equal(1, summary.BySource[1].Counts.OneDay);

        Assert.Equal(2, summary.ByWeek.Count);
        Assert.All(summary.ByWeek, w => Assert.Matches(@"^\d{4}-W\d{2}$", w.Week));
        Assert.Equal(2, summary.ByWeek[0].Counts.Total);   // both R1 docs upload the same week
    }

    [Fact]
    public void SourceTypesAreNeverCaseFolded()
    {
        var summary = TurnaroundSummary.Compute(Table(
            Row(R1, "20260706-A.pdf", sourceType: "Email"),
            Row(R1, "20260706-B.pdf", sourceType: "EMAIL")), NoIgnores);

        Assert.Equal(2, summary.BySource.Count);   // two values, reported as found
    }
}
