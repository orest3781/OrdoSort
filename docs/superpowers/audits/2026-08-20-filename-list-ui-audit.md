# Filename list — UI/UX audit (running list)

Surface: **Tools → Filename list…**
Files: `src/OrdoSort.Wpf/Windows/FilenameListWindow.xaml`, `…/FilenameListWindow.xaml.cs`,
`src/OrdoSort.Wpf/ViewModels/FilenameListViewModel.cs`, `src/OrdoSort.Core/FilenameList.cs`.
Started 2026-08-20 on `main` @ `5ee83be`. **No code changed.**

Method: static read of the XAML, code-behind, view model and Core, plus comparison
against the sibling tool windows (PageCounts, ListReformat, ZipTools, History) and the
spacing/colour canon in `Theme/Styles.xaml`. Nothing here was observed on screen —
items tagged **[live]** are the ones that most need confirming in a real run.

**This is a living list.** Findings carry stable IDs (`FL-nn`) so new ones can be
appended and old ones referenced. Add real-use observations under
[From live use](#from-live-use) and they'll be folded into the numbered list.

---

## Status — fix pass of 2026-08-21

Branch `fix/filename-list-audit-a-then-b`. Verified: **Core 660/660, Wpf 1764/1764**
(baselines 657 / 1760 before this pass; every new test was watched failing first).

**Closed:** FL-01, FL-02, FL-03, FL-04, FL-05, FL-06.
**Partially addressed, still open:** FL-10, FL-18, FL-23.
**Everything else is untouched and still open**, including FL-30 (Size renders raw bytes),
FL-08 (no progress on a big walk) and FL-13 (*Remove from list* has no toolbar button).

Also shipped in the same pass, not an audit item: a **Pages** column in `Columns ▾`,
counting visible PDFs four at a time, cached by full path, showing `…` while a count is
in flight, and cancelling when the column is unticked. `Tools → PDF page counts…` and its
own window are untouched.

---

## High — it lies to the user, or loses their work

### FL-01 · The extension filter hits the wrong empty state
`FilenameListViewModel.cs:224` · `FilenameListWindow.xaml:240`

> **FIXED** 2026-08-21 — `IsEmpty`/`NoMatches` now key on `_sources` (has anything been added?) rather than on what `Build` returned. Test: `TheTypeFilterHidingEverythingIsNoMatchesNotAnEmptyList`.

`ExtensionFilter` is applied inside `FilenameList.Build` (via `Intake.Expand`), so a
filter that matches nothing empties `_allRows`, not just `Rows`. `IsEmpty` is
`Rows.Count == 0 && _allRows.Count == 0` → **true** — so a user with 200 files already
added who types `zzz` into *Only these types* gets **"Drag files or folders here, or
browse…"**: an invitation to drop files that are already dropped.

This is the identical bug the `IsEmpty`/`NoMatches` split was written to fix for the
*Find* box. It fixed half of it: *Find* filters in `Reproject` (so `_allRows` survives
and `NoMatches` wins), *Only these types* filters in `Build` (so it doesn't).

### FL-02 · `*.pdf` in the type box silently matches nothing
`FolderMonitor.ParseFiletypes` · hint at `FilenameListWindow.xaml:94`

> **FIXED** 2026-08-21 — `ParseFiletypes` now does `Trim('*', '.')`, so `*.pdf` works and a bare `*` collapses to "everything". Fixed in the shared parser, so watch folders and Settings get it too. Tests: `FiletypesAcceptGlobStyleWildcards`, `ABareStarMeansEverythingNotALiteralToken`.

`ParseFiletypes` splits on space/comma/semicolon and does `TrimStart('.')`. `*.pdf` —
the single most likely thing a Windows user types into a box labelled *Only these
types* — keeps its `*`, becomes the token `*.pdf`, and matches no extension ever.
Result: zero rows, no error, and (because of FL-01) a "drag files here" empty state.
The hint text `pdf, docx — blank = all` is correct but sits to the right of the box in
`SubtleText` and is easy to miss.

### FL-03 · Changing a column, the sort, or the Find text silently drops your selection
`FilenameListViewModel.cs:328`

> **FIXED** 2026-08-21 — `Reproject` snapshots the selection before `Rows.Clear()` and restores it by `FullPath`, dropping any row the reproject hid; the window re-applies it to the grid via a new `SelectionRestored` event. Tests: `TheRowSelectionSurvivesAReprojection`, `ARestoredSelectionDropsRowsTheFilterHid`.

Every `Reproject()` does `Rows.Clear()`. That fires `DataGrid.SelectionChanged`, the
code-behind pushes an empty list into `SelectedPaths`, and the selection is gone — rows
are re-added unselected.

The damaging sequence is an ordinary one: *select the 5 rows you want → open
**Columns ▾** and turn on **Size** so the export carries it → click **Copy to
clipboard*** → you get **all 200 rows**, not your 5. `CopyText` deliberately means
"selection if there is one, everything otherwise", so the collapse is silent and looks
like a successful copy. Same trap for typing in *Find* and for ticking *Z to A*.

Selection is identity-keyed by `FullPath` already (`_excluded` uses exactly that trick),
so it can survive a reproject.

### FL-04 · The Copy button and Ctrl+C produce different text
`FilenameListWindow.xaml.cs:OnCopy` vs. the `DataGrid`'s built-in copy

> **FIXED** 2026-08-21 — `ClipboardCopyMode="None"` retires WPF's own copy, and Ctrl+C now calls the same `PerformCopy()` the button does. Test: `TheGridDoesNotRunItsOwnClipboardCopy`.

The grid is `IsReadOnly` with default `ClipboardCopyMode`, so **Ctrl+C still works** and
emits WPF's own tab-separated cells, **headerless**. The button emits
`FilenameList.ToText`, which for a table-shaped listing emits **a header row**, and for a
numbered list emits `1. name` rather than `1<tab>name`.

So the same selection yields two different clipboard payloads depending on how you
copied, with nothing on screen saying which you got. (The view model's own comment
claims these two paths were brought into agreement; they agree on *which rows*, not on
*what format*.)

### FL-05 · Delete removes rows with no confirmation, and the only undo is all-or-nothing
`FilenameListWindow.xaml.cs:OnGridKeyDown` · `FilenameListViewModel.cs:176`

> **FIXED** 2026-08-21 — removals are a stack; **Ctrl+Z** undoes the last batch (also on the context menu, with its gesture shown), *Restore removed* still clears the whole stack. Tests: `UndoRestoresOnlyTheLastRemovalBatch`, `RestoreRemovedStillClearsEveryBatchAtOnce`, `UndoWithNothingRemovedDoesNothing`.

`Delete` on the grid removes the selection immediately. There is no per-step undo:
**Restore removed** restores *everything* ever removed in this session. Curate 60 rows
down to 12, fat-finger Delete once more, and the only recovery is to restore all 48 and
start again. Ctrl+Z does nothing.

Minimum fix that keeps the current model: make **Restore removed** undo the *last*
removal batch, or add a Ctrl+Z that does.

### FL-06 · Success and failure are both amber, and this window is the outlier
`FilenameListWindow.xaml:22-23`

> **FIXED** 2026-08-21 — the view model now reports `StatusIsProblem`; the footer is `CaptionText` and turns amber only on a genuine failure, matching PageCountsWindow. Tests: `ASuccessfulSaveIsNotFlaggedAsAProblem`, `AFailedSaveIsFlaggedAsAProblem`, `ASuccessAfterAFailureClearsTheProblemFlag`.

`Status` uses `StatusText` → `Theme.StatusAmber`. It carries `Copied 12 names`,
`Saved to filenames.csv` **and** `Couldn't save: …` **and** `Clipboard busy — try again`
in the same warning colour.

`PageCountsWindow.xaml:22-27` — written for the same footer shape — puts its save/copy
feedback in `CaptionText` specifically so it doesn't compete with the amber result line,
and its comment cites *this* window as the model. The convention exists; this window is
the one not following it. It also breaks the "one meaning per colour" rule the v1 UI
audit already enforced elsewhere.

---

## Medium — friction that shows up in repeated real use

### FL-07 · Nothing the tool learns survives closing it
No persistence anywhere in `FilenameListViewModel`

Every open starts at: no columns, subfolders off, extension on, A→Z, blank filters. A
user whose actual job is "list this client folder recursively with Size and Modified"
re-checks four controls every single time. The window is `ShowDialog`, so this happens
on every use, not once per app run. Even remembering the last-used column set and
*Include subfolders* in config would remove most of it.

### FL-08 · No sign that a big walk is happening
`FilenameListViewModel.cs:Refresh` / `DebouncedProbe`

`Build` runs off-thread (correctly), but nothing tells the user it's running. Drop a
recursive network folder with 50k files and the window looks idle and unchanged until it
finishes — no spinner, no *Working…*, no cancel. The debounce (300 ms) also means a
re-walk begins ~300 ms after typing stops in *Only these types*, invisibly. **[live]** —
worth timing against a real share.

### FL-09 · Toggling *Include extension* re-walks the disk for a cosmetic change
`FilenameListViewModel.cs:IncludeExtension` → `Refresh(immediate: true)`

Showing/hiding `.pdf` changes only how a name is rendered, but it is baked into `Build`,
so the checkbox costs a full filesystem walk. On the folder in FL-08 that's a
multi-second stall for a display preference. It belongs in `Reproject`, with the
extension held on `FileRow`.

### FL-10 · The window's default size is too narrow for its own columns
`FilenameListWindow.xaml:4` (`Width="640"`)

> **PARTIALLY MITIGATED** 2026-08-21 — the default width went 640 → 760 to absorb the new Pages column, which would otherwise have made this worse. The all-columns-on case and the `MinWidth` 480 case are unchanged and still open.

With all six columns on, the columns' `MinWidth` floors sum to **630px** against ~612px
of content width at the *default* 640px size — a horizontal scrollbar the moment the
feature set is fully used, before anyone resizes anything. (The `MinWidth` 480 case is
already recorded as a known unfixable in `DataGridSizingCoverageTests`' `KnownUncovered`
register; the point here is the *default*, which is a free fix.) `PageCountsWindow`
defaults to 700 with three columns. Suggest ~820.

### FL-11 · Column headers look sortable and aren't
`FilenameListWindow.xaml` (`CanUserSortColumns="False"`)

Standard `DataGrid` headers with standard affordances that do nothing on click. There
are good reasons they're off (the `#` column has no real `SortMemberPath`; *Z to A* owns
sorting) but the user gets no signal — they click *File name*, nothing happens, they
click again. Either style the headers as non-interactive or move sorting onto them and
retire the checkbox.

### FL-12 · *Z to A* is a checkbox doing a two-state control's job
`FilenameListWindow.xaml:80`

A checkbox reads as "an option that is off", not "sorted A→Z right now". Its unchecked
state never says *A to Z* anywhere, so the current sort direction is only inferable from
the rows themselves. A two-item toggle (`A→Z` / `Z→A`) or header sorting (FL-11) states
it.

### FL-13 · *Remove from list* is right-click-and-Delete only
`FilenameListWindow.xaml:147` (context menu) · no toolbar button

Curation is the feature that makes this more than a `dir` command, and it is hidden
behind a context menu and an unlabelled Delete key. `PageCountsWindow` puts **Remove
selected** on the toolbar as a plain button. This window has room on the second
WrapPanel row next to **Restore removed**.

Related: the context menu hangs off the whole grid, so right-clicking empty space still
offers **Remove from list**, enabled, doing nothing.

### FL-14 · Buttons never disable, so several clicks are silent no-ops
`RelayCommand` constructed without `canExecute` at `FilenameListViewModel.cs:163,176`
and throughout

- **Copy to clipboard** with an empty list: `OnCopy` returns early on empty text — no
  clipboard write, **and no status message**. The click is indistinguishable from a
  broken button.
- **Restore removed** with nothing removed: returns early, no feedback, and the button
  is fully enabled the entire session even when it can never do anything.
- **Clear** on an already-empty list: same.
- **Save** with zero rows: writes a **0-byte .txt** (or a header-only .csv) and reports
  `Saved to filenames.txt` — a confident success for an empty file.

### FL-15 · *Restore removed* doesn't say how many
`FilenameListWindow.xaml:82`

`RemovedCount` exists and is already raised on the view model; the count only appears
buried in `CountsLine` as `· 3 removed`. `Restore 3 removed` on the button answers "did
my Delete actually take?" without a trip to the footer.

### FL-16 · Clear wipes everything with no confirmation and no undo
`FilenameListViewModel.cs:163`

`Clear` drops the sources *and* the removal set. After twenty minutes of curating a
600-file listing, one click on a button sitting 6px from **Browse files…** ends it. No
prompt, no restore. At minimum it should be undoable while the window is open.

### FL-17 · Stale `AddNote` and `Status` outlive what produced them
`FilenameListViewModel.cs:163` (Clear), `:255` (AddNote), `:380` (Status)

Neither is cleared by `Clear`, so `nothing new — already listed` and
`Saved to filenames.csv` sit beside a now-empty window, describing a state that no
longer exists. `Status` likewise survives across a whole new folder being added.

### FL-18 · The footer grows and shoves the grid when a save fails
`FilenameListWindow.xaml:22-23`

> **PARTIALLY FIXED** 2026-08-21 — the status line now trims instead of wrapping, so a long save error no longer grows the footer and squeezes the grid. The full text is still not recoverable (no tooltip), so this stays open.

`StatusText` sets `TextWrapping="Wrap"`; `Status` here is capped at `MaxWidth="240"` with
no trimming. `Couldn't save: {ex.Message}` — real .NET exception text, often a full path
— wraps to three or four lines, growing the bottom `DockPanel` and shrinking the grid
under it. `PageCountsWindow` uses `TextTrimming="CharacterEllipsis"` for the same slot.
**[live]** — easy to reproduce by saving to a read-only location.

### FL-19 · Dropping onto either text box probably loses the drop
`FilenameListWindow.xaml:64` (*Find*), `:92` (*Only these types*)

`AllowDrop` is on the `Window`, but WPF's `TextBoxBase` installs its own drag handlers
and marks the event handled, so files dropped on a text box never reach `OnDrop`. Both
boxes sit in the top third of the window — squarely inside the "just drop it anywhere in
here" target the empty state advertises. `PageCountsWindow` has no text boxes and so
never hit this. **[live]** — drag a folder onto the *Find* box and see whether anything
lists.

### FL-20 · Nothing indicates the window is a drop target while you drag
`OnDragOver` sets `e.Effects` only

The cursor changes; the window doesn't. No border highlight, no "drop here" state. The
empty-state text disappears the moment the first file lands, so on a second drop there is
no visible target at all. Consistent with the sibling tools, so this is a whole-family
finding rather than a regression — worth fixing once in the shared style.

---

## Low — polish, wording, accessibility

### FL-21 · "ignored" conflates *you filtered these out* with *these are broken*
`FilenameListViewModel.cs:340` (`FormatCounts`) · `Intake.cs:155`

`Intake.AddIfMatches` increments `Ignored` for an extension mismatch, and so does the
"neither a file nor a folder" branch. So filtering 200 files down to 3 on purpose renders
as `3 files · 197 ignored` — the same word, in the same slot, as 197 missing or
unreadable paths. `Intake.Added` already keeps `WrongType`/`Missing`/`Unusable` apart for
exactly this reason; `Expanded.Ignored` doesn't.

### FL-22 · *Find* searches names only, even when *Full path* is on
`FilenameListViewModel.cs:Reproject`

The filter is `r.Name.Contains(...)`. Turn on the *Full path* or *Folder* column — i.e.
put paths on screen and make them look searchable — and typing a folder name into *Find*
returns nothing. Either extend the match to the visible columns or label the box *Find in
name*.

### FL-23 · The two hidden-row mechanisms share one message
`FilenameListWindow.xaml:249`

> **PARTIALLY FIXED** 2026-08-21 — the message no longer asserts a cause that may be false ("filtered out, removed, or the folder was empty"), a state FL-01's fix made reachable. It still offers no way out, so this stays open.

*"Nothing to show — every file is filtered out or removed."* covers both the *Find* text
and the removals, and offers no way out. Naming the cause and offering the action ("no
name matches **draft** — clear Find") turns a dead end into one click.

### FL-24 · Intake controls and view controls are interleaved across three rows
`FilenameListWindow.xaml:40-96`

Row 1: *Browse / Clear / Include subfolders / Include extension*.
Row 2: *Find / Columns ▾ / Z to A / Restore removed*.
Row 3: *Only these types*.

So the two controls that re-read the disk (*Include subfolders*, *Only these types*) are
separated by a row of controls that only re-render what's already in memory, and nothing
distinguishes the expensive ones from the instant ones. Grouping "what gets read" above
"what gets shown" would make FL-08's invisible re-walks predictable.

### FL-25 · No accessible names on this window's inputs
`FilenameListWindow.xaml` — no `AutomationProperties` anywhere

`HistoryWindow.xaml:11` gives its Find box `AutomationProperties.Name="Find in history"`;
BulkRename, LabelMaker, About and the main views all label their controls. This window
labels none — the two text boxes, the grid, and the **Columns ▾** menu (whose accessible
name includes the bare `▾` glyph) all read poorly to a screen reader.

### FL-26 · Find label spacing is off-canon
`FilenameListWindow.xaml:63`

`Margin="0,4,6,4"` uses the 6px *button→button* toolbar gap for a *label→control* pair,
which the canon in `Styles.xaml:1934-1940` sets at 8 (`FieldLabel`). `HistoryWindow` uses
`0,0,8,0` for the same "Find:" label. One-line drift, cosmetic.

### FL-27 · No default button, and Enter does nothing
`FilenameListWindow.xaml:11`

`Close` is `IsCancel` (Esc works). `AboutWindow` and `ManageSavedWindow` mark their Close
`IsDefault` too, so Enter dismisses them; here Enter is inert everywhere, including in
both text boxes. Given the grid takes focus, Enter is probably best left inert — noting
it only so the inconsistency is a decision rather than an oversight.

### FL-28 · `ToText` and `ToCsv` disagree about the empty listing
`FilenameList.cs:ToText` (returns `""`) vs `ToCsv` (returns a header row)

Saving an empty listing produces a 0-byte `.txt` but a 1-line `.csv`. Not a defect either
way; it means the shape of "nothing" depends on which column happens to be on. See FL-14.

### FL-29 · `AddNote` truncates the sentence that explains what went wrong
`FilenameListWindow.xaml:41` (`MaxWidth="240"`, `CharacterEllipsis`)

`Intake.Added.Note` composes lines like
`4 added · 3 ignored (2 already listed · 1 doesn't exist)` — the parenthetical carries the
whole diagnosis, and it's the end of the string, so it's the first thing the ellipsis
eats. No tooltip carries the full text.

### FL-30 · The Size column renders raw bytes
`FilenameListWindow.xaml` (`Binding="{Binding Size}"`, no `StringFormat`) ·
`FilenameList.cs:Cell` (`row.Size?.ToString(InvariantCulture)`)

> **FIXED** 2026-08-21 in a sense that matters less than it looks: the *Pages* column added in the same pass renders through `FileRow.PageCell`, but **Size still renders raw bytes**. This finding is UNCHANGED and open.

`FileRow.Size` is a `long?` and nothing formats it, so the grid shows **`4293904`** and
the export writes the same. Every other tool that shows a size to a user formats it;
there is no size-formatting helper anywhere in Core or Wpf to reach for, which is why
this was never done. Raising it as a finding rather than folding it silently into the
manifest work: it is a defect in the shipped tool today, independent of that work.

Addressed by `docs/superpowers/specs/2026-08-20-file-manifest-columns-design.md` §2.1.

---

## From live use

Add observations here as you hit them — a sentence and roughly where in the flow is
plenty. They'll be triaged into the numbered list above with an `FL-nn` of their own.

- _(nothing yet)_

---

## Deliberately not listed

- **`DataGridColumnCap.Track` is not called here**, and a collapsed column's
  `ActualWidth` reports its `MinWidth` rather than 0. Both are already measured and
  recorded in `DataGridSizingCoverageTests`' `KnownUncovered` register with numbers.
  FL-10 covers only the part of it that is a plain default-size choice.
- **Modality.** The window is `ShowDialog`, so the main window is blocked while you build
  a list. Every Tools window behaves this way, so it's a family decision, not a finding
  about this one.
