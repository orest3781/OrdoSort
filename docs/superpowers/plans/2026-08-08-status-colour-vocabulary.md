# Status Colour Vocabulary Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Status notes across the app are colour-coded with one consistent vocabulary, so a colour means the same thing everywhere.

**Requested by the owner:** *"id like color coding for notes and messages, such as a saved password opens this"* — with two follow-up decisions: **every status note app-wide**, and **colour the note only, not the whole row**.

## The vocabulary (decided; apply it, do not invent alternatives)

| Meaning | Token | Used for |
|---|---|---|
| Good / ready / done | **`Theme.StatusGreen`** (new, see Task 1) | "a saved password opens this", a successful unlock line |
| Needs attention | `Theme.StatusAmber` | needs a password, ambiguous or suggested matches, in use |
| Error / couldn't | `Theme.Danger` | couldn't be read |
| Informational, de-emphasised | `Theme.SubtleText` | "already has the id", "no roster match", "(no change)", "edited by hand" |
| Nothing worth saying | *(no note)* | unprotected file, pending probe |

**This extends an existing contract, it does not replace one.** `CopyAndTerminologyTests.cs:183-484` locks Settings' per-field notes to amber-for-problem / subtle-for-fact, and `SettingsWindow.xaml:43-53`'s `NoteText` style is the precedent. **Amber means needs-attention and must never be used for a merely informational fact.** Keep that true.

**Tech Stack:** C# / .NET 8, WPF, xUnit. Repo `S:\OrdoSort`, branch `main`, base `991d535`.

## Two traps this plan exists to navigate

1. **Style Setter outranks inheritance.** `Styles.xaml`'s `DataGridCell` style (`:1580-1591`) sets its own `Foreground` (and flips it on `IsSelected`). A `Foreground` set on `DataGridRow` is *inherited* by the cell, so the cell's own Setter wins and the row-level colour never appears. **`MatchMergeWindow.xaml:120-143` sets exactly that, and is therefore probably dead today.** No test covers it. The technique that *does* work is a per-column `ElementStyle` targeting the column's own `TextBlock` — as `BulkRenameWindow.xaml:204-211` already does successfully for its "New name" column.

2. **Selected rows.** The Unlock file list is a `ListBox` whose `ItemTemplate` binds `Foreground` back to the ancestor `ListBoxItem` (`UnlockWindow.xaml:73-74`) so selected rows stay readable on the accent background — an `ItemTemplate` is a local value that outranks the app-level `ListBoxItem` style. A fixed status colour on the note **overrides that and can render unreadable when selected**. Resolve it by letting selection win: colour the note only while the row is *not* selected. Do not go hunting for a colour that passes against both backgrounds.

## Global Constraints

- **The contrast floor is WCAG AA 4.5:1**, enforced by `ThemePalette.ContrastRatio` (`:76-81`) and asserted throughout `HighlightContrastTests.cs`. Every new colour pairing must clear it **in both palettes**, selected and unselected.
- **`Theme.Success` is RGB 46,125,50 in *both* palettes** (`ThemePalette.cs` Light `:28-44` / Dark `:46-62`). `Theme.StatusAmber` is deliberately *different* per palette (146,90,4 / 240,173,78) precisely so text stays legible. Assume nothing: **measure `Theme.Success` against `Theme.Surface` in dark, and if it fails the floor, that is why Task 1 adds a per-palette green rather than reusing it.**
- **`HighlightContrastTests.cs:508-633` already asserts the Unlock file list's selected-row contrast for all four non-Pending suffixes.** Those tests will need updating once the note is coloured — **update them deliberately, never delete them**, and name each one changed.
- **The gate command is NOT plain `dotnet test`.** Smart App Control blocks the WPF test assembly by hash; `dotnet test` alone **silently skips the entire WPF suite and still exits 0**:
  ```bash
  dotnet build OrdoSort.sln -t:Rebuild -p:Deterministic=false -v minimal
  dotnet test OrdoSort.sln --no-build -v minimal
  ```
  **Baseline: Core 444 + Wpf 643 = 1087 green.** Core.Tests takes ~56s by design.
- **Two environment-sensitive suites — report, never chase, never weaken:** `FocusRingCoverageTests.TabItemShowsTheBronzeFocusRing`, `WebViewPdfViewerGuardBehaviourTests` (all 5 fail together with COM `Class not registered`, pass on re-run).
- **This session cannot drive the real UI** — screen capture returns black, input injection denied. Verify off-screen on the WPF suite's STA fixture.
- **The pattern is at thirteen.** A test that proves a `TextBlock` exists is not a test that proves it is the colour you think. Assert the **resolved, rendered brush**, the way `HighlightContrastTests` already does — not the presence of a trigger in XAML.
- Never `--no-verify`, never force, **never push**.

---

### Task 1: A legible green, and the Unlock file list note

**Files:** `Theme/ThemePalette.cs`, `Theme/ThemeManager.cs`, `ViewModels/UnlockViewModel.cs`, `Windows/UnlockWindow.xaml`, tests.

- [ ] **Step 1: Measure first.** Compute `Theme.Success` against `Theme.Surface` and `Theme.WindowBg` in **both** palettes and report the ratios. If dark fails 4.5:1, add **`Theme.StatusGreen`** as a per-palette pair chosen the way `StatusAmber` was — a dark-enough green for light, a light-enough green for dark. **Report the chosen RGBs and their measured ratios.** If `Theme.Success` unexpectedly passes everywhere, say so and reuse it rather than adding a token.

- [ ] **Step 2: Split the note from the filename.** `UnlockFileRow.DisplayText` (`UnlockViewModel.cs:71-78`) concatenates both into one string, so the note cannot be coloured separately. Expose the filename and the note as separate bindable values, keeping `DisplayText` if anything still needs the combined form (check before removing it). `ToolTipText` (`:84`) keeps the full path plus the probe's fuller message — leave that behaviour.

- [ ] **Step 3: Colour the note, and let selection win.** In `UnlockWindow.xaml`'s `ItemTemplate`, the filename keeps the existing ancestor-`ListBoxItem` `Foreground` binding. The note gets the vocabulary colour **when the row is not selected**, and reverts to the ancestor binding when it is. Ready→green, NeedsPassword→amber, InUse→amber, Unreadable→`Theme.Danger`; Pending and NotEncrypted have no note at all.

- [ ] **Step 4: Update the existing contrast tests.** `HighlightContrastTests.cs:508-633` covers the selected-row case for all four suffixes. Extend them to assert the **unselected** note colour too, and confirm the selected case still clears 4.5:1 now that a second element is involved. Name every assertion you changed.

- [ ] **Step 5: Prove teeth.** Remove the not-selected condition so the status colour also applies when selected, rebuild, and confirm a contrast test fails **because the ratio dropped below 4.5** — not because something didn't render. Paste it.

- [ ] **Step 6: Commit** `feat(ui): colour-code the Unlock readiness notes`.

---

### Task 2: Find out whether Match & Merge's colouring works, then make the grids consistent

**Files:** `Windows/MatchMergeWindow.xaml`, `Windows/BulkRenameWindow.xaml`, `Windows/TriageWindow.xaml.cs`, tests.

- [ ] **Step 1: Settle the precedence question empirically.** Render `MatchMergeWindow` off-screen with rows in each `Status`, read the **resolved `Foreground` of the Note cell's `TextBlock`**, and report what it actually is. Do not reason about it — measure it. This decides whether `MatchMergeWindow.xaml:120-143` is a working feature or dead code, and the answer belongs in a comment there either way.

- [ ] **Step 2: Move the grids' status colour to the technique that works.** Per-column `ElementStyle` on the column's own `TextBlock`, as `BulkRenameWindow.xaml:204-211` already does. Apply the vocabulary to the Note columns of Match & Merge and Bulk rename, and to Triage's `Why` column.

  Mapping — Match & Merge: ambiguous and suggested → amber; already, no_match, no_name → subtle; no_roster → amber (it is a thing the user must fix); merge → no note today, leave it. Bulk rename: "edited by hand" and "(no change)" → subtle; anything reporting a problem → amber. Triage `Why`: informational → subtle.

  **If Step 1 shows the RowStyle colouring was already working**, say so and prefer the smaller change — do not rewrite something correct for the sake of uniformity.

- [ ] **Step 3: Close the test gap.** Nothing currently asserts the rendered colour of any DataGrid Note or Why cell. Add tests that read the **resolved brush** for each status, in both palettes, selected and unselected — this is the gap that let Match & Merge's dead colouring go unnoticed.

- [ ] **Step 4: Prove teeth.** Break one column's `ElementStyle` and confirm the matching test fails **because the brush is wrong**. Paste it.

- [ ] **Step 5: Commit** `fix(ui): status colours in the grids reach the cell that shows them`.

---

### Task 3: The results list, and one meaning per colour

**Files:** `Windows/UnlockWindow.xaml`, tests.

- [ ] **Step 1: Give success a colour.** `UnlockResultKind.Ok` is currently uncoloured (`UnlockWindow.xaml:164-174` colours only Fail and Skip). Under the vocabulary, Ok → green. Fail stays amber — it is needs-attention, and the amber contract in `CopyAndTerminologyTests` depends on that meaning holding. Skip stays subtle.

- [ ] **Step 2: Sweep for contradictions.** Find every other place a colour already conveys status — `MainWindow.xaml:112-115`/`:145-148`/`:231`, `DoneView.xaml:9`/`:23`, `ReadyView.xaml:80`/`:113`, `ProcessingView.xaml:55`, `SettingsWindow.xaml:43-53` — and confirm none of them now contradicts the vocabulary. **Report each and its verdict.** Change only what genuinely conflicts; a green that already means "done" is already correct.

- [ ] **Step 3: Test the results list colours** by resolved brush, both palettes.

- [ ] **Step 4: Commit** `feat(ui): a successful unlock reads as success`.

---

### Task 4: Gate

- [ ] **Step 1: Release build and full suites.**
```bash
dotnet build OrdoSort.sln -c Release -t:Rebuild -p:Deterministic=false -v minimal
dotnet test OrdoSort.sln -c Release --no-build -v minimal
```

- [ ] **Step 2: Smoke.** `dotnet run --project tools/OrdoSort.Smoke -- demo-full` ends `All checks passed`, exit 0.

- [ ] **Step 3: Render every coloured surface off-screen in both palettes**, selected and unselected, and report the measured contrast ratio for each state. **This is the acceptance evidence** — a passing unit test on one state does not tell you the palette reads coherently.

- [ ] **Step 4: Judge it as a whole.** With all five surfaces rendered, does one colour mean one thing? Is anything shouting that should be quiet? **Say so if it looks wrong** — that is a finding, not a footnote.

- [ ] **Step 5: Report, do not push.**

## Model assignments

| Task | Implementer | Review |
|---|---|---|
| 1 Green + Unlock notes | sonnet | sonnet (read-only) |
| 2 Grid columns | sonnet | sonnet (read-only) |
| 3 Results + sweep | sonnet | — |
| 4 Gate | sonnet (read-only) | — |
