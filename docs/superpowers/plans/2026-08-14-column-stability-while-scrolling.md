# Column Stability While Scrolling — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** A content-sized column stops changing width while the user scrolls.

**Reported by the owner** as one of three symptoms of "the autofit is not working". The other two — content truncated while space sat unused, and dead space to the right — were fixed in `1b67404..ebe8c5e`. This one was attempted twice, reverted twice, and is deliberately left for a session that can design it rather than patch it.

## The symptom, measured

WPF sizes a `Width="Auto"` column from the rows currently **realized**, and under row virtualization that is only the visible window's worth. A longer value further down the list does not exist, as far as the column is concerned, until it scrolls into view.

Measured on MatchMerge, 60 rows, short values with one long filename at the bottom:

| | File column |
|---|---|
| before scrolling | **56px** |
| after the long row realizes | **319.5px** |

A 5.7x lurch. `HistoryWindow.xaml` records the same effect measured independently in the 2026-08-07 round — 173px to 410px, never shrinking back — and dealt with it by keeping `Original`/`Filed as` star-shaped. That is stable but not content-sized, which is the opposite of what autofit is for, and it was only ever applied to History.

**Severity, stated honestly:** this is the least severe of the three reported symptoms. It is cosmetic — the growth is bounded by the cap and one-way, so a column settles once the widest value seen so far has been seen. The other two produced wrong results; this one produces a jumpy but correct one.

## Reproduction

The harness is not in the tree — it was removed by the revert. Recover `MeasureFileColumnAcrossAScroll` and `AnAutoColumnIsSizedForRowsItHasNotScrolledToYet` from commit `7765192`:

```bash
git show 7765192 -- tests/OrdoSort.Wpf.Tests/AutoFitColumnTests.cs
```

It builds a grid with heterogeneous rows (short at the top, one long value at the bottom), measures the column, calls `ScrollIntoView` on the last row, and measures again. **Heterogeneous content is the whole point** — every other many-row fact in that file feeds every row the same value, so whichever rows happen to be realized measure the same as any other would and nothing can move. That is exactly why sixty-row facts passed while a person watching a real grid saw columns jump.

## What was tried, and the numbers

### Attempt 1 — realize every row (`7765192`, reverted in `72fc8e2`)

Set `grid.EnableRowVirtualization = false`, call `grid.UpdateLayout()` so WPF measures every row itself, write the resulting `ActualWidth` into each governed column's `MinWidth`, restore virtualization.

Correct, and far too slow. Timed on MatchMerge:

| rows | with the pass | baseline | cost |
|---|---|---|---|
| 60 | 458ms | 265ms | +193ms |
| 500 | **2553ms** | 165ms | **+2.4s** |
| 1999 | **5797ms** | 172ms | **+5.6s** |

Baseline is flat regardless of row count — that is virtualization doing its job. The pass made it linear at roughly **2.8ms per row**. Adding a folder of 500 PDFs would have frozen Match & Merge for two and a half seconds.

The 2000-row limit it carried was chosen by reasoning about the cost rather than measuring it, and was out by an order of magnitude. **Measure before choosing a limit.**

### Attempt 2 — measure the text, not the rows (discarded, never committed)

Evaluate each column's `Binding` per item onto a throwaway `DependencyObject`, measure the resulting string with `FormattedText` using `AppFontFamily`/`AppFontSize` and the grid's `PixelsPerDip`, take the widest, add a `CellPaddingAllowance` of 20 (DataGridCell's own Padding is `8,4`, so 16 plus a couple of px for border and rounding, rounded **up** because slightly wide is invisible and capped whereas slightly narrow clips).

Roughly **45x cheaper**, and fast enough to ship:

| rows | text measurement | baseline |
|---|---|---|
| 60 | 258ms | 265ms |
| 500 | **203ms** | 165ms |
| 1999 | **373ms** | 172ms |
| 5000 | **762ms** | ~170ms |

About **0.06ms per row-column measurement**. Still linear, so it needs a ceiling — but one that can be set from these numbers rather than guessed.

It fixed the symptom (`319.5 -> 319.5`, no movement) **and broke six tests**:

```
ZipMerge_AtMinWidthNoHorizontalScrollbar
History_AtMidWidthNoHorizontalScrollbar
Production_AtMinWidthWithFourGroupColumnsNoHorizontalScrollbar
MatchMerge_AtMinWidthNoHorizontalScrollbar
Triage_LongRosterValueStopsAtTheCapWithEllipsisAndTooltip
Unzip_AtMinWidthNoHorizontalScrollbar
```

It was discarded **without diagnosing those failures**, which was the wrong call under time pressure and is why this plan exists rather than a third attempt.

## The hypothesis to start from

Both attempts fail the same way, and the shape of it is the useful part:

> **`MinWidth` outranks `MaxWidth` in WPF's arbitration.** Any floor written to make a column stable can defeat the cap that guarantees no horizontal scrollbar. The two mechanisms are in direct conflict, and every fix that works by setting a floor inherits that conflict.

The specific, testable hypothesis for attempt 2's six failures:

> **`TriageWindow` and `ProductionWindow` do not use the remainder rule.** They call the `Func<double,double>` overload of `DataGridColumnCap.Track` with their own budgets (`ComputeRosterColumnCap`, `ComputeGroupColumnCap`), and those budgets are computed assuming the governed columns carry **no floors**. A text-measured floor silently exceeds what their arithmetic reserved, so the total overflows.

If that holds, the fix is contained: apply measured floors **only where the remainder rule governs** (History, MatchMerge, BulkRename, ZipMerge, Unzip, PageCounts, Turnaround) and leave the two `Func` grids on current behaviour, or teach their budgets to subtract floors.

**Verify the hypothesis before building on it.** Two of the six failures are remainder-rule grids at MinWidth (MatchMerge, ZipMerge, Unzip, History), which the hypothesis does *not* explain on its own — so either the clamp in `Recalculate` is not running early enough for them, or the floors interact with `EntitlementOf`'s live `ActualWidth` reads. Both are checkable.

## Global Constraints

- **The gate command is NOT plain `dotnet test`.** Smart App Control blocks the WPF test assembly by hash; when it does, `dotnet test` **silently reports zero tests and still exits 0**. Always:
  ```bash
  dotnet build OrdoSort.sln -p:Deterministic=false -v minimal
  dotnet test OrdoSort.sln --no-build -v minimal
  ```
  and **read the `Passed!` line and its counts**, never the exit code. If it says "No test is available" or "Application Control policy has blocked this file", rebuild with `--no-incremental`.
  **Baseline at the time of writing: Core 661 + Wpf 1730 green.**
- **If OrdoSort is running it holds its own binaries** and the build fails with MSB3027. Do not kill it — it does real filing work. Either ask, or build with `-p:OutputPath=` redirected elsewhere and run via `dotnet vstest`.
- **Measure before choosing any limit.** Attempt 1's row cap was reasoned about and wrong by 10x. Both attempts' real costs are in the tables above; extend them, do not re-derive them.
- **Tests that feed every row the same value cannot see this bug.** Any new fact must use heterogeneous content.
- **A screenshot has caught two things this session the tests could not.** Render the affected grids and look, do not rely on measured widths alone. Note the screenshot harness currently rasterises before layout settles (`tools/OrdoSort.Smoke/Screenshots.cs`), which is its own small bug.

---

### Task 1: Diagnose attempt 2's six failures

- [ ] **Step 1: Restore attempt 2.** It is not in git — rebuild it from the description above, or reconstruct from `7765192` and swap the realize-rows pass for text measurement. Confirm it reproduces both results: the symptom fixed (`319.5 -> 319.5`) and the same six tests failing.

- [ ] **Step 2: For each of the six, find the actual cause.** Do not assume the hypothesis. Report per test: which column overflowed, by how much, and whether its floor exceeded its own cap or the *sum* exceeded the viewport. These are different faults with different fixes.

- [ ] **Step 3: Say whether the hypothesis held.** If the two `Func` grids fail for the stated reason and the remainder-rule grids fail for a different one, say so — that is two findings, not one.

### Task 2: Decide the shape, then build it

- [ ] **Step 1: Choose, with the diagnosis in hand.** Candidates, in the order I would consider them:
  - Measured floors for remainder-rule grids only; `Func` grids unchanged.
  - Measured floors everywhere, with the two `Func` budgets taught to subtract floors.
  - Abandon floors entirely and set the column's `Width` from the measurement instead — but note this makes `Width.IsAbsolute` true, which `DataGridColumnCap`'s drag handling currently reads as "the user pinned this", so the pinning detection would need a different signal.
  - Accept the symptom and close this plan, recording why. **This is a legitimate outcome** and better than a third revert.

- [ ] **Step 2: Set the measurement ceiling from the numbers**, not from intuition. At ~0.06ms per row-column, a third of a second buys about 6000 measurements. State what budget you chose and what it costs on HistoryWindow, the one genuinely unbounded grid.

- [ ] **Step 3: Build it, with the failing test first.**

- [ ] **Step 4: Prove teeth.** Disable the mechanism and confirm the stability fact fails reporting the real numbers, not just that it fails.

- [ ] **Step 5: Full suites green**, both counts stated, plus a render of MatchMerge and History at default and MinWidth.

### Task 3: Report

- [ ] **Step 1: Report, and do not push without the owner's say-so.** Include the timings for whatever was built, so the next person after you inherits numbers rather than adjectives.

## Model assignments

| Task | Implementer | Review |
|---|---|---|
| 1 Diagnose | sonnet (six concrete failures, evidence-gathering) | — |
| 2 Build | opus (a design decision with a real WPF conflict at its centre) | sonnet, adversarial |
| 3 Report | sonnet | — |
