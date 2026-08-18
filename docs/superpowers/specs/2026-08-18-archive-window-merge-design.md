# One archive window

**Date:** 2026-08-18
**Status:** approved, ready for planning

## Problem

The Tools menu carries three entries that are all about zip files: *Zip*,
*Unzip*, and *Merge PDFs from zip*. A person who has a zip in hand must decide
which of the three tools they wanted **before** opening one, and a person who
guesses wrong closes the window and starts again. Nothing in the menu explains
that two of those entries take a zip as input and the third produces one.

Underneath, the three are largely the same program written three times.
`UnzipViewModel` and `ZipMergeViewModel` are near-clones: their intake, list
handling, `RemoveSelected`, `Clear`, cancellation, and batch loops are
line-identical, differing only in which row type they build and how many
buckets they tally. `UnzipViewModel`'s own doc comment states it outright —
"this class is ZipMergeViewModel with the merge step swapped for an extract
step". The three windows share one skeleton (`DockPanel` at margin 14, a
bottom footer with a right-docked Close, a toolbar row, a `DataGrid` with an
overlaid empty state) and differ only in title, size, button labels, and one
column.

That duplication has already cost real defects. The 2026-08-09 UI audit found
the same Error-as-amber bug in `ZipMergeWindow.xaml` and `UnzipWindow.xaml`,
and the same uncapped Result column in both — one bug class, fixed twice
because it existed twice.

## Goal

One window that handles all three jobs, chooses the job from what the person
gives it rather than from a mode they set, and deletes the duplication rather
than relocating it.

## Approach

One view model (`ArchiveViewModel`), one row type (`ArchiveRow`), one window
(`ArchiveWindow`), replacing all three of each. One Tools entry replacing
three.

**The Core engines do not change.** `Zipper.CreateZip`, `Zipper.Extract`, and
`ZipMerge.MergeZip` keep their current signatures and behaviour, and
`ZipperTests` / `ZipMergeTests` are untouched. This is a UI-layer
consolidation, which is what keeps its risk proportionate to its reach.

Two alternatives were rejected:

- **Merge the windows, keep three view models.** The list has to survive a
  person dropping files and *then* a zip, so three view models means three
  lists to keep in sync — two sources of truth for the one thing the window
  is about.
- **One window dispatching to three operation strategy objects.** Each
  operation is a single Core call. That is ceremony without payoff.

A tabbed window (a Create tab and an Extract tab) was considered and rejected
earlier in design: it preserves the mode-picking the merge exists to remove,
and leaves the two tabs as near-duplicates of each other.

## Behaviour

### The counts are the contract

Each action button carries its own count, so its scope is legible without a
rule anyone has to learn:

```
[ Zip 5 items… ]  [ Zip to… ]     [ Extract 2 zips… ]  [ Merge 2 zips… ]
```

A list of three PDFs and two zips reads exactly that way, and no button can be
misread about what it will touch.

### Enablement

| Action | Enabled when | Acts on |
|---|---|---|
| Zip, Zip to… | the list is not empty | every row |
| Extract | at least one zip row is `Pending` | pending zip rows |
| Merge PDFs | at least one zip row is `Pending` | pending zip rows |

A `.zip` is still a file, and bundling archives is legitimate, so Zip never
excludes anything: its count is always `Rows.Count`.

### Three deliberate consequences

**Intake becomes permissive.** Today Unzip and ZipMerge reject a non-zip with
"1 isn't a zip". In a merged window that rejection is wrong — a PDF is valid
input, just for a different button. `AddPaths` accepts any file, folder, or
zip that exists, and the retired rejection takes one test fact from each of
those two suites with it. `AddNote` uses Zip's noun ("item"), which covers all
three kinds.

**A processed zip is not processed twice.** Both batch tools act only on
`Pending` rows today, and that is preserved. The consequence is new to the
merged window: after extracting a zip, *Merge PDFs* for that same archive has
nothing pending to act on. This is visible rather than silent — the button
disables once no unprocessed zips remain. Doing both operations on one archive
is unusual; the escape is Clear or Remove selected. Per-action completion
tracking (one extra field on the row) was considered and deliberately not
built.

**Zip ignores results.** It folds whatever the list holds, so after extracting
three zips, `Zip 3 items` zips the three original archives, not their output.
That is what the list shows.

### Surface details

- `Kind` gains a third value, `zip`, beside `file` and `folder`. It is also
  the visual cue for why Extract and Merge lit up.
- Window: 700×520, min 580×420 — ZipMerge's dimensions, the largest of the
  three, and the one carrying the most content.
- Extract and Merge stay cancellable (`_cts`, cancelled on window close). Zip
  stays non-cancellable, being a single operation.
- Empty state: "Drag files, folders or zips anywhere on this window, or press
  Add…"
- Tools menu: `_Zip & extract…` replaces all three entries. Merge PDFs is a
  button inside the window, not a menu entry.

## Structure

### `ArchiveRow`

The union of today's three rows, living beside its view model as they do:

| Member | From | Notes |
|---|---|---|
| `Path` | all three | identity, dedupe key |
| `Display` | `PathRow.Display` | folder → `DirectoryInfo.Name`, else file name |
| `Kind` | `PathRow.Kind` | `file` \| `folder` \| `zip` |
| `IsZip` | new | `Kind == "zip"`, drives enablement |
| `StatusKind` | `UnzipRowStatus` ∪ `ZipRowStatus` | `Pending` \| `Ok` \| `NoPdfs` \| `Error` |
| `Note` | Unzip, ZipMerge | the Result column |
| `Output` | `OutputFolder` / `Output` | one name for both |

Both `Apply` overloads survive unchanged, one per result record
(`Zipper.UnzipResult`, `ZipMerge.MergeResult`).

### `ArchiveViewModel`

Constructor takes the three operation seams the current view models take
individually, keeping every existing test's injection style:

```csharp
ArchiveViewModel(
    IDialogService dialogs,
    IWorkScheduler? scheduler = null,
    SynchronizationContext? uiContext = null,
    Func<IReadOnlyList<string>, string?, Zipper.ZipResult>? zipper = null,
    Func<string, Zipper.UnzipResult>? extractor = null,
    Func<string, ZipMerge.MergeResult>? merger = null)
```

Shared surface: `Rows`, `AddNote`, `Status`, `AddPaths`, `RemoveSelected`,
`ClearCommand`, `Cancel()`.

Zip's single-shot `Status` and the batch tools' running `Summary` become **one**
`Status` property feeding one footer line: a verdict after zipping, a running
tally during a batch.

Commands and their labels: `ZipCommand` / `ZipAsCommand` (`ZipButtonText`),
`ExtractCommand` (`ExtractButtonText`), `MergeCommand` (`MergeButtonText`).
Enablement comes from `CanZip` / `CanExtract` / `CanMerge`, recomputed on
`Rows.CollectionChanged` and after every run.

The fold/map split is two private runners:

- `ZipAsync(string? outputPath)` — the fold. N items → one archive, one
  verdict.
- `RunBatchAsync(Func<ArchiveRow, Task<...>> operation, tally labels)` — the
  map. Walks pending zip rows, honours the cancellation token, updates
  `Status` per item, tallies at the end.

**Extract and Merge are the same runner with a different operation.** That is
the duplication this design exists to delete.

### Files

Created: `ViewModels/ArchiveViewModel.cs`, `Windows/ArchiveWindow.xaml`,
`Windows/ArchiveWindow.xaml.cs`.

Deleted: `ViewModels/{Zip,Unzip,ZipMerge}ViewModel.cs`,
`Windows/{Zip,Unzip,ZipMerge}Window.xaml{,.cs}`.

Modified: `MainWindow.xaml` (three menu items → one), `MainWindow.xaml.cs`
(three handlers → one).

The window's code-behind follows `UnzipWindow.xaml.cs`: `DataGridColumnCap.Track`
on the Result column in the constructor, `OnClosed → _vm.Cancel()`, and the
shared `OnDragOver`/`OnDrop` pair. Its Add buttons are "Add files…", "Add
folder…", "Remove selected", "Clear" — Zip's toolbar, which is the superset.

## Testing

### View models

The 37 facts across `ZipViewModelTests` (11), `UnzipViewModelTests` (13), and
`ZipMergeViewModelTests` (13) collapse into one `ArchiveViewModelTests`. Most
port verbatim under renamed types: dedupe and case-only dedupe, missing paths,
button-text wording, Clear, `RemoveSelected`, per-row status and note, summary
clauses, only-pending-on-second-run, cancel-between-items, Save-As passing the
chosen path, a cancelled dialog being a no-op, and the real-engine smoke tests.

Retired deliberately: the two "a non-zip drop is rejected" facts, which the new
intake contradicts by design.

Added, covering what is genuinely new:

- Zip enables on any non-empty list; Extract and Merge only with a pending zip.
- A mixed list reports the three counts independently (5 items / 2 zips / 2
  zips).
- Extract and Merge each act only on zip rows, leaving loose files untouched.
- After extracting, Merge disables rather than silently skipping.
- `Kind` is `zip` for a `.zip`, `folder` for a directory, `file` otherwise.

### Registries

Six hand-maintained registries name these windows by string. Each has a mirror
check that fails when a name no longer resolves to a real window, so an omission
breaks the build rather than rotting.

Both suites' reflection floors (`>= 8` and `>= 10`) count *every* window type
under `OrdoSort.Wpf.Windows`, not just grid windows. That total goes 16 → 14,
so neither floor is at risk.

| File | Change |
|---|---|
| `DataGridWindowCoverageTests` | `CoveredWindows`: three names → `ArchiveWindow`. Its doc comment listing nine grid windows by name needs updating — grid windows go 9 → 7. |
| `DataGridSizingCoverageTests` | `SizingCovered` gains `ArchiveWindow`; `ZipWindow` leaves `KnownUncovered` — the merged window has a capped Result column, so it graduates. |
| `WindowOverflowTests` | Three registry entries → one, at 580/700 × 420/520. |
| `AutoFitColumnTests` | Two builders → one; six Result-column facts → three. |
| `DataGridSelectionContrastTests` | Three builders → one; six theories → two. |
| `DataGridNoteColourTests` | Two assert helpers → one; the Error and NoPdfs colour facts both target the merged Result column. |

### End to end

The three scenario groups stay. Zip, Unzip and Zip merge test three real
behaviours and keep their own surface names, so `e2e.bat zip` and the
exact-match-first filter keep working unchanged; only their view-model and
window construction re-points. `ScenarioKit`'s doc comment naming three intake
methods needs one edit.

## Docs

`README.md` — the feature list (line ~105) loses two of the three tool names;
the `e2e.bat` filter note (~151) is unaffected, since the surfaces survive.
`e2e.bat` — the "14 surfaces" header comment.

Older specs, plans and audits under `docs/superpowers/` reference the three
windows as historical record and are left alone.

## Out of scope

Per-action completion tracking on a row. Any change to `Zipper` or `ZipMerge`.
Password-protected archives. Any change to the other Tools windows, whose
duplication with each other is real but not what this addresses.
