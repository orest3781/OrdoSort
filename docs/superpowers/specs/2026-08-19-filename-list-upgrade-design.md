# The filename list becomes a manifest

**Date:** 2026-08-19
**Status:** approved, ready for planning

## Problem

*Filename list* takes dropped files and folders and produces a flat,
natural-sorted list of names to paste into a manifest or a spreadsheet. It does
that one job and stops there, and the gap between "a list of names" and "a
manifest" is where every complaint below sits.

**You cannot remove one thing.** `ClearCommand` empties `_sources` entirely.
Drop three folders, notice the second one was wrong, and the only move is to
start over. The grid is even declared `SelectionMode="Extended"`
(`FilenameListWindow.xaml:63`) — a selection exists, and nothing in the window
or the view model consumes it. There is no Delete key handler and no context
menu.

**Copy ignores that selection.** `OnCopy` copies `_vm.OutputText`
(`FilenameListWindow.xaml.cs:22`), which is every row. Select five rows out of
two hundred, press *Copy to clipboard*, and two hundred names land on the
clipboard. `Ctrl+C` inside the `DataGrid` copies only the selection, so the
button and the keyboard disagree about what "copy" means in the same window.

**"or browse…" is half true.** The empty state reads *"Drag files or folders
here, or browse…"* while the only browse button is **Browse folder…**.
`IDialogService.AskOpenFile` already exists and this view model never calls it.
There is no way to reach an individual file through a dialog.

**It is names and nothing else.** `FilenameList.Listing` carries
`IReadOnlyList<string>`, so the grid has exactly one column. A manifest almost
always wants size and modified date, and a listing assembled from several
folders loses which folder each row came from — the flattening is deliberate,
but it is also lossy and nothing records what was lost.

**Save writes `.txt` only**, even though `Csv.EscapeField`/`Csv.WriteRow`
already exist in Core and `History.cs` already exports through them.

**There is no sort or filter control.** The order is always natural-ascending
by name, and the extension box is the only way to narrow a listing — there is
nothing to search five thousand rows by name.

## Goal

Turn the output from a list of names into a table the user chooses the shape
of, and make the list something they curate rather than rebuild — without
moving filesystem or formatting logic out of Core, and without changing what
the tool produces for anyone who touches none of the new controls.

## Approach

**Core returns rows that are always fully populated.** `Listing` carries a
`FileRow` per file with every field filled, and column visibility becomes
purely a UI concern.

The alternative — having `Options` name the wanted columns so `FileInfo` only
runs when Size or Modified is on — saves I/O in the common case and was
rejected on interaction grounds, not cost. Under it, ticking *Size* starts a
fresh filesystem walk of every root, through the 300ms debounce, on whatever
network share the roots live on, for a column the user may tick and untick
while deciding. Under this approach the data is already in memory and the
toggle is instant. A `Listing` whose fields mean different things depending on
its `Options` is also markedly harder to test than one fixed shape.

**Text generation moves out of the window and into Core**, so the rule
governing what reaches the clipboard is a pure function with tests rather than
string-building in a code-behind.

## Behaviour

### What you see is what you copy

One rule governs both Copy and Save:

- **Name is the only data column** → plain lines, no header. The `#` column, if
  on, renders as a `1. ` prefix.
- **Any of Size / Modified / Folder / Full path is on** → tab-separated text
  with a header row, and `#` becomes its own column.

Name-only, `#` on:

```
1. invoice-2024.pdf
2. invoice-2025.pdf
```

Size and Modified on:

```
Name	Size	Modified
invoice-2024.pdf	241152	2026-03-04 14:22
invoice-2025.pdf	198656	2026-03-09 09:05
```

Nothing displays one value and copies another. A user who turns on no new
columns gets byte-for-byte what the tool produces today.

### Values are spreadsheet-shaped, not screen-shaped

`Size` is the raw byte count and `Modified` is `yyyy-MM-dd HH:mm`, invariant
culture. The tool's stated purpose is pasting into a spreadsheet, and those are
the forms that survive the paste: the size sorts and sums as a number, and the
date is unambiguous in every locale. `241 KB` and `3/4/2026` read better in the
grid and arrive in Excel as text that cannot be summed and a date that can be
misread.

### Unreadable files render blank

`Size` and `Modified` are nullable. `FilenameList.Build`'s contract is that it
never throws, and a file that `Intake.Expand` enumerated can be gone, locked or
access-denied by the time `FileInfo` reads it — a real race on the kind of
watched intake folder this app is built around. Null renders and exports as an
empty cell rather than lying with `0` bytes or a 1601 date. The row itself
stays: it was really there in the walk.

### Removal survives a rebuild

Removing rows cannot be `Rows.Remove`. Drop a folder of a hundred files, delete
three rows, then type one character in the extension box: the rebuild re-walks
that folder and the three come straight back.

Removal is therefore an **exclusion set keyed on full path**, held in the view
model and applied after every `Build`, surviving every rebuild until `Clear`.
The counts line accounts for it — `200 files · 3 removed` — so hidden rows are
never silently missing, and **Restore removed** puts them back.

Exclusion stays in the view model. `Build` produces what is on disk; what the
user has chosen to hide from that is not something a pure listing function
should know.

### Copy follows the selection

With rows selected, Copy takes those rows; with none selected, it takes all of
them. The status says which (`Copied 5 of 200`), so neither case is silent.
This aligns the button with the `Ctrl+C` the grid already supports.

### The rest

- **Delete key and a right-click *Remove*** on the grid, driving the exclusion
  set through the selection that exists today and does nothing.
- **Browse files…**, alongside the existing folder browser, making the empty
  state's promise true.
- **A name filter box** — substring, case-insensitive, applied in memory after
  `Build`. Unlike the extension filter it never re-walks the filesystem.
- **Sort direction** — natural ascending (today's behaviour) or descending,
  applied to the in-memory projection so flipping it never re-reads the disk.

## Structure

### `src/OrdoSort.Core/FilenameList.cs`

```csharp
public sealed record FileRow(
    string    Name,      // honours IncludeExtension, as today
    long?     Size,      // null when the file could not be read
    DateTime? Modified,  // null likewise
    string    Folder,    // directory relative to the root it came from; "" at the root
    string    FullPath);

public sealed record Listing(IReadOnlyList<FileRow> Rows, int Ignored, string Error = "");

[Flags]
public enum Columns { None = 0, Number = 1, Size = 2, Modified = 4, Folder = 8, FullPath = 16 }

public static Listing Build(IReadOnlyList<string> paths, Options opt);
public static string  ToText(IReadOnlyList<FileRow> rows, Columns cols);
public static string  ToCsv (IReadOnlyList<FileRow> rows, Columns cols);
```

**Name is not a flag.** It is always emitted, so putting it in the set would
make `HasFlag(Name)` trivially true and leave the shape rule unstateable. The
rule is exact: **table shape iff `(cols & ~Columns.Number) != Columns.None`** —
that is, iff any of Size, Modified, Folder or FullPath is on. `Number` alone
stays list shape, which is what makes `1. invoice-2024.pdf` possible.

`Listing.Names` and `ToText(IEnumerable<string>)` are removed. `Options` is
unchanged apart from what it already carries.

**`Folder`** is the directory part of the file's path relative to the longest
root that prefixes it; a file added individually gets `""`. This is what makes
the flattening non-lossy.

**Sorting stays natural-ascending on `Name`, in Core, always.** Descending is
*not* a `Build` option: putting it there would mean flipping the sort order
re-walks the filesystem, contradicting the rule below that only the roots and
the three intake filters trigger a walk. The view model reverses the in-memory
projection instead.

**`ToCsv`** routes every field through `Csv.EscapeField`, which is `internal`
to Core and therefore reachable. That helper carries an Excel
formula-injection guard, and filenames are exactly the values that trip it — a
file named `=cmd.pdf` or `-rf report.pdf` is something Excel will try to
interpret when the exported CSV is opened.

### `src/OrdoSort.Wpf/ViewModels/FilenameListViewModel.cs`

- `_allRows` holds the last `Build` result; `Rows` becomes
  `ObservableCollection<FileRow>` carrying the **visible projection** of it —
  `_allRows` minus the exclusion set, minus the name filter, reversed when
  `Descending`. Copy, Save and the counts line all read the projection, which
  is what keeps "what you see is what you copy" true of the exclusion set and
  the filter as well as of the columns.
- New: `Columns`, `Descending`, `NameFilter`, `SelectedPaths`, the exclusion
  set, `RemoveSelectedCommand`, `RestoreRemovedCommand`, `BrowseFilesCommand`.
- `OutputText` becomes `ToText(Rows, Columns)`; a parallel `OutputCsv` feeds
  Save.
- Column toggles, `NameFilter` and `Descending` re-project **in memory** and
  never re-arm `_listingProbe`. Only the roots, `IncludeSubfolders`,
  `IncludeExtension` and `ExtensionFilter` trigger a real walk.
- Save's dialog filter follows the same rule as Copy: `.txt` in list shape,
  `.csv` in table shape.

### `src/OrdoSort.Wpf/Services/IDialogService.cs`

Additive `string[] AskOpenFiles(string filter)` — real multi-select, matching
what drag-and-drop already accepts. No existing call site changes; the real
implementation and the test fakes gain the method.

### `src/OrdoSort.Wpf/Windows/FilenameListWindow.xaml`

- Grid gains the four optional columns, each bound to a VM visibility flag.
- A **Columns ▾** dropdown of checkable items rather than four more checkboxes:
  the toolbar already carries two buttons and two checkboxes, and four more
  would wrap to a third line at the 480px `MinWidth` the overflow tests pin.
- `SelectionChanged` in the code-behind pushes selected paths to the view
  model — `SelectedItems` is not bindable. `Clipboard` stays in the
  code-behind, per the CLIPBOARD RULE comment already in that file.

## Testing

**Core** — the `Folder` relative-path rule including the individually-added
file; nullable `Size`/`Modified` when a file cannot be read; both `ToText`
shapes and the `#`-as-prefix-vs-column switch; `ToCsv` escaping, with a test
that pins the formula-injection guard on a filename beginning with `=`;
and that a listing with no new columns produces today's exact text.

**View model** — that removed rows stay removed across a rebuild (the defect
the exclusion set exists to prevent, and the one a naive implementation fails);
that column toggles, `NameFilter` and `Descending` do not re-arm the probe;
Copy following selection; the counts line with `removed`; `Restore removed`.

**Windows** — `WindowOverflowTests`' registry seed updated to the widest state
(every column on, a long counts line), so the new toolbar is measured at
`MinWidth`/14px and default width/18px.

**E2E** — the *Filename list* scenario extended with a removal that survives a
rebuild, a column toggle, and a table-shaped copy.

## Docs

`README.md`'s one-line description of *Filename list* gains the columns and the
export.

## Out of scope

Deliberately not in this change, each because it changes existing behaviour and
deserves its own decision:

- Making *Filename list* a tab of a shared window, the way the three zip
  windows became one in v1.2.0.
- Page counts as a column — that is the *PDF page counts* tool's job, and
  duplicating it here is how the zip windows became three near-clones.
- `.xlsx` export. `XlsxTable` is a reader only; writing one is a new component,
  and CSV already opens in Excel.
