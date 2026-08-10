# Roster Header Mapping Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Match & Merge must never guess a roster column wrong and report success. If the First/Last/Control mapping cannot be determined confidently, the user is told and asked — never silently given column 0.

**Why this is Critical:** this mapping decides **which person a document is filed against**. A wrong guess files someone's records under someone else's name, and today the UI says *"Roster loaded: N people."* either way.

**Tech Stack:** C# / .NET 8, WPF, xUnit. Repo `S:\ordosort-session` (worktree), branch `session/header-pickers`, base `4a006e5`.

## The defects, all confirmed by reading the code

`MatchMergeViewModel.cs:183-192`:
```csharp
string Pick(string key, params string[] needles)
{
    var saved = _cfg.MergeHeaders.TryGetValue(key, out var s) && headers.Contains(s) ? s : null;
    return saved
        ?? headers.FirstOrDefault(h => needles.Any(n => h.ToLowerInvariant().Contains(n)))
        ?? headers.FirstOrDefault() ?? "";      // ← silently column 0
}
_firstHeader   = Pick("first", "first");
_lastHeader    = Pick("last", "last");
_controlHeader = Pick("control", "control", "id");
```

| # | Defect | Consequence |
|---|---|---|
| 1 | No needle match falls back to `headers.FirstOrDefault()` | A roster of `Name, DOB, Ref` maps **First, Last and Control all to column 0** |
| 2 | Nothing requires the three picks to be distinct | Three roles, one column, no complaint |
| 3 | `"id"` is a naive substring | `Paid Date`, `Resident`, `Video` all match Control |
| 4 | `MatchMerge.cs:65-67` resolves by name via `IndexOf` | Duplicate header names collapse to the first occurrence |
| 5 | `MatchMerge.cs:79` stores `row[headers[i]]` in a name-keyed dictionary | A duplicate column **overwrites its twin — the data is lost** |
| 6 | A blank header can be picked | Maps a role to an unnamed column |
| 7 | `Status` reports `"Roster loaded: N people."` regardless | The user has no signal any of the above happened |

## Global Constraints

- **`main` has moved.** Another session merged substantial work; the Core suite has grown well past the 444 this session last recorded. **Establish the real baseline by running the suites before changing anything, and report it** — do not trust any number quoted from earlier.
- **The gate command is NOT plain `dotnet test`.** Smart App Control blocks the WPF test assembly by hash; `dotnet test` alone **silently skips the entire WPF suite and still exits 0**:
  ```bash
  dotnet build OrdoSort.sln -t:Rebuild -p:Deterministic=false -v minimal
  dotnet test OrdoSort.sln --no-build -v minimal
  ```
  Core.Tests takes ~56s by design — not a hang.
- **Two environment-sensitive suites — report, never chase, never weaken:** `FocusRingCoverageTests.TabItemShowsTheBronzeFocusRing`, `WebViewPdfViewerGuardBehaviourTests` (all 5 fail together with COM `Class not registered`, pass on re-run).
- **Work only in the `S:\ordosort-session` worktree.** `S:\OrdoSort` is a different checkout with another session active in it — do not touch it, do not `cd` into it.
- **This session cannot drive the real UI** — screen capture returns black, input injection denied. Verify off-screen on the WPF suite's STA fixture.
- **The pattern is at thirteen.** A test that proves a roster loads is not a test that proves it loaded *correctly*. Assert which column each role resolved to, not that no exception was thrown.
- Never `--no-verify`, never force, **never push**.

---

### Task 1: Stop guessing wrong, and say so

**Files:** `src/OrdoSort.Core/MatchMerge.cs`, `src/OrdoSort.Wpf/ViewModels/MatchMergeViewModel.cs`, tests.

- [ ] **Step 1: Write the failing tests first**, one per defect in the table above. The headline case is a roster whose headers match none of the needles — assert today it maps all three roles to column 0 and still reports success. **Run them; they must fail.** Paste the output.

- [ ] **Step 2: Remove the silent fallback.** No confident match means **no pick** — not column 0. Decide deliberately how the UI represents "unset" and record it; the mapping row already exists (`HasRoster` shows it), so the natural answer is to show it unmapped and let the user choose.

- [ ] **Step 3: Require the three to be distinct**, and say which collide when they don't.

- [ ] **Step 4: Tighten the needles.** `"id"` must not match `Paid Date` or `Resident`. Match on word boundaries or whole tokens rather than raw substrings, and keep `first`/`last` sensible for real-world headers (`First Name`, `Surname`, `Last`, `Given name`). **List the header spellings you accept and reject**, and test both.

- [ ] **Step 5: Handle duplicate and blank headers at the source.** `MatchMerge.LoadRoster` resolves by name (`:65-67`) and stores rows name-keyed (`:79`), so duplicates silently lose a column's data. Either resolve by index throughout or reject a duplicate/blank header set with a clear message. **Say which you chose and why**, and make sure the chosen route cannot lose a column silently.

- [ ] **Step 6: Never claim success on an unresolved mapping.** `"Roster loaded: N people."` must not appear while any role is unmapped, ambiguous, or colliding. State what it says instead in each case.

- [ ] **Step 7: Saved mappings must survive this.** `_cfg.MergeHeaders` persists the user's choice and `_saveHeaders` writes it (`:218-221`). A saved mapping that no longer matches the current file must be discarded cleanly rather than half-applied — and a **valid** saved mapping must still be honoured without re-prompting.

- [ ] **Step 8: Full suites green**, with the baseline from Global Constraints. Name every pre-existing assertion you changed and why.

- [ ] **Step 9: Prove teeth.** Restore the `?? headers.FirstOrDefault()` fallback and confirm the Step 1 headline test fails **because all three roles resolved to column 0** — not because something threw. Paste it.

- [ ] **Step 10: Commit** `fix(merge): never guess a roster column and call it loaded`.

---

### Task 2: Gate

- [ ] **Step 1: Release build and full suites**, against the baseline established in Task 1.
```bash
dotnet build OrdoSort.sln -c Release -t:Rebuild -p:Deterministic=false -v minimal
dotnet test OrdoSort.sln -c Release --no-build -v minimal
```

- [ ] **Step 2: Smoke.** `dotnet run --project tools/OrdoSort.Smoke -- demo-full` ends `All checks passed`, exit 0. Note the workbench builds a roster of its own — confirm it still loads and its merge cohorts still come out at the documented counts.

- [ ] **Step 3: Walk real rosters end to end**, off-screen, driving the real `MatchMergeWindow`: a clean roster; one with no matching headers; one with duplicate headers; one with a blank header; one with `Paid Date` present to catch the `"id"` needle. For each, record **which column each role resolved to** and **exactly what the status line says**. This is the acceptance evidence.

- [ ] **Step 4: Report, do not push.**

## Model assignments

| Task | Implementer | Review |
|---|---|---|
| 1 Mapping | sonnet | sonnet (read-only) |
| 2 Gate | sonnet | — |
