# Autofit Grid Columns Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make grid columns size to their contents across the app, starting with Match & Merge, without horizontal scrolling, dead space, or columns that jump while scrolling.

**Requested by the owner:** *"I want all columns to autofit the contents in match and merge and anywhere else."*

**Decisions taken by the owner — implement these, do not re-open:**
1. **Cap, then ellipsis.** Columns size to content, but no column may exceed a sensible share of the window. Past that, truncate with `…` and keep the full value in a tooltip. **No horizontal scrollbar.**
2. **Fill the window.** Leftover width goes to the main text column so there is no dead grey space to the right.

**Architecture:** Four grids, and **they are not all the same problem**. Three are bounded (a rename batch, a candidate list, a merge preview) where WPF's `Auto` behaves well. **History is virtualized over an unbounded table**, where `Auto` measures only *realized* rows — so columns visibly jump as you scroll and a longer path renders. That is the failure mode decision 1 exists to prevent, so History needs a different tactic and Task 1 Step 5 settles it by measurement.

**Tech Stack:** C# / .NET 8, WPF, xUnit. Repo `S:\OrdoSort`, branch `main`, base `26684b3`.

## Current state

| Grid | File | Columns today |
|---|---|---|
| Match & Merge | `Windows/MatchMergeWindow.xaml:147-149` | File `*`, Becomes `*`, Note `240` |
| Bulk rename | `Windows/BulkRenameWindow.xaml:167-186` | Current `*` Min120, New name `*` Min120, Note `220` |
| History | `Windows/HistoryWindow.xaml:42-100` | When `140`, Original `*` Min120, Filed as `*` Min120, Name `160`, Destination `120`, Undone `70` |
| Triage | `Windows/TriageWindow.xaml:41-44` | **built in code-behind** — no `<DataGrid.Columns>` block |

## Global Constraints

- **The gate command is NOT plain `dotnet test`.** Smart App Control blocks the WPF test assembly by hash; when it does, `dotnet test` **silently skips the entire WPF suite and still exits 0**. Always:
  ```bash
  dotnet build OrdoSort.sln -t:Rebuild -p:Deterministic=false -v minimal
  dotnet test OrdoSort.sln --no-build -v minimal
  ```
  **Baseline: Core 426 + Wpf 591 = 1017 green.** Core.Tests takes ~53s by design — not a hang.
- **`FocusRingCoverageTests.TabItemShowsTheBronzeFocusRing` is environment-sensitive** — either result is fine, never chase or weaken it.
- **This session cannot drive the real UI** — screen capture returns black, input injection denied. Verify headlessly: the WPF suite builds real windows off-screen on a shared STA fixture (`[Collection(HighlightContrastTests.Name)]` + `HighlightContrastFixture`). `DataGridStarColumnTests` shows the pattern for measuring real column widths after `Show()` + `UpdateLayout()`.
- **Two existing test suites encode the behaviour you are changing. Update them deliberately, never delete them:**
  - `DataGridStarColumnTests` asserts star columns get a fair share (>100px) on first layout — it exists because they once collapsed to `MinWidth`.
  - `HistoryWindowXamlTests` asserts `TextTrimming` and tooltips on History's Name/Destination columns.
  If either contradicts the new behaviour, change the assertion and **say which and why** in your report.
- **The pattern is at twelve.** A test that proves a grid renders is not a test that proves a column sized correctly. Assert measured `ActualWidth` against content, and ask what fails if the sizing is reverted.
- Never `--no-verify`, never force, **never push**.

---

### Task 1: Content-sized columns with a cap and a filler

**Files:** the four XAML files above, `Windows/TriageWindow.xaml.cs`, and tests.

- [ ] **Step 1: Decide the shared shape once, then apply it consistently.** The owner's two decisions imply: content-sized columns use `Width="Auto"` with a `MaxWidth`; exactly one column per grid is the **filler** (`Width="*"` with a `MinWidth`) so slack is absorbed; and every text cell gets `TextTrimming="CharacterEllipsis"` plus a `ToolTip` bound to the full value, so the cap never hides data.

  Pick the filler per grid deliberately — it should be the column whose content is most variable and most worth the extra room. Record your choice and reasoning for each.

  **Express the cap as a share of the window, not a magic pixel count**, so it behaves at any window size. If you can only do that in code, say so and keep the XAML honest about it.

- [ ] **Step 2: Match & Merge** (`MatchMergeWindow.xaml:147-149`) — this is the one the owner named, so get it right first. `Note` is fixed at 240 today and `File`/`Becomes` are star, so short filenames leave both stretched and long ones clip. Apply the shape.

- [ ] **Step 3: Bulk rename** (`BulkRenameWindow.xaml:167-186`). Note the existing comment block above those columns reasoning about the 120px floor and window widths — **read it before editing and update it if your change makes it false.** A stale comment describing the old sizing is exactly the trap this repo keeps hitting.

- [ ] **Step 4: Triage** (`TriageWindow.xaml.cs`) — its columns are built in code, so it needs the same treatment applied there rather than in XAML. Find where they are constructed and match the shape.

- [ ] **Step 5: History — measure before choosing.** `HistoryWindow.xaml` is virtualized over an unbounded audit table, and `Original`/`Filed as` hold full paths.

  Build a history with a few thousand rows including some very long paths, show the window off-screen, and **measure whether `Auto` columns change width as rows realize** (measure, scroll/realize more rows, measure again). Report the numbers.

  - If they jump, do **not** ship `Auto` here — keep History's columns stable (its current star + `MinWidth` + ellipsis + tooltip shape already satisfies the owner's decisions) and change only what genuinely improves: the fixed-pixel columns (`When` 140, `Name` 160, `Destination` 120, `Undone` 70) can size to content because their values are bounded in length.
  - If they don't jump, apply the same shape as the others.

  **Either outcome is acceptable. Say which you measured and why you chose what you chose** — a comment in the XAML recording it, so the next reader doesn't re-litigate.

- [ ] **Step 6: Write the tests.** For each grid, assert against **measured** widths after a real off-screen `Show()` + `UpdateLayout()`:
  - a short-content column is narrower than it used to be (it fits, rather than filling a fixed width);
  - a long-content column stops at the cap rather than growing without bound;
  - the grid's columns together fill the available width — no dead space;
  - the total never exceeds the viewport, i.e. **no horizontal scrollbar**;
  - trimming and tooltips are present wherever a cap can bite.

- [ ] **Step 7: Full suites green**, with `DataGridStarColumnTests` and `HistoryWindowXamlTests` updated rather than deleted. State the count and name every pre-existing assertion you changed.

- [ ] **Step 8: Prove teeth.** Revert the sizing on one grid, rebuild, and confirm the matching test fails **because the column is the wrong width** — not because something didn't render. Paste it.

- [ ] **Step 9: Commit** `feat(ui): grid columns size to their contents, capped, with no dead space`.

---

### Task 2: Gate and record

- [ ] **Step 1: Release build and full suites.**
```bash
dotnet build OrdoSort.sln -c Release -t:Rebuild -p:Deterministic=false -v minimal
dotnet test OrdoSort.sln -c Release --no-build -v minimal
```

- [ ] **Step 2: Smoke.** `dotnet run --project tools/OrdoSort.Smoke -- demo-full` ends `All checks passed`, exit 0.

- [ ] **Step 3: Render every affected grid at two window sizes** — near-minimum and wide — off-screen in both palettes, and confirm for each: content fits, nothing clips that shouldn't, no horizontal scrollbar, no dead space. The `screenshots` smoke mode renders the gallery (**it always exits 1 by design — judge it by the PNGs, not the exit code**), or drive the windows directly. Report what you observed per grid, per size. **This is the acceptance evidence** — measured widths in a unit test do not tell you whether it looks right.

- [ ] **Step 4: Report, do not push.**

## Model assignments

| Task | Implementer | Review |
|---|---|---|
| 1 Column sizing | sonnet (four grids, one measurement decision) | sonnet |
| 2 Gate | sonnet | — |
