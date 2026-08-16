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

    public sealed record Summary(
        IReadOnlyList<Doc> Docs,
        BucketCounts Overall,
        IReadOnlyList<MonthLine> ByMonth,
        IReadOnlyList<SourceLine> BySource,
        IReadOnlyList<WeekLine> ByWeek,
        IReadOnlyList<IgnoredSource> Ignored,
        int DuplicateRows,
        int FutureDated,
        int NoDate);

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
}
