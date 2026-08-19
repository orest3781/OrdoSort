# One zip window

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

One window and one Tools entry covering all three jobs, deleting the
duplication rather than relocating it, without asking anyone to pick a mode
for a decision the input already answers.

## Approach

One window (`ZipToolsWindow`) with **two tabs**, replacing three windows and
three menu entries.

**Zip & unzip** holds one list and chooses its action from the contents:
loose files and folders can be zipped, archives can be extracted, and the
buttons light accordingly. Zip and Extract are inverse operations on the same
objects, so nothing is gained by making a person declare which one they meant.

**Merge PDFs** is its own tab with its own list. It consumes archives and
produces a document, which makes it a different job wearing a zip costume, not
a third mode of the same one.

The tab split is doing real work. It removes the only genuine ambiguity in an
auto-detecting surface — drop a zip, did you mean extract or merge? — by
having the answer already given. It also means a zip extracted on one tab has
no bearing on merging on the other, because the lists are separate. An earlier
draft of this design folded all three actions onto one list and had to accept
that extracting an archive left *Merge PDFs* dead for it until the list was
cleared; the tab makes that problem cease to exist rather than be tolerated.

Internally the two tabs share everything except their actions, so this is
*less* code than a single combined view model, not more (see Structure).

Two alternatives were rejected:

- **Three tabs — Create, Extract, Merge.** Preserves the mode-picking the
  merge exists to remove, and leaves Create and Extract as near-duplicate tabs
  of each other when they are the one pair that genuinely benefits from
  auto-detection.
- **One window dispatching to three operation strategy objects.** Each
  operation is a single Core call. That is ceremony without payoff.

**The Core engines do not change.** `Zipper.CreateZip`, `Zipper.Extract`, and
`ZipMerge.MergeZip` keep their current signatures and behaviour, and
`ZipperTests` / `ZipMergeTests` are untouched. This is a UI-layer
consolidation, which is what keeps its risk proportionate to its reach.

## Behaviour

### Zip & unzip

The tab takes any file, folder, or archive that exists. Each action button
carries its own count, so its scope is legible without a rule anyone has to
learn:

```
[ Zip 5 items… ]  [ Zip to… ]     [ Extract 2 zips… ]
```

A list of three PDFs and two archives reads exactly that way, and neither
button can be misread about what it will touch.

| Action | Enabled when | Acts on |
|---|---|---|
| Zip, Zip to… | the list is not empty | every row |
| Extract | at least one zip row is `Pending` | pending zip rows |

An archive is still a file, and bundling archives is legitimate, so Zip never
excludes anything: its count is always `Rows.Count`.

Two consequences, both deliberate:

**Intake becomes permissive on this tab.** Today Unzip rejects a non-zip with
"1 isn't a zip". Here that rejection would be wrong — a PDF is valid input,
just for the other button. `AddPaths` accepts anything that exists, which
retires one fact from `UnzipViewModelTests`. `AddNote` uses Zip's noun
("item"), which covers all three kinds.

**Zip ignores results.** It folds whatever the list holds, so after extracting
three archives, `Zip 3 items` zips the three originals, not their output. That
is what the list shows.

### Merge PDFs

Its own list, its own toolbar, its own Merge button — today's ZipMerge
behaviour unchanged, including its zips-only intake and the "isn't a zip"
rejection, which stays correct on a tab that can only merge. Pending-only
processing is likewise unchanged: merging twice leaves already-merged rows
alone, exactly as it does today.

### Shared surface details

- `Kind` gains a third value, `zip`, beside `file` and `folder`. On the Zip &
  unzip tab it is also the visual cue for why Extract lit up.
- Window: 700×520, min 580×420 — ZipMerge's dimensions, the largest of the
  three, and the one carrying the most content.
- Extract and Merge stay cancellable (`_cts`, cancelled on window close). Zip
  stays non-cancellable, being a single operation.
- Empty state, Zip & unzip: "Drag files, folders or zips anywhere on this
  window, or press Add…". Merge PDFs keeps "Drag zips anywhere on this
  window, or press Add zips…".
- Tools menu: `_Zip and unzip…` replaces all three entries. Window title
  "OrdoSort — Zip and unzip".

## Structure

### `ZipItemRow`

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

### Three view models, one base

Each tab owns its own list, so the machinery both need lives in a base class
and each tab adds only its actions. This is the shape a shared base is
actually for; an earlier draft rejected one because three view models would
have had to share a single list, and that objection does not apply once each
tab has its own.

**`ZipListViewModel`** (abstract) owns everything shared: `Rows`,
`AddNote`, `Status`, `AddPaths`, `RemoveSelected`, `ClearCommand`, `Cancel()`,
the `Rows.CollectionChanged` wiring, and the cancellable batch runner

```csharp
protected Task RunBatchAsync(Func<ZipItemRow, ...> operation, tally labels)
```

which walks pending zip rows, honours the token, updates `Status` per item and
tallies at the end. `AddPaths` takes a virtual "does this path belong here"
predicate so the two tabs differ by one override rather than by a copied
method.

**`ZipExtractViewModel`** adds `ZipCommand`, `ZipAsCommand`, `ExtractCommand`,
their button texts, and `CanZip` / `CanExtract`. It holds the fold —
`ZipAsync(string? outputPath)`, N items into one archive with one verdict —
and calls the inherited runner for Extract. It is the only view model taking
`IDialogService` (Save-As).

**`MergePdfsViewModel`** adds `MergeCommand`, `MergeButtonText`, `CanMerge`,
and calls the inherited runner with the merge operation. Its path predicate
accepts zips only.

**Extract and Merge are the same inherited runner with a different
operation.** That is the duplication this design exists to delete.

`ZipToolsWindow`'s `DataContext` is a small shell holding one instance of each
subclass, one per tab.

Zip's single-shot `Status` and the batch tools' running `Summary` become one
`Status` per tab: a verdict after zipping, a running tally during a batch.

### Files

Created: `ViewModels/ZipListViewModel.cs` (base + `ZipItemRow` +
`ZipItemRowStatus`), `ViewModels/ZipExtractViewModel.cs`,
`ViewModels/MergePdfsViewModel.cs`, `ViewModels/ZipToolsViewModel.cs` (shell),
`Windows/ZipToolsWindow.xaml`, `Windows/ZipToolsWindow.xaml.cs`.

Deleted: `ViewModels/{Zip,Unzip,ZipMerge}ViewModel.cs`,
`Windows/{Zip,Unzip,ZipMerge}Window.xaml{,.cs}`.

Modified: `MainWindow.xaml` (three menu items → one), `MainWindow.xaml.cs`
(three handlers → one).

The window follows `SettingsWindow` for its `TabControl` and
`UnzipWindow.xaml.cs` for everything else: `DataGridColumnCap.Track` on each
tab's Result column in the constructor, `OnClosed` cancelling both tabs, and
the shared `OnDragOver`/`OnDrop` pair routed to whichever tab is selected. The
Zip & unzip toolbar is Zip's, which is the superset: "Add files…", "Add
folder…", "Remove selected", "Clear". Merge PDFs keeps "Add zips…", "Remove
selected", "Clear".

## Testing

### View models

The 37 facts across `ZipViewModelTests` (11), `UnzipViewModelTests` (13), and
`ZipMergeViewModelTests` (13) become two suites, split the way the tabs are:

- **`ZipExtractViewModelTests`** — every Zip fact and every Unzip fact, ported
  under renamed types: dedupe and case-only dedupe, missing paths, button-text
  wording, Clear, `RemoveSelected`, per-row status and note, summary clauses,
  only-pending-on-second-run, cancel-between-zips, Save-As passing the chosen
  path, a cancelled dialog being a no-op, and both real-engine smoke tests.
  This suite also covers the inherited machinery, deliberately rather than
  incidentally: it exercises the richer subclass, so no test-only subclass of
  the base is needed.
- **`MergePdfsViewModelTests`** — the 13 ZipMerge facts, including the
  zips-only intake and the "isn't a zip" rejection, which survive here
  unchanged.

Retired deliberately: the single "a non-zip drop is rejected" fact on the
extract path, which the permissive Zip & unzip intake contradicts by design.
Its Merge-tab counterpart survives.

Added, covering what is genuinely new:

- Zip enables on any non-empty list; Extract only with a pending zip.
- A mixed list reports its two counts independently (5 items / 2 zips).
- Extract acts only on zip rows, leaving loose files untouched.
- `Kind` is `zip` for a `.zip`, `folder` for a directory, `file` otherwise.
- The two tabs' lists are independent: extracting on one leaves the other's
  rows pending.

### Registries

Six hand-maintained registries name these windows by string. Each has a mirror
check that fails when a name no longer resolves to a real window, so an
omission breaks the build rather than rotting.

Both suites' reflection floors (`>= 8` and `>= 10`) count *every* window type
under `OrdoSort.Wpf.Windows`, not just grid windows. That total goes 16 → 14,
so neither floor is at risk.

| File | Change |
|---|---|
| `DataGridWindowCoverageTests` | `CoveredWindows`: three names → `ZipToolsWindow`. Its doc comment listing nine grid windows by name needs updating — grid windows go 9 → 7. |
| `DataGridSizingCoverageTests` | `SizingCovered` gains `ZipToolsWindow`; `ZipWindow` leaves `KnownUncovered` — the merged window has capped Result columns, so it graduates. |
| `WindowOverflowTests` | Three registry entries → one, at 580/700 × 420/520, with `ProbeEveryTab: true` — only the selected tab's content exists in the visual tree, exactly as for `SettingsWindow`. |
| `AutoFitColumnTests` | Two builders → one, selecting the tab under test before measuring; six Result-column facts → three per tab where the column differs. |
| `DataGridSelectionContrastTests` | Three builders → one, again selecting the tab; six theories → two. |
| `DataGridNoteColourTests` | Two assert helpers → one; the Error and NoPdfs colour facts target their own tab's Result column. |

Every window-level builder must select its tab before measuring or resolving
descendants, since a `TabControl` realises only the selected tab's content.

### End to end

The three scenario groups stay. Zip, Unzip and Zip merge test three real
behaviours and keep their own surface names, so `e2e.bat zip` and the
exact-match-first filter keep working unchanged; only their view-model and
window construction re-points, and the window's tab is selected before a
scenario drives it. `ScenarioKit`'s doc comment naming three intake methods
needs one edit.

## Docs

`README.md` — the feature list (line ~105) loses two of the three tool names;
the `e2e.bat` filter note (~151) is unaffected, since the surfaces survive.
`e2e.bat` — the "14 surfaces" header comment.

Older specs, plans and audits under `docs/superpowers/` reference the three
windows as historical record and are left alone.

## Out of scope

Any change to `Zipper` or `ZipMerge`. Password-protected archives. Moving
files between the two tabs' lists. Any change to the other Tools windows,
whose duplication with each other is real but not what this addresses.
