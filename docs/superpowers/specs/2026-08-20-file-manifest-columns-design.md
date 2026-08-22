# Filename list → file manifest builder — Phase 1 design

Date: 2026-08-20 · Branch base: `main` @ `5ee83be` · Status: **awaiting review**

## What this is

Today the *Filename list* tool shows filenames plus up to five optional columns,
chosen from a five-item `Columns ▾` menu, in a fixed order, with no formatting
choices. This design turns it into a **file manifest builder**: you pick which facts
you want beside each name, in what order, formatted how you want them, save that
choice as a preset, and export the result.

Phase 1 builds the framework and ships every column that costs no new I/O. It is the
phase that decides the shape — Phases 2 and 3 are then largely new entries in a
registry.

## Phase plan

| Phase | Contents | Status |
|---|---|---|
| **1** | Column registry, grouped picker with ordering, per-column formats, presets, sorting, export follows selection, all zero-I/O columns | **this spec** |
| 2 | Per-file readers: PDF (page count, paper size, orientation, encrypted, producer), TIFF/image (page count, dimensions, DPI), zip entry counts, line counts — plus the async probe + cache-by-path + cancel model | later spec |
| 3 | Hashing (SHA-256), exact-duplicate groups, group-and-subtotal by folder, aligned totals row | later spec |

Phase 1 introduces **no new file I/O**. Everything it renders comes from the
`FileInfo` the walk already performs.

## Decisions taken during brainstorming

1. **`Tools → PDF page counts…` survives as a preset launcher** into this window
   (Phase 2, once a Pages column exists). It will open with the Pages column on and
   the type filter pre-set to `pdf`. One window class, two doors. `PageCountsWindow`,
   `PageCountsViewModel` and `PageCountRow` retire in Phase 2; `PageCounts.Count` in
   Core stays untouched.
2. **A per-file probe counts only the visible rows, cached by full path** (Phase 2).
   Recorded here because it constrains the Phase 1 row model: rows must be able to
   carry per-row values that arrive after the row exists.
3. **Real header sorting**, with one sort authority (below, §7).
4. **Size unit choice moves into the export header**: display `4.1 MB`, CSV header
   `Size (MB)`, CSV value `4.1`. `Auto` exports raw bytes under `Size (bytes)`.
5. **The picker is a dropdown panel** hung off a `Columns…` button (§6).

---

## 1 · The column registry

### 1.1 Why the flags enum goes

`FilenameList.Columns` is a `[Flags]` enum of five members, consumed by a fixed
`Layout` array and a `Cell(row, column, index)` switch. It cannot express column
**order** or **per-column format**, and twenty members in a bitfield with a
hand-maintained parallel array is not a structure that survives Phases 2 and 3.

### 1.2 `ColumnDef`

New file **`src/OrdoSort.Core/FileColumns.cs`**:

```csharp
public enum ColumnAlign { Left, Right }
public enum RenderTarget { Display, Export }

/// <summary>One selectable rendering of a column, e.g. Size as MB.
/// Id is persisted in presets and config, so it must stay stable.</summary>
public sealed record ColumnFormat(string Id, string Label);

public sealed record ColumnDef(
    string Id,                                    // "size" — stable, persisted
    string Group,                                 // "File" | "Dates" | "Path"
    string Title,                                 // "Size"
    IReadOnlyList<ColumnFormat> Formats,          // empty = nothing to choose
    Func<ColumnFormat?, string> Header,           // "Size (MB)"
    Func<FileRow, int, ColumnFormat?, RenderTarget, string> Render,
    Func<FileRow, IComparable?>? SortKey,         // typed; null = column not sortable
    ColumnAlign Align);
```

`Render` takes the row's 1-based **position** as well as the row, because `#` is a fact
about the projection rather than about the file — `FileRow` cannot carry it. This is
the same reason today's `Cell(row, column, index)` already takes an index.

`SortKey` is nullable: `#` is the one column with nothing meaningful to sort by (it
*is* the sort order), so its header does not offer sorting.

One `Render` taking a `RenderTarget` rather than two functions: for every column but
Size the two branches are identical, and a second delegate per column would be noise
that must be kept in sync.

`Formats` is a list of `ColumnFormat` rather than a typed enum per column so that the
registry stays one uniform shape and a preset can round-trip through config as two
plain strings. `Render` switches on `format.Id`.

### 1.3 The selection

```csharp
public sealed record ColumnChoice(string ColumnId, string FormatId);
```

The selection is an **ordered `IReadOnlyList<ColumnChoice>`**. Order is the user's, and
it is the order the grid shows and the export writes.

`Name` stops being special-cased. Today it is deliberately excluded from the enum
("Name is NOT a member: it is always emitted"); it becomes an ordinary registry entry
that merely *defaults* to first. This makes "full path only, no filename" expressible.
The picker's only constraint is that the selection may not be empty.

### 1.4 The list-vs-table rule

`IsTable` currently reads `(cols & ~Columns.Number) != Columns.None`. It becomes:

```csharp
public static bool IsTable(IReadOnlyList<ColumnChoice> selection)
{
    var content = selection.Where(c => c.ColumnId is not "number").ToList();
    return content.Count > 1
        || content.Any(c => c.ColumnId is not ("name" or "stem"));
}
```

`number` is excluded from the count for the reason today's rule excludes it: a numbered
list of names is still a list, which is what lets `1. invoice-2024.pdf` exist. `stem`
joins `name` as list-shaped — a column of bare stems is no more a table than a column
of filenames. Everything else makes it a table.

Same meaning as today, same single location, so the Save button's label, the save
dialog's filter and the content shape still cannot disagree.

### 1.5 File layout

`FilenameList.cs` is ~200 lines; twenty column definitions plus format rendering would
clear 500. Split:

- **`FileColumns.cs`** — `ColumnDef`, `ColumnFormat`, `ColumnChoice`, the registry
  (`FileColumns.All`, `FileColumns.ById`), all rendering and formatting helpers.
- **`FilenameList.cs`** — keeps `Build`, `FolderFor`, `Listing`, `FileRow`, and
  `ToText`/`ToCsv`, which now walk the selection and delegate every cell to the
  registry.

Core stays `net8.0` and gains no package reference.

---

## 2 · Columns shipped in Phase 1

Group **File**

| Id | Title | Formats | Notes |
|---|---|---|---|
| `number` | # | — | position in the current sort; right-aligned |
| `name` | File name | — | with extension |
| `stem` | Name without extension | — | retires the global *Include extension* toggle |
| `ext` | Extension | — | lowercase, no dot; blank when none |
| `size` | Size | Auto, Bytes, KB, MB, GB | right-aligned; blank when unreadable |
| `attributes` | Attributes | — | `Read-only, Hidden, System`; blank when none |
| `duplicate` | Duplicate name | — | `×3` when the name occurs 3 times; blank when unique |

Group **Dates**

| Id | Title | Formats | Notes |
|---|---|---|---|
| `modified` | Modified | ISO, Short, Date only | blank when unreadable |
| `created` | Created | ISO, Short, Date only | new — only Modified exists today |

Group **Path**

| Id | Title | Formats | Notes |
|---|---|---|---|
| `folder` | Folder | — | relative to the root it arrived under (existing `FolderFor`) |
| `fullpath` | Full path | — | |
| `depth` | Depth | — | folders below the root; `0` for a file at the root |
| `drive` | Drive | — | `C:\` or `\\server\share\` via `Path.GetPathRoot` |

### 2.1 Size formatting

| Format | Display | CSV header | CSV value |
|---|---|---|---|
| Auto | `4.1 MB` | `Size (bytes)` | `4293904` |
| Bytes | `4293904` | `Size (bytes)` | `4293904` |
| KB | `4193 KB` | `Size (KB)` | `4193` |
| MB | `4.1 MB` | `Size (MB)` | `4.1` |
| GB | `0.004 GB` | `Size (GB)` | `0.004` |

Decimal places: Bytes 0, KB 0, MB 1, GB 3. Divisor is 1024. `Auto` picks the largest
unit whose value is ≥ 1 and renders it with that unit's decimals.

**`Auto` and `MB` can display identically and still export differently.** That is
deliberate and it is the single place in the tool where display and export shapes
diverge: `Auto` has no one unit to name in a header, so its export falls back to raw
bytes. Every other column renders identically for both targets.

A null `Size` (the per-file stat failed — already guarded and non-fatal) renders empty
in both targets, never `0`.

### 2.2 Duplicate name

Computed in `Build` over the whole listing, case-insensitively, on the **full file
name including extension**, and stored on `FileRow` as `NameOccurrences`. Keeping it a
build-time value lets `ColumnDef.Render` stay a pure per-row function rather than
needing the whole set.

Consequence, accepted and documented in the code: duplicates reflect the *built*
listing — the extension filter narrows it, but the Find box and row removals do not.
A duplicate you have filtered out of view is still a duplicate of the row you can see,
which is the semantic a manifest wants.

### 2.3 `FileRow` and the stat seam grow

```csharp
public sealed record FileRow(
    string Name, string Stem, string Extension,
    long? Size, DateTime? Modified, DateTime? Created,
    FileAttributes? Attributes,
    string Folder, string FullPath,
    int Depth, int NameOccurrences);
```

`Build`'s injectable stat delegate changes from
`Func<string,(long Size, DateTime Modified)>?` to `Func<string, FileStat>?` with
`record FileStat(long Size, DateTime Modified, DateTime Created, FileAttributes Attributes)`.
It stays injectable for the same reason it is today: a test must be able to force the
gone/locked/denied failure that is otherwise a race.

`Name` and `Stem` are both stored rather than one derived from the other, because
`stem` is now a column that can be shown *alongside* `name`.

---

## 3 · Row model and selection survival

The WPF layer gains a wrapper:

```csharp
public sealed class FilenameListRow : ObservableObject
{
    public FilenameList.FileRow Row { get; }
    public int Position { get; internal set; }   // 1-based, in current sort order
    // Phase 2 adds: Pages, Note, Pending
}
```

`FilenameListViewModel.Rows` becomes `ObservableCollection<FilenameListRow>`.

Two reasons, both load-bearing:

1. **`#` stops being an `AlternationIndex` trick.** `Position` is assigned during
   `Reproject`, which retires `AlternationCount="{Binding Rows.Count}"` and the
   comment explaining why striping had to be sacrificed for it. Row striping can come
   back.
2. **Phase 2 needs somewhere for a late-arriving page count to land.** `FileRow` is an
   immutable Core record rebuilt on every reproject; an async result written to one
   would be erased by the next keystroke.

**Selection survival (audit FL-03).** Today every `Reproject` calls `Rows.Clear()`,
which fires `SelectionChanged`, empties `SelectedPaths`, and silently turns "copy my 5
selected rows" into "copy all 200". Fix: `Reproject` captures the selected full paths
before the rebuild and raises a `RowsReprojected` event afterwards; the window
re-selects rows whose `FullPath` matches. Identity by path, matching how `_excluded`
already survives rebuilds.

---

## 4 · Presets and remembered state

Two mechanisms, deliberately separate — they answer different questions.

**Presets** capture *which facts you want*: the ordered selection plus each column's
format, plus the sort key and direction. Stored in the existing `Config`, serialized
as a compact string per preset:

```
manifest = name|;size|mb;modified|iso;folder|    sort=size:desc
```

That string is **illustrative**. The exact grammar — separators, escaping of a preset
name containing them, and how a format-less column is written — is pinned by the
implementation plan, not by this line.

Ships with three built-ins, which are read-only and always present:

- **Names only** — `name` (today's default behaviour, so the tool opens unchanged)
- **Sizes and dates** — `name`, `size (Auto)`, `modified (ISO)`
- **Manifest** — `number`, `name`, `size (MB)`, `modified (ISO)`, `folder`

**Last-used state** captures *how you were working*: include-subfolders, the extension
filter, window size and position, and the last selected preset. Restored on open.

Together these close audit FL-07, where nothing a user configures survives closing the
window.

Unknown column or format ids encountered when loading a preset are **skipped, not
fatal** — a config written by a later version must not break an earlier one, and the
same rule protects a preset saved before a column was renamed.

---

## 5 · Dynamic grid columns

Because order is now the user's, `DataGrid.Columns` cannot be a fixed set of
`x:Name`d columns toggled by `Visibility` (today's approach, forced by
`DataGridColumn` being outside both trees). The window **builds `DataGrid.Columns`
programmatically** from the selection whenever it changes: one `DataGridTextColumn`
per `ColumnChoice`, header from `ColumnDef.Header`, cell text from a converter that
calls `ColumnDef.Render(row, format, Display)`, `ElementStyle` based on
`GridCellTextSelectionAware`, right-aligned where `Align` says so, and
`TextTrimming`/`ToolTip` for the columns that can run long (`name`, `stem`, `folder`,
`fullpath`).

This incidentally dissolves the root cause of audit FL-10: with all six columns forced
on, the current fixed set's `MinWidth` floors sum to 630px against ~612px available at
the 640px default. With a user-chosen set you only ever pay for columns you asked for.
The default width still rises to **900** so the shipped *Manifest* preset fits without
a scrollbar.

**Risk:** `DataGridColumnCap.Track` was written against a static column set and is
called by five sibling windows. Its interaction with columns created and destroyed at
runtime is unproven. Phase 1 will attach it after each rebuild and verify; if it
misbehaves, the fallback is per-column `MaxWidth` derived from the viewport, recorded
here so the decision is not rediscovered.

---

## 6 · The picker

A `Columns…` button in the second toolbar row opens a dropdown panel:

```
[Columns… ▾]
┌─ Preset: [ Manifest        ▾ ]  [Save as…]  [Delete] ─┐
│  FILE                    │  In order:                 │
│  [x] File name           │  ≡ #                       │
│  [ ] Name without ext.   │  ≡ File name               │
│  [ ] Extension           │  ≡ Size        [ MB   ▾ ]  │
│  [x] Size      [ MB  ▾ ] │  ≡ Modified    [ ISO  ▾ ]  │
│  [ ] Attributes          │  ≡ Folder                  │
│  [ ] Duplicate name      │                            │
│  DATES                   │  drag ≡ to reorder         │
│  [x] Modified  [ ISO ▾ ] │                            │
│  [ ] Created             │                            │
│  PATH …                  │                            │
└────────────────────────────────────────────────────────┘
```

Left: grouped checkbox list, every column in the registry, with its format dropdown
inline when it has one. Right: the current selection in order, drag-to-reorder. Top:
preset chooser, save, delete. Closes on click-away; every change applies live so the
grid behind it updates as you tick.

**Risk:** drag-to-reorder inside a popup is fiddly in WPF (mouse capture vs. popup
dismissal). If it costs more than it is worth, the fallback is ▲/▼ buttons beside the
ordered list — same capability, less polish. Recorded so the plan can choose without
re-litigating.

The old five-item `Columns ▾` `Menu` is removed, along with the `ShowNumber`/`ShowSize`
/`ShowModified`/`ShowFolder`/`ShowFullPath` adapter properties and the
`SyncColumnVisibility` code-behind that pushed them onto named columns.

---

## 7 · Sorting

Header-click sorting, with **one sort authority**.

`CanUserSortColumns` stays `False`. Letting WPF sort would reorder its own view of
`ItemsSource` underneath `Reproject`, so the on-screen `#` would stop matching what
gets copied or saved — the exact desync the current code comments warn about.

Instead the window handles `DataGrid.Sorting`, sets `e.Handled = true` to cancel WPF's
own sort, sets the view model's `SortColumnId` + `SortDescending`, and assigns
`column.SortDirection` by hand so the arrow glyph appears. `Reproject` orders
`_allRows` using `ColumnDef.SortKey`, **nulls last in both directions** (an unreadable
file's blank size should not lead the list), with `NaturalSort` as the comparer for
string keys and the existing name order as the tiebreak.

`Position` is assigned after sorting, so `#` always numbers what is on screen.

The **Z to A** checkbox is removed. This closes audit FL-11 (headers that look
clickable and are not) and FL-12 (a checkbox standing in for a two-state control).

---

## 8 · Export

`ToText` keeps its existing shape rule: a single `name` column is a plain list;
`number` + `name` is `1. filename`; anything else is tab-separated with a header row.

`ToCsv` always carries a header. Headers come from `ColumnDef.Header(format)`, values
from `Render(row, format, Export)`, **in the user's chosen order**, every field
through `Csv.EscapeField`. The Excel formula-injection guard matters more here than
before, not less: this phase emits considerably more user-controlled text per row.

The `.csv` keeps its UTF-8 BOM and the `.txt` keeps none — unchanged, and for the
reason already documented in `SaveAsync`.

`CountsLine` gains a size total over the **visible** rows when a size column is shown:

```
41 files · 3 removed · 1.2 GB
```

An aligned totals row beneath the columns is deferred to Phase 3 — it carries the same
information for considerably more layout work.

---

## 9 · Audit findings closed by this work

Folded in deliberately, because this phase touches the same code:

| Finding | How |
|---|---|
| FL-03 selection lost on reproject | §3 — re-select by `FullPath` after rebuild |
| FL-07 nothing survives closing | §4 — presets + last-used state |
| FL-09 cosmetic toggle re-walks disk | §2 — `stem` is a column, not a rebuild trigger |
| FL-10 default too narrow | §5 — dynamic columns + 900px default |
| FL-11 dead-looking headers | §7 — headers now sort |
| FL-12 *Z to A* checkbox | §7 — removed |
| FL-30 Size renders raw bytes | §2.1 — the headline feature |

Not closed here and still open: FL-01, FL-02, FL-04, FL-05, FL-06, FL-08, FL-13
through FL-29.

---

## 10 · What breaks

This is the honest cost of the phase, and it is most of the work:

- `FilenameList.Columns` (public, `[Flags]`) is deleted. Every use in Core, the view
  model, the window and the tests changes.
- `FilenameList.FileRow`'s constructor signature changes (§2.3).
- `FilenameList.Options.IncludeExtension` is deleted — `Build` now always stores
  both `Name` and `Stem`, so nothing decides between them at walk time.
- `Build`'s `stat` parameter type changes.
- `FilenameListViewModel.Rows` changes element type; every column binding follows.
- `ShowNumber`/`ShowSize`/`ShowModified`/`ShowFolder`/`ShowFullPath` and
  `IncludeExtension` are deleted from the view model.
- `SyncColumnVisibility` and the named-column XAML are deleted from the window.
- `FilenameListTests`, `FilenameListViewModelTests`, `FilenameListWindowTests`,
  `DataGridSizingCoverageTests` and `WindowOverflowTests` all need rework.

The new columns themselves are cheap once the registry exists. That is the trade: pay
the refactor once in Phase 1 so Phases 2 and 3 are largely new `ColumnDef` entries.

---

## 11 · Testing

**Core (`FileColumns`, `FilenameList`)**

- Every registry column renders without throwing for a row whose `Size`, `Modified`,
  `Created` and `Attributes` are all null.
- Size: each format's display string, CSV header and CSV value, per the §2.1 table —
  including that `Auto` and `MB` can display alike and export differently.
- Date formats round-trip; a null date is empty, never a default `DateTime`.
- `Duplicate name` counts case-insensitively on the full name, across folders.
- `Depth` and `Drive` for a root-level file, a nested file, an individually added
  file, and a UNC path.
- Selection order is preserved into both `ToText` and `ToCsv`.
- `IsTable` for: `name` alone; `number`+`name`; `name`+`size`; `fullpath` alone.
- `Csv.EscapeField` still neutralises a filename beginning `=`.
- Preset round-trip through the config string, including an unknown column id and an
  unknown format id, both skipped rather than fatal.

**WPF**

- Ticking a column adds a grid column in the right position; unticking removes it.
- Reordering changes both grid order and export order.
- Sorting by each column orders correctly, nulls last in both directions, and `#`
  renumbers to match.
- Selection survives a Find keystroke, a column toggle and a sort change (FL-03).
- The picker refuses to leave the selection empty.
- Presets apply and persist; last-used state restores on reopen.
- Existing overflow and sizing coverage, reworked for dynamic columns.

**The trap this repo keeps setting.** Every asynchronous assertion must wait on a
**false→true transition**, never on a state the code has already reached: `WaitFor` and
`E2EPump.Until` both evaluate their predicate before their first sleep, and three
tests on the previous Filename list branch passed against a wrong implementation for
exactly that reason. Prove each one by substituting the wrong implementation and
watching it fail.

---

## 12 · Out of scope for Phase 1

No new file I/O of any kind. Specifically not in this phase: PDF page counts, PDF
paper size / orientation / encryption / producer, TIFF page counts, image dimensions
and DPI, zip entry counts, line counts, hashing, exact-duplicate detection,
group-and-subtotal by folder, and the aligned totals row.

Also deliberately not absorbed: **templated per-file output** (`{n}. {name}`). That is
what `ListReformat.cs` already exists to do, and a second copy of it here would be two
implementations of one idea.

---

## 13 · Open risks

1. `DataGridColumnCap.Track` against runtime-built columns (§5) — unproven; fallback
   recorded.
2. Drag-to-reorder inside a popup (§6) — fallback to ▲/▼ recorded.
3. Test churn (§10) is large enough that the implementation plan should sequence the
   registry and its Core tests *before* any UI work, so the UI is built against a
   proven contract.
