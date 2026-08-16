using System.Globalization;
using System.Text;

namespace OrdoSort.Core;

/// <summary>The Turn-around page's two outward surfaces (spec decision 9):
/// the two-sheet workbook Export writes, and the plain text Copy summary
/// puts on the clipboard for pasting into email. Both include every
/// set-aside count next to the figures it affects, so the denominator can
/// be defended when questioned. All formatting is invariant and pinned by
/// tests — the view model calls these, it never formats a published figure
/// itself.</summary>
public static class TurnaroundExport
{
    /// <summary>"2026-07" → "Jul". Invariant month abbreviations — the same
    /// label the month grid and the delta chip render.</summary>
    public static string MonthName(string month) =>
        DateTime.ParseExact(month, "yyyy-MM", CultureInfo.InvariantCulture)
            .ToString("MMM", CultureInfo.InvariantCulture);

    private static string F1(double v) => v.ToString("F1", CultureInfo.InvariantCulture);
    private static string N0(int v) => v.ToString("N0", CultureInfo.InvariantCulture);

    public static string BuildCopyText(TurnaroundSummary.Summary summary,
        UploadReportFeed.LoadReport report)
    {
        var o = summary.Overall;
        var sb = new StringBuilder();
        sb.Append("Turn-around time — ")
          .Append(report.FirstUpload?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? "?")
          .Append(" to ")
          .Append(report.LastUpload?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? "?")
          .Append($" ({N0(report.FilesFound)} files, {N0(report.RowCount)} rows)").Append('\n');
        sb.Append($"0-1 business days: {F1(o.ZeroToOnePercent)}% ({N0(o.ZeroToOne)} of {N0(o.Total)})")
          .Append($" · 2 days: {F1(o.TwoPercent)}% ({N0(o.TwoDays)})")
          .Append($" · 3+ days: {F1(o.ThreePlusPercent)}% ({N0(o.ThreePlus)})").Append('\n');
        sb.Append(string.Join(" · ", summary.ByMonth.Select(m =>
            $"{MonthName(m.Month)}: {F1(m.Counts.ZeroToOnePercent)}% in 0-1"))).Append('\n');
        sb.Append("By source (0-1 share): ").Append(string.Join(" · ",
            summary.BySource.Select(s => $"{s.SourceType} {F1(s.Counts.ZeroToOnePercent)}%"))).Append('\n');
        sb.Append($"Set aside: {N0(summary.DuplicateRows)} duplicates")
          .Append($" · {N0(summary.FutureDated)} future-dated")
          .Append($" · {N0(summary.NoDate)} without a date");
        foreach (var ig in summary.Ignored)
            sb.Append($" · {ig.Value} {N0(ig.Count)} ignored");
        return sb.ToString();
    }

    public static void Write(string path, TurnaroundSummary.Summary summary,
        UploadReportFeed.LoadReport report, string sourceFolder)
    {
        XlsxWriter.Write(path, new[]
        {
            new XlsxWriter.Sheet("Summary", SummaryRows(summary, report, sourceFolder)),
            new XlsxWriter.Sheet("Documents", DetailRows(summary)),
        });
    }

    private static IReadOnlyList<IReadOnlyList<object?>> SummaryRows(
        TurnaroundSummary.Summary summary, UploadReportFeed.LoadReport report, string sourceFolder)
    {
        var o = summary.Overall;
        var rows = new List<IReadOnlyList<object?>>
        {
            new object?[] { "Turn-around time" },
            new object?[] { "Source folder", sourceFolder },
            new object?[] { "Files found", report.FilesFound },
            new object?[] { "Files skipped", report.Skipped.Count },
            new object?[] { "Rows", report.RowCount },
            new object?[] { "Upload span",
                $"{report.FirstUpload?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)} to {report.LastUpload?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)}" },
            Array.Empty<object?>(),
            new object?[] { "Bucket", "Documents", "Percent" },
            new object?[] { "Same day", o.SameDay, Math.Round(o.Total == 0 ? 0 : 100.0 * o.SameDay / o.Total, 2) },
            new object?[] { "1 business day", o.OneDay, Math.Round(o.Total == 0 ? 0 : 100.0 * o.OneDay / o.Total, 2) },
            new object?[] { "2 business days", o.TwoDays, Math.Round(o.TwoPercent, 2) },
            new object?[] { "3+ business days", o.ThreePlus, Math.Round(o.ThreePlusPercent, 2) },
            new object?[] { "0-1 business days", o.ZeroToOne, Math.Round(o.ZeroToOnePercent, 2) },
            new object?[] { "Measurable documents", o.Total },
            Array.Empty<object?>(),
            new object?[] { "Month", "Documents", "0-1 %", "2 days", "3+ days" },
        };
        rows.AddRange(summary.ByMonth.Select(m => (IReadOnlyList<object?>)new object?[]
        {
            MonthName(m.Month), m.Counts.Total, Math.Round(m.Counts.ZeroToOnePercent, 2),
            m.Counts.TwoDays, m.Counts.ThreePlus,
        }));
        rows.Add(Array.Empty<object?>());
        rows.Add(new object?[] { "Source", "Documents", "0-1 %", "2 days", "3+ days" });
        rows.AddRange(summary.BySource.Select(s => (IReadOnlyList<object?>)new object?[]
        {
            s.SourceType, s.Counts.Total, Math.Round(s.Counts.ZeroToOnePercent, 2),
            s.Counts.TwoDays, s.Counts.ThreePlus,
        }));
        rows.Add(Array.Empty<object?>());
        rows.Add(new object?[] { "Set aside", "Count" });
        rows.Add(new object?[] { "Duplicates", summary.DuplicateRows });
        rows.Add(new object?[] { "Future-dated", summary.FutureDated });
        rows.Add(new object?[] { "Without a date", summary.NoDate });
        rows.AddRange(summary.Ignored.Select(ig => (IReadOnlyList<object?>)new object?[]
        {
            $"Ignored: {ig.Value}", ig.Count,
        }));
        return rows;
    }

    /// <summary>Sheet 2's rows — internal-shaped but public so the test can
    /// pin the content without re-reading a second sheet the minimal reader
    /// can't reach. Dates pre-formatted, invariant.</summary>
    public static IReadOnlyList<IReadOnlyList<object?>> DetailRows(TurnaroundSummary.Summary summary)
    {
        var rows = new List<IReadOnlyList<object?>>
        {
            new object?[] { "FileName", "SourceType", "Pagecount", "Destination",
                "DocDate", "UploadDate", "BusinessDays", "Bucket", "SourceReport" },
        };
        rows.AddRange(summary.Docs.Select(d => (IReadOnlyList<object?>)new object?[]
        {
            d.FileName, d.SourceType, d.Pagecount, d.Destination,
            d.DocDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            d.UploadDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            d.BusinessDays.ToString(CultureInfo.InvariantCulture),
            BucketLabel(d.Bucket),
            Path.GetFileName(d.SourceFile),
        }));
        return rows;
    }

    public static string BucketLabel(TurnaroundSummary.Bucket bucket) => bucket switch
    {
        TurnaroundSummary.Bucket.SameDay => "Same day",
        TurnaroundSummary.Bucket.OneDay => "1 day",
        TurnaroundSummary.Bucket.TwoDays => "2 days",
        _ => "3+ days",
    };
}
