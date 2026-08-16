using System.IO.Compression;
using System.Text;
using OrdoSort.Core;

namespace OrdoSort.Core.Tests;

/// <summary>UploadReportFeed walks a folder for PECF reports. Tests build
/// real minimal workbooks with ZipArchive (the XlsxTableTests technique) so
/// nothing depends on Excel — and everything in them is synthetic; live
/// sample data never enters a fixture (spec: PHI stance).</summary>
public class UploadReportFeedTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("ordofeed_").FullName;
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { /* best effort */ } }

    /// <summary>A one-sheet workbook of inline strings. Cell text must not
    /// contain &, &lt; or &gt; — fixture data here never does.</summary>
    private string WriteXlsx(string relativePath, string[][] rows)
    {
        var path = Path.Combine(_dir, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
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
        return path;
    }

    private static string[][] Rows(params string[] fileNames) =>
        new[] { new[] { "FileName", "SourceType" } }
            .Concat(fileNames.Select(f => new[] { f, "Email" }))
            .ToArray();

    [Fact]
    public void OnlyExactReportNamesMatch()
    {
        Assert.True(UploadReportFeed.IsReportFile(@"x\20260701-1042-PECF Report.xlsx"));
        Assert.True(UploadReportFeed.IsReportFile("20260701-1042-pecf report.XLSX"));   // case-insensitive
        Assert.False(UploadReportFeed.IsReportFile("summary.xlsx"));
        Assert.False(UploadReportFeed.IsReportFile("20260701-PECF Report.xlsx"));            // no time half
        Assert.False(UploadReportFeed.IsReportFile("20260701-1042-PECF Report - Copy.xlsx")); // suffixed
    }

    [Fact]
    public void FindsReportsInDatedSubfoldersSortedByName()
    {
        WriteXlsx(@"20260706\20260706-0941-PECF Report.xlsx", Rows("20260706-A.pdf"));
        WriteXlsx(@"20260701\20260701-1042-PECF Report.xlsx", Rows("20260701-B.pdf"));
        WriteXlsx("20260707-1001-PECF Report.xlsx", Rows("20260707-C.pdf"));   // root level counts too
        WriteXlsx(@"20260701\notes.xlsx", Rows("ignored.pdf"));                // filtered out

        var files = UploadReportFeed.FindFiles(_dir);
        Assert.Equal(new[]
        {
            "20260701-1042-PECF Report.xlsx",
            "20260706-0941-PECF Report.xlsx",
            "20260707-1001-PECF Report.xlsx",
        }, files.Select(Path.GetFileName));
    }

    [Fact]
    public void LoadReportsCountsSpanAndRows()
    {
        WriteXlsx(@"20260701\20260701-1042-PECF Report.xlsx", Rows("20260701-A.pdf", "20260701-B.pdf"));
        WriteXlsx(@"20260710\20260710-0939-PECF Report.xlsx", Rows("20260710-C.pdf"));

        var result = UploadReportFeed.Load(_dir);
        Assert.Equal(2, result.Report.FilesFound);
        Assert.Empty(result.Report.Skipped);
        Assert.Equal(new DateOnly(2026, 7, 1), result.Report.FirstUpload);
        Assert.Equal(new DateOnly(2026, 7, 10), result.Report.LastUpload);
        Assert.Equal(3, result.Report.RowCount);
        Assert.Equal(3, result.Table.Rows.Count);
    }

    [Fact]
    public void ACorruptFileIsSkippedAndNamedWhileTheRestLoads()
    {
        WriteXlsx(@"20260701\20260701-1042-PECF Report.xlsx", Rows("20260701-A.pdf"));
        var corrupt = Path.Combine(_dir, "20260702-0900-PECF Report.xlsx");
        File.WriteAllText(corrupt, "not a zip");   // matches the name filter, fails to read

        var result = UploadReportFeed.Load(_dir);
        Assert.Equal(2, result.Report.FilesFound);
        Assert.Single(result.Report.Skipped);
        Assert.Contains("20260702-0900-PECF Report.xlsx", result.Report.Skipped[0]);
        Assert.Equal(1, result.Report.RowCount);   // the good file still loaded
    }

    [Fact]
    public void AMissingRootIsAnEmptyResultWithANote()
    {
        var result = UploadReportFeed.Load(Path.Combine(_dir, "nope"));
        Assert.Equal(0, result.Report.FilesFound);
        Assert.Single(result.Report.Skipped);
        Assert.Contains("nope", result.Report.Skipped[0]);
        Assert.Equal(0, result.Report.RowCount);
        Assert.Empty(result.Table.Rows);
        Assert.Null(result.Report.FirstUpload);
    }
}
