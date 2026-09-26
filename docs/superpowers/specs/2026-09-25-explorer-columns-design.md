# Table rules v2: mimic File Explorer's Details view

Status: waiting on the owner's review (2026-09-25).

## Why

The owner wants every table in OrdoSort to work like the Details view in Windows File Explorer, "as much as possible", and asked for the table rules themselves to be redone to match it. These rules replace the September table rules (Rules 1–5) in full.

Today the tables auto-fit: a shared sizer (`DataGridColumnCap` + `ColumnShares`) keeps recomputing column widths as rows arrive and the window changes, so columns move under the user. Explorer never does that.

## Owner decisions (2026-09-25)

| Question | Answer |
|---|---|
| Which tables | Every table in the app (9 grids) |
| Widths | Columns start at a set width and stay where you put them; sideways scroll when wider than the window |
| Fit | Double-click a divider fits that column; Ctrl + Plus fits all |
| Sort | Click a header to sort, with an arrow |
| Hide/show | Right-click a header |
| Remember | Per window, on this PC |
| Reorder | Drag headers to reorder, remembered |
| Row look | Explorer look: no row lines, no striping |
| Rules | Redo them to mimic Explorer (number columns right-aligned, like Explorer's Size) |
| Typing | Typing jumps to a row; F2 edits |

## The rules

| # | Rule | Explorer behaviour it copies |
|---|---|---|
| 1 | **Fixed widths.** Each column starts at a set pixel width (listed below) and changes only when the user drags it, double-clicks its divider, or uses a fit command. Adding rows, removing rows and resizing the window never move a column. | Details view column widths |
| 2 | **Sideways scroll.** When the columns are wider than the table, a horizontal scrollbar appears. Space to the right of the last column stays empty. | Same |
| 3 | **Fit to content.** Double-clicking the divider on a column's right edge sizes that column to its widest value across every row, including rows scrolled out of view, and never narrower than its header. Ctrl + Plus (main keyboard or keypad) does this for every visible column. | Double-click divider; Ctrl + Plus |
| 4 | **Header menu.** Right-clicking any header shows "Size column to fit" (the clicked column), "Size all columns to fit", a separator, then a checklist of the columns. The first column is always shown and has no untick. | Explorer's header context menu |
| 5 | **Reorder.** Drag a header left or right to move the column. The first column stays first. | Drag headers |
| 6 | **Sort.** Click a header to sort ascending, again for descending. A small arrow at the top centre of the sorted header shows the direction, in theme colours for both Light and Dark. The File list stays unsortable, because its # column *is* the row order. | Sort by column, with the chevron |
| 7 | **Remembered.** Each window remembers its column widths, order, hidden columns and sort. They're saved to `%LOCALAPPDATA%\OrdoSort\table-columns.json` when the window closes. They are not stored in `config.json`, which several stations share. | Explorer remembers view settings |
| 8 | **Row look.** No lines between rows and no alternating shading. Rows get a hover highlight and a full-row selection highlight, with faint dividers between header cells only. Colours come from the Light/Dark theme and keep the 4.5:1 text contrast. | Details view rows |
| 9 | **Alignment.** Text columns are left-aligned. Number columns (sizes and counts: File list's #, Size and Pages; Page counts' Pages) show their values right-aligned, and every header label stays left-aligned. | Explorer's Size column |
| 10 | **Cut-off text.** Text too long for its column ends in "…", and hovering shows the full text in a tooltip. | Tooltip on a truncated name |
| 11 | **Spacing.** Cell padding stays at 12px each side (drawn since today's fix). Rows are about 27px tall. Header labels line up with cell text. | Comfortable (non-compact) spacing |
| 12 | **Keyboard.** Typing letters jumps to the next row whose first column starts with what was typed. Keys typed within one second accumulate, as in Explorer. Arrows, Home/End, Page Up/Down, Shift/Ctrl selection and Ctrl + A work as today. F2 (or Enter, as today) edits Bulk rename's New name, and typing no longer starts an edit. Delete still removes rows where it does today. | Type-to-select; F2 to rename |
| 13 | **Empty space.** Clicking below the last row clears the selection. | Same |

Not copied, and why:

| Explorer feature | Why not |
|---|---|
| Grouping ("Group by") | Nothing here to group by |
| Filter dropdowns on headers | A different feature; ask for it separately if wanted |
| Drag-a-box selection | Shift/Ctrl-click cover it |
| "More…" column chooser | These tables have a fixed set of columns; the header checklist shows them all |
| Compact view toggle | One spacing is enough |

## Starting widths (px)

| Window | Columns |
|---|---|
| Bulk rename | Current name 300 · New name 300 · Note 160 |
| File list | # 50 · File name 320 · Pages 70 · Size 80 · Modified 140 · Folder 180 · Full path 320 |
| History | When 140 · Original 240 · Filed as 240 · Name 140 · Destination 160 · Undone 80 |
| Match & merge | File 280 · Becomes 280 · Note 200 |
| Merge PDFs | Item 360 · Kind 90 · Result 200 |
| Page counts | File 360 · Pages 70 · Note 200 |
| Standardise names | Current name 380 · Result 300 |
| Zip tools | Item 360 · Kind 90 · Result 200 |
| Triage | Each roster column 140 · Why 150 |

## How it's built

**`ExplorerColumns`** is a shared behaviour in `src/OrdoSort.Wpf/Views/`. Each window attaches it to its grid with a window key, for example `ExplorerColumns.Attach(PreviewGrid, "BulkRename")`. It replaces each window's `DataGridColumnCap.Track` call and owns rules 1–5, 7, 12 and 13. Details:

- **Column identity:** columns are identified by header text, so Triage's per-roster columns remember their own widths.
- **Fit:** measures each row's value through the column's own binding (so date formats and roster cells come out right), using the grid's font, plus padding.
- **File list:** its show/hide toggles already decide what gets exported, so its header checklist sets the same view-model flags. The screen and the export can't disagree.
- **Damaged or missing saved file:** the window opens at its defaults. This follows Box Labels' reasoning: a layout preference must never stop a window opening.
- **Failed save:** written to `crash.log`, not shown in a dialog.

**Shared styles (`Theme/Styles.xaml`):**

- rows: no row lines and no striping (rule 8);
- the header gets the sort chevron (rule 6);
- a `GridCellNumber` element style for right-aligned numbers (rule 9);
- column reordering back on (rule 5).

**Removed:** `DataGridColumnCap`, `ColumnShares`, `ShrinkWhenEmpty`, and the September Rules 3 and 5, with their tests. That leaves one way to do the job.

## Tests

| Test | Proves |
|---|---|
| Defaults | Each grid opens at its listed widths when nothing is saved |
| Never auto-resizes | A dragged width survives adding 200 rows and resizing the window |
| Sideways scroll | Columns wider than the grid show a horizontal scrollbar |
| Divider fit | Uses the widest value, including rows scrolled out of view |
| Ctrl + Plus | Every visible column fits |
| Header menu | "Size column to fit", "Size all columns to fit", and hide/show work; the first column can't be hidden |
| Reorder | A moved column stays moved; the first column stays first |
| Sort | Two clicks give ascending then descending; the arrow reaches 3:1 in both themes |
| Remembered | Widths, order, hidden columns and sort saved on close come back in a new window; a damaged file opens at the defaults |
| Row look | No row lines and no striping; hover and selection still reach 4.5:1 text contrast |
| Numbers | Size and Pages values are right-aligned; headers are left-aligned |
| Typing | Letters jump to a matching row; a second letter within 1s refines it; in Bulk rename typing does not start an edit, and F2 does |
| Empty space | A click below the rows clears the selection |
| File list | The header checklist flips the export flag |
| Coverage | Every DataGrid in `src` is attached and every column has a pixel width |

Suites that pin auto-fit are deleted or rewritten:

- `AutoFitColumnTests`, `DataGridColumnCapTests`, `ColumnSharesTests`, `DataGridStarColumnTests`;
- the star checks in `DataGridSizingCoverageTests`;
- History's "When is not capped";
- `TablesDoNotLetColumnsBeReorderedOrRowsResized` (reordering comes back on);
- `PageCountsPagesColumnAlignsLeftNotRight` (rule 9 reverses it).

The trim/tooltip, padding and colour suites stay.

## Commits (each green on check.bat)

1. `ExplorerColumns`: fixed widths, fit, Ctrl + Plus, header menu, reorder, remembered layout and sort, with tests. Attached to Bulk rename only.
2. The other 8 grids: pixel widths, attach, drop the `Track` calls; File list's checklist wired to its view-model flags.
3. Styles: Explorer row look, sort chevron, right-aligned numbers, reordering on.
4. Keyboard and mouse: type-to-jump, F2-only editing in Bulk rename, click empty space to clear the selection.
5. Delete `DataGridColumnCap`, `ColumnShares` and the auto-fit suites; STATUS and README.
