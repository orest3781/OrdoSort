using System.Text.RegularExpressions;

namespace OrdoSort.Core;

/// <summary>The Turn-around dashboard's one source: a folder of
/// "YYYYMMDD-HHMM-PECF Report.xlsx" exports, in dated subfolders or not —
/// scanning is always recursive (spec feed 1). Load never throws: a missing
/// root or an unreadable file becomes a Skipped entry in the LoadReport, the
/// SweptTable.FileErrors pattern, because a batch of report files is exactly
/// the place one bad file must not take the rest down. The name filter is
/// deliberately exact — a folder full of hand-saved copies ("… - Copy.xlsx")
/// and unrelated workbooks must not leak rows into the SLA numbers.</summary>
public static partial class UploadReportFeed
{
    // The full report-name shape, anchored both ends: date, time, the fixed
    // suffix. IgnoreCase covers hand-renamed extensions (.XLSX) and casing.
    [GeneratedRegex(@"^\d{8}-\d{4}-PECF Report\.xlsx$", RegexOptions.IgnoreCase)]
    private static partial Regex ReportNameRegex();

    /// <summary>What the Sources page shows for this feed: how much was
    /// found, what was skipped and why, the upload-date span, the row count.</summary>
    public sealed record LoadReport(int FilesFound, IReadOnlyList<string> Skipped,
        DateOnly? FirstUpload, DateOnly? LastUpload, int RowCount);

    public sealed record Result(SweptTable.Table Table, LoadReport Report);

    public static bool IsReportFile(string path) =>
        ReportNameRegex().IsMatch(Path.GetFileName(path));

    /// <summary>Every matching file under root, recursively, sorted by bare
    /// filename ordinal (the YYYYMMDD-HHMM prefix makes that chronological)
    /// with the full path as tiebreak, so load order — and therefore
    /// "earliest report wins" dedupe downstream — never depends on
    /// filesystem enumeration order.</summary>
    public static IReadOnlyList<string> FindFiles(string root) =>
        Directory.EnumerateFiles(root, "*.xlsx", SearchOption.AllDirectories)
            .Where(IsReportFile)
            .OrderBy(Path.GetFileName, StringComparer.Ordinal)
            .ThenBy(p => p, StringComparer.Ordinal)
            .ToList();

    public static Result Load(string root)
    {
        IReadOnlyList<string> files;
        try
        {
            files = FindFiles(root);
        }
        catch (Exception ex)   // missing root, access denied — one note, empty result
        {
            var empty = new SweptTable.Table(Array.Empty<string>(),
                Array.Empty<SweptTable.Row>(), 0, Array.Empty<string>());
            return new Result(empty, new LoadReport(0, new[] { $"{root}: {ex.Message}" },
                null, null, 0));
        }

        var table = SweptTable.Load(files);
        var uploads = files
            .Select(TurnaroundTime.UploadTimeFromReportName)
            .Where(u => u is not null)
            .Select(u => DateOnly.FromDateTime(u!.Value))
            .ToList();

        return new Result(table, new LoadReport(files.Count, table.FileErrors,
            uploads.Count == 0 ? null : uploads.Min(),
            uploads.Count == 0 ? null : uploads.Max(),
            table.Rows.Count));
    }
}
