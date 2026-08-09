using System.Globalization;
using System.Text;

namespace OrdoSort.Core;

/// <summary>
/// Production is the shop's own answer to "how much did we get through
/// today, broken down by who did it and where it came from": group the
/// swept rows by whichever header columns the user picks (a shift lead
/// reading the report picks SOURCE-FOLDER + employee; a manager might pick
/// just SOURCE-FOLDER), count records per group, and sum whichever numeric
/// columns matter (PDF-PAGE-COUNT, say). SweptTable.Load (Task 2) already
/// unions every file's headers and fills every row's Cells dictionary with
/// every union header, so Group and WithDerived below only ever index
/// Cells — they never have to guess whether a column exists.
///
/// FILE-OWNER cells are domain-prefixed ("ACME\user1") and
/// DATE-TIME is US-formatted free text ("4/1/2025 7:55") — neither is
/// directly useful as a group key or a sortable value, hence WithDerived
/// peeling an Employee name and a Date/Hour pair off them before Group ever
/// runs. Every parse and format below runs through
/// CultureInfo.InvariantCulture, the same policy CultureInvariantDatesTests
/// pins for the rest of the repo: a shop's swept CSVs come off whatever
/// locale that station's Windows happens to be running, and a Production
/// total that silently shifted between two runs of the same data would be
/// worse than useless.
/// </summary>
public static class ProductionReport
{
    private static readonly string[] DateTimeFormats = { "M/d/yyyy H:mm", "M/d/yyyy H:mm:ss", "M/d/yyyy" };

    /// <summary>Appends up to three derived columns onto a copy of the
    /// table — Employee (from ownerColumn) and Date/Hour (from
    /// datetimeColumn) — so the caller can Group() on them exactly like any
    /// real header. Either source column is optional and independent: an
    /// empty name, or a name that isn't actually in Headers, just skips
    /// that derivation rather than adding an all-blank column nobody asked
    /// for. table itself is untouched — SweptTable's records are meant to
    /// be shared across a run, so this always builds new Headers and new
    /// per-row dictionaries rather than mutating anything reachable from
    /// table.</summary>
    public static SweptTable.Table WithDerived(SweptTable.Table table, string ownerColumn, string datetimeColumn)
    {
        var headerSet = new HashSet<string>(table.Headers, StringComparer.Ordinal);
        var deriveEmployee = ownerColumn.Length > 0 && headerSet.Contains(ownerColumn);
        var deriveDateHour = datetimeColumn.Length > 0 && headerSet.Contains(datetimeColumn);

        // Collision names are decided once, up front, against the ORIGINAL
        // header set — a shop whose sweep already has its own "Employee" or
        // "Date" column (some do) gets the derived version parked under
        // "(derived)" rather than silently overwriting a real column.
        var employeeName = deriveEmployee ? (headerSet.Contains("Employee") ? "Employee (derived)" : "Employee") : null;
        var dateName = deriveDateHour ? (headerSet.Contains("Date") ? "Date (derived)" : "Date") : null;
        var hourName = deriveDateHour ? (headerSet.Contains("Hour") ? "Hour (derived)" : "Hour") : null;

        var headers = new List<string>(table.Headers);
        if (employeeName is not null) headers.Add(employeeName);
        if (dateName is not null) headers.Add(dateName);
        if (hourName is not null) headers.Add(hourName);

        var rows = new List<SweptTable.Row>(table.Rows.Count);
        foreach (var row in table.Rows)
        {
            var cells = new Dictionary<string, string>(row.Cells, StringComparer.Ordinal);
            if (employeeName is not null)
                cells[employeeName] = DeriveEmployee(row.Cells.TryGetValue(ownerColumn, out var owner) ? owner : "");
            if (dateName is not null || hourName is not null)
            {
                var (date, hour) = DeriveDateHour(row.Cells.TryGetValue(datetimeColumn, out var stamp) ? stamp : "");
                if (dateName is not null) cells[dateName] = date;
                if (hourName is not null) cells[hourName] = hour;
            }
            rows.Add(new SweptTable.Row(cells, row.SourceFile));
        }

        return new SweptTable.Table(headers, rows, table.FilesRead, table.FileErrors);
    }

    /// <summary>ACME\user1 -> user1: everything after the last
    /// backslash, trimmed. A value with no backslash at all (some shops'
    /// FILE-OWNER is already a bare username) is trimmed and kept whole
    /// rather than treated as unparseable.</summary>
    private static string DeriveEmployee(string owner)
    {
        var lastSlash = owner.LastIndexOf('\\');
        return (lastSlash >= 0 ? owner[(lastSlash + 1)..] : owner).Trim();
    }

    /// <summary>The three exact patterns a swept DATE-TIME cell shows up in,
    /// tried in order, then a generic invariant parse as a last resort for
    /// anything shaped a little differently. Hour is zero-padded ("07", not
    /// "7") specifically so a text sort of the column matches a time sort —
    /// the whole point of splitting it out of Date in the first place.
    /// Anything that parses none of these ways yields two blank cells
    /// rather than throwing or dropping the row.</summary>
    private static (string Date, string Hour) DeriveDateHour(string cell)
    {
        var parsed = DateTime.TryParseExact(cell, DateTimeFormats, CultureInfo.InvariantCulture,
                DateTimeStyles.None, out var dt)
            || DateTime.TryParse(cell, CultureInfo.InvariantCulture, DateTimeStyles.None, out dt);

        return parsed
            ? (dt.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), dt.ToString("HH", CultureInfo.InvariantCulture))
            : ("", "");
    }

    /// <summary>One group's tally: Key aligned index-for-index with the
    /// groupByColumns the caller asked for (so [SOURCE-FOLDER, Employee]
    /// produces Key[0] = folder, Key[1] = employee), Count of matching
    /// rows, and Sums keyed by sum-column name.</summary>
    public sealed record GroupResult(IReadOnlyList<string> Key, int Count, IReadOnlyDictionary<string, double> Sums);

    /// <summary>Groups table's rows by the caller-picked column names,
    /// counting records and summing the caller-picked numeric columns per
    /// group. Column names are never hardcoded — a shop's headers are
    /// whatever its sweep produced — so both lists are pure user picks,
    /// including the degenerate cases: an empty groupByColumns collapses
    /// every row into one totals group (Key = []), and a stale group column
    /// name that isn't actually a key in any row's Cells contributes ""
    /// for every row rather than throwing (SweptTable already guarantees
    /// every real header is a key in every row's Cells, so a lookup miss
    /// can only be a leftover config pick from a run against different
    /// files).
    ///
    /// Keys are Trim()med but otherwise compared ordinally —
    /// "EMAILS_APPEAL" and " EMAILS_APPEAL " are the same group,
    /// "EMAILS_APPEAL" and "emails_appeal" are not, matching the rest of
    /// the repo's no-normalization stance (SweptTable's own header union
    /// takes the same line). Sums tolerate garbage: a blank or non-numeric
    /// sum cell contributes 0 rather than throwing, and "1,234.5"
    /// (thousands separator, as Excel often quotes a number back out)
    /// parses to 1234.5. Sorted ordinally, key column by key column in
    /// pick order — a [SOURCE-FOLDER, Employee] pick sorts by folder then
    /// by employee within each folder, reading as "groups per category,
    /// broken up by employee".</summary>
    public static List<GroupResult> Group(
        SweptTable.Table table, IReadOnlyList<string> groupByColumns, IReadOnlyList<string> sumColumns)
    {
        var order = new List<IReadOnlyList<string>>();
        var counts = new Dictionary<IReadOnlyList<string>, int>(KeyComparer.Instance);
        var sums = new Dictionary<IReadOnlyList<string>, double[]>(KeyComparer.Instance);

        foreach (var row in table.Rows)
        {
            var key = groupByColumns
                .Select(col => (row.Cells.TryGetValue(col, out var v) ? v : "").Trim())
                .ToList();

            if (!counts.ContainsKey(key))
            {
                order.Add(key);
                counts[key] = 0;
                sums[key] = new double[sumColumns.Count];
            }
            counts[key]++;

            var rowSums = sums[key];
            for (var i = 0; i < sumColumns.Count; i++)
            {
                var cell = row.Cells.TryGetValue(sumColumns[i], out var v) ? v : "";
                if (double.TryParse(cell, NumberStyles.Float | NumberStyles.AllowThousands,
                        CultureInfo.InvariantCulture, out var parsed))
                    rowSums[i] += parsed;
            }
        }

        var results = order
            .Select(key => new GroupResult(
                key,
                counts[key],
                (IReadOnlyDictionary<string, double>)sumColumns
                    .Select((name, i) => (name, value: sums[key][i]))
                    .ToDictionary(t => t.name, t => t.value, StringComparer.Ordinal)))
            .ToList();

        results.Sort((a, b) => CompareKeys(a.Key, b.Key));
        return results;
    }

    /// <summary>Ordinal, element by element, in pick order — the same
    /// comparison String.CompareOrdinal gives a single string, extended to
    /// a list so a [SOURCE-FOLDER, Employee] pick's sort reads as "by
    /// folder, then by employee" rather than some hash-order jumble.
    /// groupByColumns empty means every key is [], so this never runs past
    /// the loop and every GroupResult compares equal — there is only ever
    /// one to sort anyway.</summary>
    private static int CompareKeys(IReadOnlyList<string> a, IReadOnlyList<string> b)
    {
        var n = Math.Min(a.Count, b.Count);
        for (var i = 0; i < n; i++)
        {
            var c = string.CompareOrdinal(a[i], b[i]);
            if (c != 0) return c;
        }
        return a.Count - b.Count;
    }

    /// <summary>Structural equality/hashing for a group key. .NET's default
    /// dictionary equality for a List is reference equality, which would
    /// make every freshly-built key its own dictionary entry even when two
    /// rows land in the same group.</summary>
    private sealed class KeyComparer : IEqualityComparer<IReadOnlyList<string>>
    {
        public static readonly KeyComparer Instance = new();

        public bool Equals(IReadOnlyList<string>? x, IReadOnlyList<string>? y)
        {
            if (ReferenceEquals(x, y)) return true;
            if (x is null || y is null || x.Count != y.Count) return false;
            for (var i = 0; i < x.Count; i++)
                if (!string.Equals(x[i], y[i], StringComparison.Ordinal)) return false;
            return true;
        }

        public int GetHashCode(IReadOnlyList<string> obj)
        {
            var hash = new HashCode();
            foreach (var s in obj) hash.Add(s, StringComparer.Ordinal);
            return hash.ToHashCode();
        }
    }

    /// <summary>The Production export, mirroring History.ExportCsv and
    /// TurnaroundTime.ExportCsv: UTF-8 with a BOM so Excel opens it without
    /// asking, every field routed through Csv.WriteRow so a group value
    /// that happens to contain a comma (a category folder name, say)
    /// can't break the row shape. Header is the group column names, then
    /// record_count, then the sum column names — the same left-to-right
    /// reading order Group's own Key/Count/Sums line up in. Sum values are
    /// formatted "0.##" so a whole-number total prints as "5", not "5.00",
    /// while a genuine fraction still shows. Returns the row count so the
    /// caller can report "N groups exported" without a second pass over
    /// results.</summary>
    public static int ExportCsv(
        List<GroupResult> results, IReadOnlyList<string> groupByColumns, IReadOnlyList<string> sumColumns,
        string dest)
    {
        using var writer = new StreamWriter(dest, false, new UTF8Encoding(true));
        writer.WriteLine(Csv.WriteRow(groupByColumns.Append("record_count").Concat(sumColumns)));
        foreach (var result in results)
        {
            var fields = result.Key
                .Append(result.Count.ToString(CultureInfo.InvariantCulture))
                .Concat(sumColumns.Select(name => result.Sums[name].ToString("0.##", CultureInfo.InvariantCulture)));
            writer.WriteLine(Csv.WriteRow(fields));
        }
        return results.Count;
    }
}
