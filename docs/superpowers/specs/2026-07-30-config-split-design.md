# Config split — design

**Date:** 2026-07-30
**Status:** Approved by user (walkthrough decisions); sub-project 1 of 4 in the
workflow-refinement program (config split → filing/routing → dashboard → tools polish)

## Context

`config.json` currently holds everything: core paths and filing preferences,
`routes[]`, `watch_folders[]` + `alert_texts[]`, `label_clients[]`, appearance,
sounds, saved passwords, and merge-tool state. The user wants the four
list-shaped sections in their own files, for three reasons at once: sharing
sections between machines on a network share (like the shared
`history.sqlite`), hand-editing tidiness, and different owners/cadence (an
admin maintains destinations and the alert list; stations keep their own
local settings).

## Goal

Split four sections of `config.json` into per-section JSON files, each
individually shareable via a configurable path, with automatic migration,
zero data loss, and multi-machine-safe box-label counters.

## File layout

Four new optional keys in `config.json`, with defaults; relative paths
resolve against `config.json`'s directory (the same rule `names_file` and
`history_db` follow today):

| Key | Default | File content |
|---|---|---|
| `destinations_file` | `destinations.json` | `{"routes": [...]}` |
| `monitored_folders_file` | `monitored-folders.json` | `{"watch_folders": [...]}` |
| `alerts_file` | `alerts.json` | `{"alert_texts": [...]}` |
| `box_labels_file` | `box-labels.json` | `{"label_clients": [...]}` |

Each side file is a JSON object with its one list key. Unknown top-level
keys in a side file survive a load/save round trip (the existing `Extras`
pattern, per file). Serialization matches `config.json`: indented, relaxed
escaping, trailing newline. This makes the files future-proof — e.g.
sub-project 4 adds a `date_style` key to `box-labels.json`, and sub-project 3
adds a `section` key to watch-folder entries, with no format change.

## Load semantics

`Config.Load(path)` produces the same fully-populated `Config` object it
does today. Per section:

1. If the side file exists, its list is the truth. An inline section still
   present in `config.json` is ignored (side file wins).
2. If the side file is missing but `config.json` has the inline section
   (a pre-split config), the inline list is used.
3. If neither exists, the section is empty (current default).

First run (no `config.json`): create `config.json` **and** all four side
files with defaults, so the invariant "side files exist after any save"
holds from the start.

A side file that exists but is unreadable or invalid JSON throws
`ConfigException` naming **that file** — surfaced in the same readable
startup dialog config errors get today. Normalization (null lists → empty,
null entries dropped, per-item null fields defaulted) applies to each side
file's content exactly as it applies inline today.

## Save semantics and file ownership

- **Settings save** writes `config.json` (with the four inline sections
  omitted — this is what completes the migration) plus `destinations.json`,
  `monitored-folders.json`, and `alerts.json` at their configured paths.
  Each file saves through the `TrySave` philosophy: a failure warns, never
  crashes, and names the failing file.
- **`box-labels.json` is never written by Settings.** It is written only by
  the Box labels tool through the exclusive path below — a blind
  settings-style write would clobber counters advanced by another station.
- Every side-file write includes that file's preserved `Extras`.

## Multi-machine label counters (confirmed requirement)

Several stations print labels from one shared `box-labels.json`. All label
mutations (advancing `next_number` when printing/exporting, plus client
add/remove/edit/reset in the Box labels window) go through one atomic
operation in Core:

- Open the file with `FileShare.None` (exclusive), read current content,
  apply the mutation, write, close.
- On sharing violation, retry every 150 ms up to 5 s (the `busy_timeout`
  philosophy), then fail with a readable message ("another station is
  using the box-labels file — try again").
- The tool re-reads the file at window open and before every print, so a
  station always prints from fresh numbers.

## UI: Data files section

The **Tools & data** Settings page gains a **Data files** section (the
page's rename to "Data files" and the saved-passwords move happen in
sub-project 4): four rows — *Destinations*, *Monitored folders*, *Alerts*,
*Box labels* — each a path box + Browse. Behavior:

- Relative or absolute paths accepted; relative shown as typed, resolved
  against the config's folder.
- Live validation note per row: target exists and parses → "N entries";
  target missing → "will be created on save"; target invalid → the parse
  error.
- Changing a path **re-points, it does not move content**: after save, the
  file at the new path (if any) becomes the truth; if none exists there,
  the current in-memory list is written to it on save. The note text states
  this while a changed path is pending.

## Refresh model

Side files are read at app start and re-read when Settings opens (plus the
box-labels re-read at every print). No hot-watching of config files in v1 —
recorded as a future refinement.

## Demo & tooling

`DemoReset` (reset.bat) and the demo-full generator emit split files
directly, and their self-check summaries verify against the split layout.

## Testing

- Core: per-section load/save round trips; side-file-wins-over-inline;
  inline fallback; migration completes on save (inline sections gone, side
  files present); null/junk handling per file; `Extras` round-trip per
  file; `ConfigException` names the failing side file; relative-path
  resolution.
- Core: counter atomicity — two threads incrementing the same client
  through the exclusive API against a real temp file yield distinct,
  gapless numbers; a held exclusive handle makes the second writer retry
  then fail readably after the timeout.
- Wpf (headless): Data files rows validate and save through
  `SettingsViewModel`; Settings save routes sections to their files and
  never touches `box-labels.json`; LabelMaker mutations go through the
  exclusive path.
- Full suites stay green; the 557 baseline grows only by additions.

## Non-goals

- Hot-reloading config files while the app runs.
- Moving/copying file contents when a path key changes.
- Splitting anything else (appearance, sounds, merge state, and
  `saved_passwords` stay in `config.json` — DPAPI values are
  per-Windows-account and cannot be shared).
- The saved-passwords UI move and tab rename (sub-project 4).
- Dashboard `section` field (sub-project 3) and `date_style` (sub-project
  4) — this spec only guarantees the file formats tolerate them.

## Delivery

Feature work directly on `main` (per established user preference), normal
commits per task, pushed after the verification gate: build + full test
suites green, demo generators pass their self-checks.
