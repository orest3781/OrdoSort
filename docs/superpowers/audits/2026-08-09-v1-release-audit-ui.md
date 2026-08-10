# v1.0 UI audit — WPF (correctness, accessibility, consistency)

Repo: `S:\ordosort-session`, branch `session/header-pickers`, HEAD `6c11ead`.
Scope: newest surfaces weighted most — TurnaroundWindow/ProductionWindow (reports),
five Tools-menu utilities (FilenameList, PageCounts, ListReformat, ZipMerge, Zip,
Unzip), the MainWindow notification rail, Settings' Alerts & polling tab,
BulkRenameWindow's operation cards, and the Theme/Styles.xaml consolidation.
Method: static read of XAML/code-behind/tests plus hand-computed WCAG contrast
math (this session cannot render or screenshot the app — no pixels were checked).

## Findings

### Important

1. **Zip/Unzip "Error" renders as amber, not red — colour vocabulary broken for
   the newest tool windows.** `src/OrdoSort.Wpf/Windows/ZipMergeWindow.xaml:108-115`
   and `src/OrdoSort.Wpf/Windows/UnzipWindow.xaml:92-95` map a genuine operation
   failure (`ZipRowStatus.Error` / `UnzipRowStatus.Error` — the extract/merge threw
   or the archive is corrupt) to `Theme.StatusAmber`, the same colour used for the
   merely-informational `NoPdfs` state ("this zip had no PDFs, nothing failed").
   `UnlockWindow.xaml:109-116`, written the same day, explicitly reserves
   `Theme.StatusRed` for its own hard failure ("Unreadable") with a comment on why
   amber is wrong there. A user scanning a batch of failed extracts sees the same
   "needs attention" amber as a row that succeeded trivially — violates "one
   meaning per colour" (audit item 5). Not a contrast failure; a semantic one.

2. **New Tools windows' error-message columns aren't capped — real horizontal-
   scrollbar risk.** `PageCountsWindow.xaml:113-139` (Note), `ZipMergeWindow.xaml:101-131`
   (Result), `UnzipWindow.xaml:85-111` (Result) are all `Width="Auto"` with
   `TextTrimming="CharacterEllipsis"` but **no `DataGridColumnCap.Track` call** in
   their code-behind (confirmed absent — only `BulkRenameWindow.xaml.cs:24`,
   `MatchMergeWindow.xaml.cs:23`, `HistoryWindow.xaml.cs:42`, `TurnaroundWindow.xaml.cs:37`,
   `ProductionWindow.xaml.cs:206`, `TriageWindow.xaml.cs:278` call it). These
   columns surface `$"couldn't extract: {ex.Message}"` (`src/OrdoSort.Core/Zipper.cs:248,292`)
   — real .NET exception text, which can include full paths and can run long
   (locked-file, permission, corrupt-archive messages). Without a cap, an Auto
   column has no width to ellipsize against and will grow past the viewport,
   producing exactly the automatic-layout horizontal scrollbar item 3 says must
   never happen. BulkRename/MatchMerge already solved this identical shape (their
   own Note columns) by capping — the new windows didn't carry that fix over.

3. **ProductionWindow's selection trigger binds the wrong ancestor — works today
   only by default-setting coincidence.** `ProductionWindow.xaml.cs:151-160` builds
   the "let selection win" DataTrigger against
   `RelativeSource(FindAncestor, typeof(DataGridRow), 1)`, but every other such
   trigger in the app — `TurnaroundWindow.xaml:130-137`, `MatchMergeWindow.xaml`,
   `BulkRenameWindow.xaml:288-295`, and `Theme/Styles.xaml:1760-1769`
   (`GridCellTextSelectionAware`) — binds `AncestorType=DataGridCell`. The comment
   immediately above it (line 145-150) even says "the same trailing IsSelected
   trigger every XAML-declared column carries." It renders correctly only because
   `SelectionUnit` is never set anywhere in this app (verified by grep — default
   is `FullRow`, so `DataGridRow.IsSelected` and `DataGridCell.IsSelected` stay in
   lockstep). A comment/code mismatch that happens not to be live today, but is
   exactly the shape of the four prior foreground-precedence bugs this repo has
   already shipped.

### Minor

4. **StatusAmber never got the Surface-pairing test its siblings did.**
   `tests/OrdoSort.Wpf.Tests/ThemeTests.cs:23` asserts `StatusAmber` only against
   `WindowBg`; `StatusGreen` (line 29-30) and `StatusRed` (line 36-37) are each
   asserted against **both** `WindowBg` and `Surface` — the comments there explain
   this was added after both colours were found failing on `Surface` specifically
   (status-colour-vocabulary plan, 2026-08-08). `StatusAmber` is exactly as
   exposed: it papers the Note/Result columns of MatchMerge, BulkRename,
   PageCounts, ZipMerge, Unzip and Unlock, all of which sit on `Theme.Surface`.
   Hand-computed the missing pairing directly (WCAG relative-luminance formula,
   all seven schemes' real RGB values from `ThemePalette.cs`): 5.70:1 (paper) to
   8.84:1 (microfilm) — comfortably clears 4.5:1 everywhere, so **not a live bug**,
   but it is the one status colour that skipped the lesson this repo's own test
   file says it learned twice already.

## Verified sound

- **Report grids follow the shared pattern correctly.** Every `DataGridTextColumn`
  in `TurnaroundWindow.xaml` and the code-built columns in `ProductionWindow.xaml.cs`
  derive from `GridCellText`/`GridCellTextSelectionAware`
  (`Theme/Styles.xaml:1722,1760`) and carry the trailing "let selection win"
  DataTrigger, so selected-row contrast should hold (mechanism finding #3 aside).
- **The one previously-known accepted exception is already closed.** The task
  brief's "MainWindow toast glyph at 4.11:1 dark" gap was fixed in commit
  `cd669a3` (already an ancestor of HEAD, same-day): the glyph now binds
  `Theme.StatusRedRaised` (`MainWindow.xaml:179`) and clears ≥4.5:1 in all seven
  schemes per `ThemePalette.cs`'s materialized values — confirmed no exceptions
  of this kind remain anywhere in the newest surfaces.
- **Notification rail avoids the NameScope TargetName trap.** Its nested action/
  dismiss `Button`s use local `Style`+`DataTrigger` on their own `TextBlock`
  (`MainWindow.xaml:100-129, 139-162`) rather than a `DataTemplate.Triggers`
  `TargetName` Setter reaching across NameScopes (which WPF would reject at
  compile time, MC4111) — both carry `AutomationProperties.Name`.
- **Dashboard tile cascade avoids the Foreground-precedence trap.**
  `ReadyView.xaml:220-251` binds `Foreground` as a local value on the tile
  `Button` and every descendant `TextBlock` individually (not relying on
  inheritance or the ContentPresenter auto-wrap this repo has been bitten by
  before) — correct per the codebase's own documented lesson.
- **DataGridColumnCap mechanism itself** (`Views/DataGridColumnCap.cs`) is
  well-reasoned and self-honest about what it does and doesn't currently prevent
  (see its own "HONESTY CHECK" comment) — no contradiction found.

## Could not check

- No live rendering: screen capture returns black and input injection is denied
  in this session, so every claim above is from reading XAML/C#/tests plus
  independently recomputed WCAG math, not observed pixels.
- `DataGridSelectionContrastTests.cs` / `DataGridNoteColourTests.cs` cover
  MatchMerge, BulkRename, History, Triage, Turnaround and Production, but **not**
  the five new Tools windows (Zip, Unzip, ZipMerge, PageCounts, FilenameList) —
  their selected/hovered-cell contrast has no automated regression coverage; I
  could only spot-check the XAML by hand.
- `FocusRingCoverageTests` explicitly excludes `MenuItem` and any `DataGrid`
  (per the task brief) — could not verify cell/row focus-visual behaviour for
  the new report/tool grids beyond that known, app-wide gap.
- Did not deep-audit `ListReformatWindow.xaml` (no DataGrid, low risk) or
  the Settings "Alerts & polling" tab's individual hover states beyond the shared
  `ChipButton`/`ListBoxItem` styles already reviewed for other tabs.
