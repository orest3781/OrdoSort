# History Index Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Close finding 5.1 of `docs/superpowers/audits/2026-08-04-full-audit.md` — the `history` table has no indexes at all, while `RankedNames()` runs a full-table `GROUP BY` after **every** commit, skip and undo, against a table that is never pruned.

**Architecture:** One measurement-led change to `History.cs`. The deliverable is not "an index exists" but "the query plan no longer scans the table, and the write path did not get slower to pay for it." Both halves are measured, and the read half is pinned by a test that fails if the index is dropped.

**Tech Stack:** C# / .NET 8, `Microsoft.Data.Sqlite` 8.0.11. Repo `S:\OrdoSort`, branch `main`, base `e407b5d`.

## Global Constraints

- **This database is the app's audit log and it can live on an SMB share with several workstations open at once.** `History.cs:5-15` documents why WAL is deliberately not used (it relies on shared memory that does not work over a network filesystem and is the documented way to corrupt a shared SQLite file). **Do not touch `journal_mode`, `synchronous`, or `busy_timeout`.** They are load-bearing and were chosen deliberately.
- **The migration must be safe on an existing populated database**, including one already open by another station. `CREATE INDEX IF NOT EXISTS` is idempotent; the existing 30s `busy_timeout` covers waiting for another station's lock.
- **A read win paid for with a write loss is not a win.** Every index slows `INSERT`, and `LogCommit` runs on the hot filing path with `synchronous=FULL`. Measure both directions.
- **The gate command is NOT plain `dotnet test`.** Smart App Control blocks the WPF test assembly by hash; when it does, `dotnet test` **silently skips the entire WPF suite and still exits 0**. Always run:
  ```bash
  dotnet build OrdoSort.sln -t:Rebuild -p:Deterministic=false -v minimal
  dotnet test OrdoSort.sln --no-build -v minimal
  ```
  **Baseline: Core 377 + Wpf 537 = 914 green.** A WPF line that is missing, reports a skip, or reports a much smaller number means the suite did not run.
- A stray `OrdoSort.exe` breaks rebuilds — `tasklist | findstr OrdoSort` before building.
- **Do not assert on wall-clock timings in a test.** Timing assertions flake on a busy machine and will poison this suite. Assert on the **query plan**, which is deterministic; report timings as evidence in the report, not as a gate.
- **The lesson that has now held six times in this codebase:** every fix round came from *an untested branch carrying the entire safety argument for its change.* Here the safety argument is "the query uses the index" — so the question is what fails if the index is dropped. A test that merely proves `RankedNames()` returns correct names would pass with no index at all.
- Never `--no-verify`, never force, **never push**.

**Explicitly out of scope, recorded so it is not silently absorbed:** the deeper question is arguably whether `RefreshCompleterAsync` should re-run a full aggregate after *every single commit* rather than updating incrementally. That is a larger change to `ShellViewModel`/`Completer` and is not this task. Indexing helps regardless of how often the query runs. Note it in the report; do not implement it.

---

### Task 1: Index what is actually scanned

**Files:**
- Modify: `src/OrdoSort.Core/History.cs` (the `Schema` constant or `Migrate()`)
- Create: `tests/OrdoSort.Core.Tests/HistoryIndexTests.cs`

**Interfaces:**
- Produces: an index created idempotently on every `History` open, so existing databases gain it without a separate upgrade step.
- May add an internal helper to expose `EXPLAIN QUERY PLAN` output for a given SQL string, so the test can assert on the plan. Keep it `internal` — `InternalsVisibleTo OrdoSort.Core.Tests` is already present (`OrdoSort.Core.csproj:17`).

- [ ] **Step 1: Establish the before-state by measurement.** Write a throwaway benchmark (not a committed test) that builds a `History` in a temp folder, inserts **100,000** rows via `LogCommit` with a realistic spread of `name_entered` values (say 2,000 distinct names, some repeated far more than others, and a realistic fraction with `reverted = 1` and some with `name_entered = ''`), then records:

  - the output of `EXPLAIN QUERY PLAN` for `RankedNames()`'s exact SQL;
  - the wall-clock time of `RankedNames()`, best of 5;
  - the wall-clock time of `Count()` and of `Rows(200)`, best of 5;
  - the wall-clock time of 1,000 `LogCommit` calls (this is the write baseline you must not regress).

  Paste all of it into your report. **This is the evidence the whole task rests on** — without a before-state, the after-state proves nothing.

- [ ] **Step 2: Confirm the diagnosis before fixing it.** The audit claims `RankedNames()` full-scans. Confirm it: the plan should contain `SCAN history` (or equivalent) with no index in use. **If it does not — if SQLite is already handling this acceptably — say so and stop.** Report the finding rather than adding an index that buys nothing. An index that does not change the plan is pure cost: slower inserts, a larger file, and a migration for nothing.

- [ ] **Step 3: Choose the index by measurement, not by intuition.** The query is:

```sql
SELECT name_entered FROM history
 WHERE name_entered != '' AND reverted = 0
 GROUP BY name_entered
 ORDER BY MAX(ts_utc) DESC, COUNT(*) DESC, MAX(id) DESC
```

The leading candidate is a **partial covering index** whose `WHERE` matches the query's exactly, so SQLite can both use it and satisfy the grouping from index order without touching the table:

```sql
CREATE INDEX IF NOT EXISTS ix_history_ranked_names
    ON history(name_entered, ts_utc, id)
    WHERE reverted = 0 AND name_entered != ''
```

Try it, and re-run `EXPLAIN QUERY PLAN`. **SQLite only uses a partial index when the query's `WHERE` provably implies the index's `WHERE`** — confirm from the plan that it actually did, rather than assuming. If the plan still scans, try alternatives (a plain index on `(name_entered, ts_utc, id)`; reordering columns) and report which you tried and what each plan said. The winner is whichever the planner demonstrably uses.

Note that the final `ORDER BY MAX(ts_utc) DESC` sorts the *grouped* output — over distinct names, not all rows — so a residual sort in the plan is expected and fine. What must disappear is the full scan of `history`.

- [ ] **Step 4: Measure the write cost.** Re-run Step 1's 1,000-`LogCommit` benchmark with the index in place. Report both numbers. A modest slowdown is the expected, acceptable price. **If inserts got materially slower — enough to be felt on the filing path — say so plainly and reconsider**, because filing is the app's core interaction and the completer query is a background refresh.

- [ ] **Step 5: Measure the one-time migration cost.** With the 100,000-row database from Step 1 *without* the index, time how long `new History(path)` takes when it creates the index on first open. This matters: `ShellViewModel`'s constructor already opens the history synchronously before the first window paints (an open audit finding, 5.3), so this cost lands on the user's first launch after upgrading. Report the number. If it is large enough to look like a hang, say so — that changes whether this should ship as-is.

- [ ] **Step 6: Write the test that fails if the index is dropped.**

```csharp
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
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    private History Seeded(int rows = 500)
    {
        var h = new History(Path.Combine(_dir, "history.sqlite"));
        for (var i = 0; i < rows; i++)
            /* LogCommit(...) with ~50 distinct names, a few reverted,
               a few with an empty name_entered — match the real signature */;
        return h;
    }

    [Fact]
    public void TheAutocompleteQueryDoesNotScanTheWholeTable()
    {
        using var h = Seeded();
        var plan = h.ExplainRankedNames();     // internal helper, see Step 3
        Assert.DoesNotContain("SCAN history", plan, StringComparison.OrdinalIgnoreCase);
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
```

Adapt `LogCommit`'s call to its real signature (`History.cs:98`) and name the internal helpers to match what you actually add — the shape above is the contract, not literal code. Keep both helpers `internal`.

- [ ] **Step 7: Run — both MUST FAIL** before the index exists (the first on `SCAN history`, the second on the missing index name). Paste the output.

- [ ] **Step 8: Implement.** Add the winning index from Step 3 to `Migrate()` (after the existing `reverted_ts` column migration, so an old database gets its column before anything indexes it), with a comment recording *why* it exists and what the measurement showed — the numbers from Steps 1 and 3, briefly. A future reader must be able to tell whether it still earns its keep.

- [ ] **Step 9: Tests pass; full suites green.**

```bash
dotnet build OrdoSort.sln -t:Rebuild -p:Deterministic=false -v minimal
dotnet test OrdoSort.sln --no-build -v minimal
```

Expected: Core **379** (377 + 2), Wpf 537.

- [ ] **Step 10: Prove teeth.** Drop the index from the schema/migration, rebuild, and confirm both new tests fail. Restore. Paste both outputs. This is the step the whole plan is built around.

- [ ] **Step 11: Commit** `perf(history): index the autocomplete query`.

Record in the body: the before/after query plans, the read timing, the write timing, and the one-time migration cost on 100k rows.

---

### Task 2: Gate and record

- [ ] **Step 1: Release build and full suites.**

```bash
dotnet build OrdoSort.sln -c Release -t:Rebuild -p:Deterministic=false -v minimal
dotnet test OrdoSort.sln -c Release --no-build -v minimal
```

Floor: Core 379, Wpf 537.

- [ ] **Step 2: Smoke.** `dotnet run --project tools/OrdoSort.Smoke -- demo-full` ends `All checks passed`, exit 0. This exercises real filing against a real database, so it is the best evidence the write path and the migration both survive.

- [ ] **Step 3: Launch sanity against a pre-existing database.** This is the upgrade path a real user takes, and no unit test covers it end to end. Copy `demo-full`'s history database aside first so you are opening a populated one that predates the index, then launch Debug with `--config demo-full\config.json`, confirm the app starts, file or view history to prove the DB is usable, `Stop-Process`, and confirm none remains. Report what you observed and how long startup took.

- [ ] **Step 4: Update the audit document.** In `docs/superpowers/audits/2026-08-04-full-audit.md`, mark finding **5.1** fixed with the commit SHA in the style the already-fixed findings use, recording the measured before/after. Correct the "What to fix, in order" list. Commit `docs: mark the history index done`.

- [ ] **Step 5: Report, do not push.**

## Model assignments

| Task | Implementer | Review |
|---|---|---|
| 1 Measure and index | sonnet (measurement-led, SQL planner judgement) | sonnet |
| 2 Gate | sonnet | — |
