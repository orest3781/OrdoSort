using System.Globalization;

namespace OrdoSort.Core;

/// <summary>The Turn-around dashboard's engine (spec rules 1–5): dedupe by
/// filename with the earliest report winning, set ignored sources aside with
/// counts, classify every measurable document into the workbook's
/// business-day buckets, and aggregate — overall, by month, by source, by
/// ISO week. Headline metric is business days, superseding the 08-11 spec's
/// calendar-day decision: the workbook this mimics computes
/// busday_count(FileDate, UploadDate), verified on 23,565 of 23,672 live
/// rows. Pure computation on SweptTable rows; nothing here touches disk.</summary>
public static class TurnaroundSummary
{
    // The PECF export's fixed layout (spec feed 1). SweptTable's union rows
    // make a missing column read as "", never throw.
    public const string FileNameColumn = "FileName";
    public const string SourceTypeColumn = "SourceType";
    public const string PagecountColumn = "Pagecount";
    public const string DestinationColumn = "Destination";

    public enum Bucket { SameDay, OneDay, TwoDays, ThreePlus }

    /// <summary>One measurable, deduplicated, non-ignored document.</summary>
    public sealed record Doc(string FileName, string SourceType, string Pagecount,
        string Destination, DateOnly DocDate, DateOnly UploadDate, int BusinessDays,
        Bucket Bucket, string SourceFile);

    /// <summary>The four bucket counts plus the derived figures every panel
    /// renders. Percentages of an empty population read 0, not NaN — an
    /// empty month must render as dashes, not poison a binding.</summary>
    public sealed record BucketCounts(int SameDay, int OneDay, int TwoDays, int ThreePlus)
    {
        public int Total => SameDay + OneDay + TwoDays + ThreePlus;
        public int ZeroToOne => SameDay + OneDay;
        public double ZeroToOnePercent => Total == 0 ? 0 : 100.0 * ZeroToOne / Total;
        public double TwoPercent => Total == 0 ? 0 : 100.0 * TwoDays / Total;
        public double ThreePlusPercent => Total == 0 ? 0 : 100.0 * ThreePlus / Total;
    }

    public sealed record IgnoredSource(string Value, int Count);
    public sealed record MonthLine(string Month, BucketCounts Counts);     // "2026-07"
    public sealed record SourceLine(string SourceType, BucketCounts Counts);
    public sealed record WeekLine(string Week, BucketCounts Counts);       // "2026-W28"

    /// <summary>Every set-aside count is derived from its matching detail
    /// list below (DuplicateRows.Count, etc.) rather than tracked
    /// separately, so a count and the rows a click-through would show for it
    /// can never disagree (spec decision 2: each count is clickable to
    /// inspect the rows behind it). Ignored (per-value, post-dedupe) is the
    /// figure every summary card renders; IgnoreList.Discover's counts are
    /// raw pre-dedupe totals and must not be mixed with these on one card.</summary>
    public sealed record Summary(
        IReadOnlyList<Doc> Docs,
        BucketCounts Overall,
        IReadOnlyList<MonthLine> ByMonth,
        IReadOnlyList<SourceLine> BySource,
        IReadOnlyList<WeekLine> ByWeek,
        IReadOnlyList<IgnoredSource> Ignored,
        IReadOnlyList<SweptTable.Row> DuplicateRowsDetail,
        IReadOnlyList<SweptTable.Row> FutureDatedDetail,
        IReadOnlyList<SweptTable.Row> NoDateDetail,
        IReadOnlyList<SweptTable.Row> IgnoredDetail)
    {
        public int DuplicateRows => DuplicateRowsDetail.Count;
        public int FutureDated => FutureDatedDetail.Count;
        public int NoDate => NoDateDetail.Count;
    }

    /// <summary>Weekdays in [from, to) — numpy busday_count semantics, which
    /// is what the workbook's TAT column is. Saturday-dated work uploaded
    /// Monday reads 0: no business day passed. Reversed dates count
    /// negative, but Compute never classifies those — future-dated documents
    /// are excluded by calendar comparison first (spec rule 4). The walk is
    /// linear in the gap; real gaps are days, not decades.</summary>
    public static int BusinessDaysBetween(DateOnly from, DateOnly to)
    {
        if (to < from) return -BusinessDaysBetween(to, from);
        var days = 0;
        for (var d = from; d < to; d = d.AddDays(1))
            if (d.DayOfWeek is not DayOfWeek.Saturday and not DayOfWeek.Sunday) days++;
        return days;
    }

    /// <summary>The workbook's four Turnaround values by their business-day
    /// count. Callers guarantee non-negative input (see BusinessDaysBetween).</summary>
    public static Bucket Classify(int businessDays) => businessDays switch
    {
        0 => Bucket.SameDay,
        1 => Bucket.OneDay,
        2 => Bucket.TwoDays,
        _ => Bucket.ThreePlus,
    };

    /// <summary>The whole pipeline, in the order the spec's verified
    /// reference figures were derived: sort by upload time so the earliest
    /// report wins dedupe; dedupe by FileName (blank names never merge —
    /// each blank row still counts, under NoDate); set ignored sources
    /// aside whole, counted per value; then dates — a row missing either
    /// date is NoDate, a document dated after its upload is FutureDated
    /// (calendar comparison, spec rule 4 — never coerced, never classified);
    /// everything left is measurable and aggregates four ways.</summary>
    public static Summary Compute(SweptTable.Table table, IgnoreList ignoredSources)
    {
        // 1. Earliest report first; original index as tiebreak keeps this stable.
        var ordered = table.Rows
            .Select((row, i) => (Row: row,
                Upload: TurnaroundTime.UploadTimeFromReportName(row.SourceFile), Index: i))
            .OrderBy(r => r.Upload ?? DateTime.MaxValue)
            .ThenBy(r => r.Index)
            .ToList();

        // 2. Dedupe by FileName cell, ordinal. Blank names are not identities.
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var duplicateRowsDetail = new List<SweptTable.Row>();
        var kept = new List<(SweptTable.Row Row, DateTime? Upload)>();
        foreach (var (row, upload, _) in ordered)
        {
            var name = Cell(row, FileNameColumn);
            if (name.Length > 0 && !seen.Add(name)) { duplicateRowsDetail.Add(row); continue; }
            kept.Add((row, upload));
        }

        // 3. Ignored sources: set aside whole, counted per value.
        var ignoredCounts = new Dictionary<string, int>(StringComparer.Ordinal);
        var ignoredDetail = new List<SweptTable.Row>();
        var live = new List<(SweptTable.Row Row, DateTime? Upload)>();
        foreach (var item in kept)
        {
            var source = Cell(item.Row, SourceTypeColumn);
            if (ignoredSources.IsIgnored(source))
            {
                ignoredCounts[source] = ignoredCounts.GetValueOrDefault(source) + 1;
                ignoredDetail.Add(item.Row);
            }
            else live.Add(item);
        }

        // 4–6. Dates, exclusions, classification.
        var docs = new List<Doc>();
        var noDateDetail = new List<SweptTable.Row>();
        var futureDatedDetail = new List<SweptTable.Row>();
        foreach (var (row, upload) in live)
        {
            var fileName = Cell(row, FileNameColumn);
            var docDate = DocumentDate.Parse(fileName);
            if (docDate is null || upload is null) { noDateDetail.Add(row); continue; }

            var uploadDate = DateOnly.FromDateTime(upload.Value);
            if (uploadDate < docDate.Value) { futureDatedDetail.Add(row); continue; }

            var busDays = BusinessDaysBetween(docDate.Value, uploadDate);
            docs.Add(new Doc(fileName, Cell(row, SourceTypeColumn),
                Cell(row, PagecountColumn), Cell(row, DestinationColumn),
                docDate.Value, uploadDate, busDays, Classify(busDays), row.SourceFile));
        }

        return new Summary(
            docs,
            CountBuckets(docs),
            docs.GroupBy(d => d.UploadDate.ToString("yyyy-MM", CultureInfo.InvariantCulture))
                .OrderBy(g => g.Key, StringComparer.Ordinal)
                .Select(g => new MonthLine(g.Key, CountBuckets(g.ToList())))
                .ToList(),
            docs.GroupBy(d => d.SourceType, StringComparer.Ordinal)
                .Select(g => new SourceLine(g.Key, CountBuckets(g.ToList())))
                .OrderByDescending(s => s.Counts.Total)
                .ThenBy(s => s.SourceType, StringComparer.Ordinal)
                .ToList(),
            docs.GroupBy(d =>
                {
                    var date = d.UploadDate.ToDateTime(TimeOnly.MinValue);
                    return (Year: ISOWeek.GetYear(date), Week: ISOWeek.GetWeekOfYear(date));
                })
                .OrderBy(g => g.Key.Year).ThenBy(g => g.Key.Week)
                .Select(g => new WeekLine(
                    $"{g.Key.Year.ToString(CultureInfo.InvariantCulture)}-W{g.Key.Week.ToString("00", CultureInfo.InvariantCulture)}",
                    CountBuckets(g.ToList())))
                .ToList(),
            ignoredCounts
                .OrderByDescending(kv => kv.Value)
                .ThenBy(kv => kv.Key, StringComparer.Ordinal)
                .Select(kv => new IgnoredSource(kv.Key, kv.Value))
                .ToList(),
            duplicateRowsDetail, futureDatedDetail, noDateDetail, ignoredDetail);
    }

    private static string Cell(SweptTable.Row row, string column) =>
        row.Cells.TryGetValue(column, out var value) ? value : "";

    private static BucketCounts CountBuckets(IReadOnlyList<Doc> docs) => new(
        docs.Count(d => d.Bucket == Bucket.SameDay),
        docs.Count(d => d.Bucket == Bucket.OneDay),
        docs.Count(d => d.Bucket == Bucket.TwoDays),
        docs.Count(d => d.Bucket == Bucket.ThreePlus));
}
