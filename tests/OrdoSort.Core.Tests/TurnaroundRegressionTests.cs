using System.IO.Compression;
using System.Text;
using OrdoSort.Core;

namespace OrdoSort.Core.Tests;

/// <summary>The whole Phase 1 pipeline over real files on disk — the
/// miniature regression fixture the spec calls for: same shapes as the
/// verified live figures, small enough to check by hand, entirely
/// synthetic. If a rule regresses (dedupe order, a date convention, the
/// business-day counter, an exclusion), one of these exact numbers moves.</summary>
public class TurnaroundRegressionTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("ordoreg_").FullName;
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { /* best effort */ } }

    private void WriteReport(string relativePath, string[][] dataRows)
    {
        var path = Path.Combine(_dir, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var rows = new[] { new[] { "FileName", "SourceType", "Pagecount", "Destination" } }
            .Concat(dataRows).ToArray();
        var sb = new StringBuilder(
            "<worksheet xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\"><sheetData>");
        for (var r = 0; r < rows.Length; r++)
        {
            sb.Append($"<row r=\"{r + 1}\">");
            for (var c = 0; c < rows[r].Length; c++)
                sb.Append($"<c r=\"{(char)('A' + c)}{r + 1}\" t=\"inlineStr\"><is><t>{rows[r][c]}</t></is></c>");
            sb.Append("</row>");
        }
        sb.Append("</sheetData></worksheet>");
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
        using var w = new StreamWriter(zip.CreateEntry("xl/worksheets/sheet1.xml").Open(), Encoding.UTF8);
        w.Write(sb.ToString());
    }

    private static string[] Doc(string name, string source) => new[] { name, source, "10", "MIX" };

    [Fact]
    public void TheMiniatureLiveShapeComputesItsExactFigures()
    {
        WriteReport(@"20260706\20260706-0900-PECF Report.xlsx", new[]
        {
            Doc("20260706-A.pdf", "Email"),
            Doc("20260703-B.pdf", "Email"),
            Doc("20260702-C.pdf", "FAX"),
            Doc("20260701-D.pdf", "Paper"),
            Doc("20260704-E.pdf", "CD"),
            Doc("07022026 F.pdf", "ECAA"),
            Doc("07.03.2026 G.pdf", "ECAA"),
            Doc("20260707-H.pdf", "Email"),
            Doc("NODATE.pdf", "Email"),
        });
        WriteReport(@"20260803\20260803-0900-PECF Report.xlsx", new[]
        {
            Doc("20260706-A.pdf", "Email"),   // duplicate — the July report wins
            Doc("20260803-I.pdf", "Email"),
            Doc("20260731-J.pdf", "FAX"),
            Doc("20260730-K.pdf", "CD"),
            Doc("20260724-L.pdf", "Paper"),
        });

        var feed = UploadReportFeed.Load(_dir);
        Assert.Equal(2, feed.Report.FilesFound);
        Assert.Empty(feed.Report.Skipped);
        Assert.Equal(14, feed.Report.RowCount);

        var summary = TurnaroundSummary.Compute(feed.Table, new IgnoreList(new[] { "ECAA" }));

        Assert.Equal(9, summary.Docs.Count);
        Assert.Equal(new TurnaroundSummary.BucketCounts(3, 2, 2, 2), summary.Overall);
        Assert.Equal(55.56, summary.Overall.ZeroToOnePercent, 2);

        Assert.Equal(new[] { "2026-07", "2026-08" }, summary.ByMonth.Select(m => m.Month));
        Assert.Equal(new TurnaroundSummary.BucketCounts(2, 1, 1, 1), summary.ByMonth[0].Counts);
        Assert.Equal(new TurnaroundSummary.BucketCounts(1, 1, 1, 1), summary.ByMonth[1].Counts);

        Assert.Equal(new[] { "Email", "CD", "FAX", "Paper" },
            summary.BySource.Select(s => s.SourceType));
        Assert.Equal(3, summary.BySource[0].Counts.Total);

        Assert.Equal(1, summary.DuplicateRows);
        Assert.Equal(new TurnaroundSummary.IgnoredSource("ECAA", 2), Assert.Single(summary.Ignored));
        Assert.Equal(1, summary.FutureDated);
        Assert.Equal(1, summary.NoDate);
    }
}
