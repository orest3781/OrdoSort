using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace OrdoSort.Core;

/// <summary>
/// Turn-around Time is entirely a filename-dating exercise: a PECF report's
/// own filename carries the moment it was uploaded (e.g.
/// "20250303-1144-PECF Report.xlsx"), and each document row inside it names
/// the document it describes with the document's own date baked into the
/// front of the FileName cell (e.g.
/// "20250303-JASMIN-KRISTEN-COPR020525-15350.pdf"). TAT is just the gap
/// between those two dates. Nothing here touches disk except ExportCsv —
/// SweptTable (Task 2) already did the file reading, and every row it hands
/// back carries every union header, so Compute only ever indexes Cells, it
/// never has to guess whether a column exists.
///
/// Every parse and format below runs through CultureInfo.InvariantCulture —
/// CurrentCulture must never leak into a computed date, an average, or a
/// written CSV, the same policy CultureInvariantDatesTests pins for the rest
/// of the repo. A shop's PECF exports come from whatever locale that
/// station's Windows install happens to be running, and TAT numbers that
/// silently shift between two runs of the same report would be worse than
/// useless.
/// </summary>
public static partial class TurnaroundTime
{
    // Report filenames carry an upload timestamp: 8 date digits, a dash, 4
    // time digits. The time group must not be followed by another digit —
    // otherwise a 12-digit run with no dash reads as an 8+4 split it never
    // intended (and, via (?!\d) rather than a literal non-digit, still
    // matches when the time group ends the string, no extension needed).
    [GeneratedRegex(@"^(\d{8})-(\d{4})(?!\d)")]
    private static partial Regex ReportUploadRegex();

    // Fallback for report filenames with no time component — just the date.
    [GeneratedRegex(@"^(\d{8})(?!\d)")]
    private static partial Regex ReportDateOnlyRegex();

    // Document FileName cells: 8 date digits then a dash. Covers both the
    // raw scanner form ("20250303--ID.pdf", a literal double dash) and the
    // renamed form ("20250303-NAME-ID.pdf", a single dash) — either way the
    // date is the leading 8 digits up to the first dash.
    [GeneratedRegex(@"^(\d{8})-")]
    private static partial Regex DocDateRegex();

    /// <summary>The report file's own name is its upload timestamp. Tries
    /// "yyyyMMdd-HHmm" first; if the time half doesn't parse (bad hour,
    /// bad minute — still a valid-looking file someone will want a TAT for)
    /// falls back to the date alone at midnight rather than dropping the
    /// row. Anything that isn't even date-shaped, or a date-shaped run that
    /// isn't a real calendar date, is null — never a thrown exception, since
    /// a batch of report files is exactly the place one oddly-named file
    /// shouldn't take the rest down.</summary>
    public static DateTime? UploadTimeFromReportName(string path)
    {
        var name = Path.GetFileName(path);

        var withTime = ReportUploadRegex().Match(name);
        if (withTime.Success)
        {
            var stamp = $"{withTime.Groups[1].Value}-{withTime.Groups[2].Value}";
            if (DateTime.TryParseExact(stamp, "yyyyMMdd-HHmm", CultureInfo.InvariantCulture,
                    DateTimeStyles.None, out var dt))
                return dt;
        }

        var dateOnly = ReportDateOnlyRegex().Match(name);
        if (dateOnly.Success &&
            DateTime.TryParseExact(dateOnly.Groups[1].Value, "yyyyMMdd", CultureInfo.InvariantCulture,
                DateTimeStyles.None, out var midnight))
            return midnight;

        return null;
    }

    /// <summary>The document's own date, read off the front of its FileName
    /// cell. Cells sometimes carry a full path rather than a bare name, so
    /// Path.GetFileName runs first regardless. Anything that doesn't start
    /// with an 8-digit-dash run, or whose 8 digits aren't a real calendar
    /// date, is null — the row still counts (ComputeAll never drops it), it
    /// just can't contribute a TAT number.</summary>
    public static DateOnly? ExtractDocDate(string filenameCell)
    {
        var name = Path.GetFileName(filenameCell);
        var match = DocDateRegex().Match(name);
        if (!match.Success) return null;

        return DateOnly.TryParseExact(match.Groups[1].Value, "yyyyMMdd", CultureInfo.InvariantCulture,
            DateTimeStyles.None, out var date)
            ? date
            : null;
    }

    /// <summary>One document's computed turn-around: which report it came
    /// from, when it was uploaded, when it was created, and the gap between
    /// them. Negative TatDays is left as-is rather than clamped to zero — a
    /// document dated after its own report's upload is honest data (a typo,
    /// a backdated scan, whatever), and clamping it would hide exactly the
    /// row someone reviewing the report most needs to see.</summary>
    public sealed record DocRow(string SourceFile, string FileName, DateOnly? DocDate, DateTime? UploadDate,
        int? TatDays, string Category);

    /// <summary>Turns one SweptTable row into one DocRow. filenameColumn and
    /// categoryColumn are picked by the caller from the union header set
    /// (headers vary per shop, so column names are never hardcoded here); a
    /// column name that isn't actually a key in this row's Cells — same
    /// situation as a header this row's own source file never had — reads
    /// as "", exactly like a header SweptTable itself never saw.</summary>
    public static DocRow Compute(SweptTable.Row row, string filenameColumn, string? categoryColumn)
    {
        var fileName = row.Cells.TryGetValue(filenameColumn, out var fn) ? fn : "";
        var uploadDate = UploadTimeFromReportName(row.SourceFile);
        var docDate = ExtractDocDate(fileName);
        var tatDays = docDate is not null && uploadDate is not null
            ? (uploadDate.Value.Date - docDate.Value.ToDateTime(TimeOnly.MinValue)).Days
            : (int?)null;
        var category = categoryColumn is not null && row.Cells.TryGetValue(categoryColumn, out var cat)
            ? cat.Trim()
            : "";

        return new DocRow(row.SourceFile, fileName, docDate, uploadDate, tatDays, category);
    }

    /// <summary>Compute over every row, input order preserved. Rows are
    /// never dropped for an unparseable date on either side — TatDays rides
    /// along as null, so the window that lists these rows still shows them
    /// and counts them, rather than silently shrinking the report.</summary>
    public static IReadOnlyList<DocRow> ComputeAll(SweptTable.Table table, string filenameColumn,
        string? categoryColumn) =>
        table.Rows.Select(row => Compute(row, filenameColumn, categoryColumn)).ToList();

    /// <summary>One period's TAT summary — a day or an ISO week, depending
    /// on which grouping produced it.</summary>
    public sealed record PeriodAverage(string Period, double AverageDays, int Count);

    /// <summary>Only rows with a computed TatDays can average into anything
    /// — a row with an unparseable date contributes to nobody's mean.
    /// Grouped by the calendar date the report was uploaded (not the
    /// document date: TAT is a property of when work landed, not when the
    /// document itself was dated), ascending.</summary>
    public static IReadOnlyList<PeriodAverage> DailyAverages(IReadOnlyList<DocRow> rows) =>
        rows.Where(r => r.TatDays is not null)
            .GroupBy(r => r.UploadDate!.Value.Date)
            .OrderBy(g => g.Key)
            .Select(g => new PeriodAverage(
                g.Key.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                g.Average(r => r.TatDays!.Value),
                g.Count()))
            .ToList();

    /// <summary>Same grouping idea as DailyAverages but by ISO week (Monday-
    /// start, the week-53-belongs-to-whichever-year-owns-its-Thursday rule),
    /// since a per-day view is too granular to spot a trend and a per-month
    /// view is too coarse to act on. Period reads "2025-W10".</summary>
    public static IReadOnlyList<PeriodAverage> WeeklyAverages(IReadOnlyList<DocRow> rows) =>
        rows.Where(r => r.TatDays is not null)
            .GroupBy(r =>
            {
                var date = r.UploadDate!.Value.Date;
                return (Year: ISOWeek.GetYear(date), Week: ISOWeek.GetWeekOfYear(date));
            })
            .OrderBy(g => g.Key.Year).ThenBy(g => g.Key.Week)
            .Select(g => new PeriodAverage(
                $"{g.Key.Year.ToString(CultureInfo.InvariantCulture)}-W{g.Key.Week.ToString("00", CultureInfo.InvariantCulture)}",
                g.Average(r => r.TatDays!.Value),
                g.Count()))
            .ToList();

    /// <summary>One category's TAT summary, plus how many of its rows blew
    /// past the threshold — the count a reviewer actually cares about, not
    /// just the average that can hide a handful of bad outliers.</summary>
    public sealed record CategoryBreakdown(string Category, double AverageDays, int Count, int OverThreshold);

    /// <summary>Grouped by Category exactly as it came off the row — ordinal,
    /// case-sensitive, no trimming-away-the-differences, matching the rest
    /// of the repo's no-normalization stance (SweptTable's own header union
    /// takes the same line). Sorted the same way, ordinally, so the result
    /// order doesn't depend on CurrentCulture either.</summary>
    public static IReadOnlyList<CategoryBreakdown> ByCategory(IReadOnlyList<DocRow> rows, int thresholdDays) =>
        rows.Where(r => r.TatDays is not null)
            .GroupBy(r => r.Category, StringComparer.Ordinal)
            .OrderBy(g => g.Key, StringComparer.Ordinal)
            .Select(g => new CategoryBreakdown(
                g.Key,
                g.Average(r => r.TatDays!.Value),
                g.Count(),
                g.Count(r => r.TatDays!.Value > thresholdDays)))
            .ToList();

    /// <summary>Rows whose TAT strictly exceeds the threshold — a row
    /// exactly AT the threshold hasn't blown it, only past it counts. Input
    /// order, same as ComputeAll.</summary>
    public static IReadOnlyList<DocRow> ExceedingThreshold(IReadOnlyList<DocRow> rows, int thresholdDays) =>
        rows.Where(r => r.TatDays is not null && r.TatDays.Value > thresholdDays).ToList();

    /// <summary>The Documents-tab export, mirroring History.ExportCsv: UTF-8
    /// with a BOM so Excel opens it without asking, every field routed
    /// through Csv.EscapeField so a document name that happens to start with
    /// "=" can't turn into a formula-injection payload. Returns the row
    /// count so the caller can report "N rows exported" without a second
    /// pass over rows.</summary>
    public static int ExportCsv(IReadOnlyList<DocRow> rows, string dest)
    {
        using var writer = new StreamWriter(dest, false, new UTF8Encoding(true));
        writer.WriteLine(Csv.WriteRow(new[]
            { "source_report", "file_name", "category", "doc_date", "upload_date", "tat_days" }));
        foreach (var row in rows)
        {
            writer.WriteLine(Csv.WriteRow(new[]
            {
                Path.GetFileName(row.SourceFile),
                row.FileName,
                row.Category,
                row.DocDate?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? "",
                row.UploadDate?.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture) ?? "",
                row.TatDays?.ToString(CultureInfo.InvariantCulture) ?? ""
            }));
        }
        return rows.Count;
    }
}
