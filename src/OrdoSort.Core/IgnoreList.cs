namespace OrdoSort.Core;

/// <summary>The set-aside rule shared by both dashboards (spec decision 7):
/// some values in the source data belong to processes a report doesn't cover
/// (ECAA today; others later), so instead of a hard-coded rule per value,
/// the set of values discovered in the loaded data becomes a checklist and
/// unchecking one removes it from every figure — while its count stays on
/// screen, so absent data and deliberately excluded data are never confused.
/// Membership is ordinal, never normalized. The list itself persists as
/// Config.TatIgnoredSources (and, in Phase 3, ProductionIgnoredCategories).</summary>
public sealed class IgnoreList
{
    private readonly HashSet<string> _ignored;

    /// <summary>Distinct ignored values, first-seen order — exactly what
    /// gets written back to config.</summary>
    public IReadOnlyList<string> Ignored { get; }

    public IgnoreList(IEnumerable<string> ignoredValues)
    {
        _ignored = new HashSet<string>(StringComparer.Ordinal);
        var ordered = new List<string>();
        foreach (var value in ignoredValues)
            if (_ignored.Add(value)) ordered.Add(value);
        Ignored = ordered;
    }

    public bool IsIgnored(string value) => _ignored.Contains(value);

    /// <summary>One checklist row: a value seen in the data, how often, and
    /// whether it's currently set aside.</summary>
    public sealed record Entry(string Value, int Count, bool Ignored);

    /// <summary>Every distinct value in the data with its count — the
    /// checklist the Sources page renders. Count descending so the values
    /// that matter most sit on top, ordinal tiebreak so the order never
    /// depends on CurrentCulture.</summary>
    public IReadOnlyList<Entry> Discover(IEnumerable<string> values) =>
        values.GroupBy(v => v, StringComparer.Ordinal)
            .Select(g => new Entry(g.Key, g.Count(), IsIgnored(g.Key)))
            .OrderByDescending(e => e.Count)
            .ThenBy(e => e.Value, StringComparer.Ordinal)
            .ToList();
}
