using OrdoSort.Core;

namespace OrdoSort.Core.Tests;

/// <summary>The history table had no indexes at all, while RankedNames() —
/// the autocomplete source — ran a full-table GROUP BY after every commit,
/// skip and undo, against a table this app never prunes (2026-08-04 audit,
/// finding 5.1).
///
/// These assert the QUERY PLAN, not a wall-clock time. A timing assertion
/// would flake on a busy machine and get muted; the plan is deterministic and
/// is the actual claim being made. Correctness of the returned names is
/// covered elsewhere — and deliberately so: a correctness test passes just as
/// happily with no index at all, which is exactly the trap this suite exists
/// to avoid.</summary>
public class HistoryIndexTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(),
        "ordo_hist_ix_" + Guid.NewGuid());

    public HistoryIndexTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        // SQLite releases its native file handle slightly after Dispose on
        // Windows; retry the temp-dir cleanup rather than fail the test.
        for (var attempt = 0; ; attempt++)
        {
            try { Directory.Delete(_dir, recursive: true); return; }
            catch (IOException) when (attempt < 10)
            {
                GC.Collect();
                GC.WaitForPendingFinalizers();
                Thread.Sleep(50);
            }
        }
    }

    private static readonly string[] Names =
        Enumerable.Range(0, 50).Select(i => $"NAME_{i:D2}").ToArray();

    private History Seeded(int rows = 500)
    {
        var h = new History(Path.Combine(_dir, "history.sqlite"));
        for (var i = 0; i < rows; i++)
        {
            // ~50 distinct names, a few reverted, a few with a blank
            // name_entered — matches the real LogCommit signature.
            var name = i % 25 == 0 ? "" : Names[i % Names.Length];
            var id = h.LogCommit(
                $"C:/in/{i}.pdf", $"{i}.pdf", $"{name}_{i}.pdf",
                name, "insert", "", "A", "C:/a", i % 7 == 0, "");
            if (i % 11 == 0) h.MarkReverted(id);
        }
        return h;
    }

    [Fact]
    public void TheAutocompleteQueryDoesNotScanTheWholeTable()
    {
        using var h = Seeded();
        var plan = h.ExplainRankedNames();
        // A plain Contains("SCAN history") is not enough to prove the table
        // isn't scanned: SQLite's own good-path line for this query reads
        // "SCAN history USING INDEX ix_history_ranked_names" — an index-only
        // covering scan — which contains "SCAN history" as a literal
        // substring. What must be absent is the *bare* unindexed scan, whose
        // line is exactly "SCAN history" with nothing after it. Measured
        // directly: dropping the index reproduces that exact bare line.
        Assert.DoesNotContain(
            plan.Split('\n'),
            line => line.Trim().Equals("SCAN history", StringComparison.OrdinalIgnoreCase));
        Assert.Contains("ix_history_ranked_names", plan, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>The index must arrive on databases that already exist — every
    /// station upgrading has one. Simulates that by dropping the index from an
    /// open database and reopening.</summary>
    [Fact]
    public void AnExistingDatabaseGainsTheIndexOnOpen()
    {
        var path = Path.Combine(_dir, "existing.sqlite");
        using (var h = new History(path)) h.ExecForTests("DROP INDEX IF EXISTS ix_history_ranked_names");
        using var reopened = new History(path);
        Assert.Contains("ix_history_ranked_names", reopened.ExplainRankedNames(),
            StringComparison.OrdinalIgnoreCase);
    }
}
