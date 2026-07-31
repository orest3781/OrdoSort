# Dashboard refinement — design

**Date:** 2026-07-31
**Status:** Approved by user (walkthrough decisions, 2026-07-30); sub-project 3 of 4
in the workflow-refinement program (config split ✅ → filing/routing ✅ → **dashboard** → tools polish)

## Context

The Ready dashboard renders one flat tile grid under a single heading
(`MonitorTitle`, default "Monitored folders"): `ShellViewModel.Tiles` feeds one
`ItemsControl`/`WrapPanel` in `ReadyView.xaml`. Alert terms are edited as a
one-per-line multiline textbox (`AlertTextsText` joined/split in
`SettingsViewModel`). The user wants monitored folders organized into multiple
named sections, and the alert list edited as chips.

## Goal

Monitored folders grouped into named dashboard sections (defined by a simple
per-folder field), and an alert-term chip editor — with no change to alert
matching, tile behavior, flashing, or the global visibility dropdown.

## Sections

### Model

- `WatchFolder` gains `section` (string, default `""`), stored in
  `monitored-folders.json` like every other folder field.
- A folder's **effective section** is its `section` value, or the existing
  `MonitorTitle` ("Section heading" in Settings) when blank — so current
  configs render exactly as today: one group, same heading.
- Sections render **in order of first appearance** in the folder list. No
  separate section registry, no reorder UI (deliberate walkthrough decision).

### Rendering

- `ShellViewModel` replaces the flat `Tiles` collection with
  `TileGroups: ObservableCollection<TileGroupViewModel>`, where a group is
  `(string Title, ObservableCollection<TileViewModel> Tiles)`. The rebuild
  path (poll refresh) groups statuses by effective section; the flash tick
  walks all groups' tiles.
- `ReadyView.xaml`: an outer `ItemsControl` over `TileGroups`, each item a
  heading (same style as today's single heading) + the existing tile
  `WrapPanel` template.
- **Global visibility unchanged** (Active only / All / Hidden via the
  existing header dropdown). Under *Active only*, a section whose tiles are
  all inactive shows no heading (the group is omitted); under *All*, every
  section and tile shows; under *Hidden*, nothing (sweep skipped), as today.
- The dashboard's compact self-sizing behavior is preserved — groups stack
  vertically in the same panel the single grid occupied.

### Settings (Dashboard tab)

- The per-folder editor gains a **Section** row: an editable ComboBox whose
  dropdown lists the distinct section names already used by other folders
  (pick-or-type autocomplete), blank = default section. `WatchVm` gains a
  `Section` property; the existing tile Preview is unaffected.
- The existing "Section heading" field keeps its meaning (the DEFAULT
  section's heading) — its note text updates to say so:
  `Folders without a section land under this heading.`

## Alert chips

- `SettingsViewModel` replaces `AlertTextsText` (multiline string) with:
  - `AlertTerms: ObservableCollection<string>` — the chips.
  - `NewAlertText: string` + `AddAlertCommand` — trims, ignores blank,
    case-insensitive dedupe (adding "urgent" when "URGENT" exists is a
    no-op that still clears the box), appends, clears the box.
  - `RemoveAlertCommand(term)` — removes that chip.
  - Build writes `cfg.AlertTexts = AlertTerms.ToList()`; from-config seeds
    the collection. `alerts.json` format unchanged.
- XAML: the multiline TextBox becomes a chip flow — an `ItemsControl` with
  a `WrapPanel` of chip borders (term text + an × button, themed like the
  existing tile/button styles) followed by an entry row: single-line
  TextBox (Enter key bound to `AddAlertCommand`) + an `Add` button.
- The caption keeps its content ("Filenames containing any of these terms
  flash the tile and the inbox count red. Matching ignores case and looks
  at filenames only:") minus the now-wrong "(one per line)".
- The tile preview recompute that `AlertTextsText` triggered now triggers
  on collection changes.

## Demo

The demo-full generator assigns sections to its three monitored folders
(two sections, e.g. "Incoming" and "Failed queues") so the grouping is
visible out of the box; its self-check counts are unaffected (they count
folders, not sections). The small reset.bat demo keeps its single
unsectioned folder — proving the default-section fallback.

## Non-goals

- Per-section visibility or collapse (global dropdown stays authoritative —
  explicit walkthrough decision).
- Section reordering/rename UI beyond editing folder fields.
- Any change to alert MATCHING semantics, flash behavior, sounds, or toasts.
- Migrating `monitor_title` (it keeps its key and role as default heading).

## Testing

- Core: `WatchFolder.section` round-trips through `monitored-folders.json`
  (typed property, split save path); omitted section loads as `""`.
- Wpf (headless): groups build in first-appearance order; blank sections
  fall under the `MonitorTitle` heading; a mixed config (two sections +
  one blank) yields three groups titled correctly; active-only omits a
  group whose folders are empty while keeping its populated sibling; the
  flash tick reaches tiles in every group.
- Wpf (Settings): chips — seed from config, add (trim/dedupe case-insensitively/
  clear box), remove, round-trip to `AlertTexts` preserving order; Section
  field round-trips per folder; the autocomplete list contains the distinct
  sections of the OTHER folders.
- Baseline 629 (Core 350 + Wpf 279) grows only by additions, plus the
  sanctioned rewrites of tests that touched `Tiles`/`AlertTextsText`
  directly.

## Delivery

Directly on `main` (established), commits per task, push after the full
gate (build + suites + demo-full self-check + launch sanity).
