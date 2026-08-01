# Settings Dashboard tab rework — design

**Date:** 2026-08-01
**Status:** Approved by user (direction chosen from visual-companion mockups:
one-tab compaction "B", plus grouped-list section manager "A").

## Context

The Dashboard tab's folder detail form (~500px of stacked fields plus the
live tile preview) shares the tab with a ~300px fixed-height Alerts footer.
At the default 880×820 window the form clips behind its ScrollViewer —
"half of it is hidden" — and every alert chip added steals more height.
Separately, sections exist only as text typed per folder: there is no
overview, renaming a section means editing every folder, and the blank
default heading is a detached textbox at the top of the tab.

## Goal

Everything on the tab visible at the default window size, with the folder
list doubling as a real section manager. No behavior changes to alerts,
polling, tiles, or config schema — sections stay plain strings on folders.

## 1. Tab structure

Three bands in one tab (the current top "Section heading:" row + its note
are DELETED — that setting moves into the list, §2):

- **Band 1 (flexible height):** the folder editor — grouped list left
  (230px), compacted detail form right.
- **Band 2 (fixed):** a two-column footer — Alerts left, polling right —
  at roughly half the current footer height.

Nothing scrolls at 880×820. The detail pane keeps its ScrollViewer only as
a safety net for the 560px MinHeight.

## 2. Grouped list = section manager

- The folder ListBox groups under section headers, in dashboard order:
  first-seen over flat folder order, trimmed + case-insensitive keys,
  first-seen casing wins — the same rules `TileGroups` uses on Ready.
  Blank sections form the default group, headed by `MonitorTitle`.
- The default group is ALWAYS shown in this list, even with no members
  (rendered empty, where its first member sits — or pinned first when
  empty): it stays the edit surface for `MonitorTitle` and a drop target
  for clearing a folder's section. (Ready's dashboard is unchanged — it
  still renders no empty groups.)
- **Header rename (✎ per header → inline TextBox):** renames the `Section`
  of every folder whose trimmed section matches the group key
  case-insensitively; the new value is applied verbatim (trimmed).
  Renaming onto another existing section's name merges the groups.
  Renaming to blank moves the group's folders into the default group.
  Editing the DEFAULT group's header edits `MonitorTitle` itself (same
  config key as today, better placed).
- **Drag between groups:** dropping a folder inside another group assigns
  that group's section (default group ⇒ clears Section) and places it at
  the drop position in the flat order. Within-group drag and the ↑/↓
  buttons keep today's flat-order reordering semantics.
- The per-folder Section pick-or-type ComboBox in the detail form STAYS
  (with its load-bearing `IsTextSearchEnabled="False"`); typing a new
  section there still creates a group live.

## 3. Detail form compaction

- Label + Section share one row (two field columns).
- Tile color textbox + swatches merge onto one row.
- Folder path row, file-types block, "include subfolders", the Problem/
  "Create it" row, and the live tile preview keep their current layouts.
- Target height ≈330px (from ≈500px).

## 4. Footer

- **Left — Alerts:** section header; explainer trimmed to one line; the
  chips WrapPanel capped at about two chip rows with its own vertical
  scrollbar beyond that (chips can no longer squeeze Band 1); add-box +
  Add button; flash checkbox. All alert behavior (comma/newline splitting,
  dedupe, removal) unchanged.
- **Right — polling:** "Check folders every [n] sec" with the 5/15/30/60
  preset chips and a one-line caption. Behavior unchanged.

## Non-goals

- No config schema changes (`section` stays a per-folder string;
  `monitor_title` unchanged; monitored-folders.json format untouched).
- No dashboard (Ready screen) changes — TileGroups already renders what
  this tab edits.
- No changes to alert matching, polling cadence, tile preview computation,
  or any other Settings tab.
- No new window sizes.

## Testing

VM-level (SettingsViewModel): grouped projection order (first-seen,
case-insensitive, default group last-or-in-place per flat order); rename
applies to all matching folders and only those; rename-to-existing merges;
rename-to-blank moves to default; default-header edit round-trips to
`MonitorTitle`; drag-assign sets/clears Section; `SectionChoices` and tile
preview unchanged. Existing dashboard/alert tests keep passing. Smoke
dialogs + demo-full + screenshots for the visual acceptance pass.

## Delivery

Directly on `main` (established), commits per task, push after the full
gate. Final acceptance: user's visual pass on before/after screenshots.
