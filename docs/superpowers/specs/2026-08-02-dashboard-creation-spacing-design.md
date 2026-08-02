# Dashboard tab: contextual creation + spacing polish — design

**Date:** 2026-08-02
**Status:** Approved by user (acceptance feedback on the 2026-08-01 rework:
"section and folder creation is confusing" → contextual-creation option
chosen; spacing review of the live-rendered tab → four fixes approved,
bundled here because they touch the same XAML region).

## Context

After the Dashboard tab rework, creation works backwards from the tab's
mental model: "Add" appends a blank-section folder that materializes under
the DEFAULT group (pinned at the top when empty) while the button sits at
the bottom, and there is no visible way to create a section at all —
sections only appear by typing a new name into a folder's Section combo or
renaming a header. Separately, an off-screen render of the shipped tab
against a stress config found four spacing defects.

## 1. Contextual creation

- **Per-header ＋** (every header, including the default group), beside the
  ✎: VM method `AddFolderToSection(WatchSectionVm h)` creates
  `WatchEditVm { Label = "New folder", Section = h.IsDefault ? "" : h.Header }`,
  inserts it into the flat `WatchFolders` right after that group's last
  member (append at the end when the group is empty), and selects it.
- **Bottom buttons become: Add folder · Add section · Remove · ↑ · ↓.**
  "Add folder" (the renamed Add) inherits the selected folder's section and
  inserts immediately after it in flat order; with no selection it appends
  a default-group folder at the end (today's behavior).
- **"Add section"**: VM method `AddSection()` picks a unique name — "New
  section", then "New section 2", "New section 3", … (uniqueness checked
  case-insensitively against existing section keys) — creates one
  `WatchEditVm { Label = "New folder" }` carrying that section, appends it,
  selects it, and puts the new group's header straight into rename mode
  (`IsEditing = true`, `EditText` = the generated name) so the next
  keystrokes name the section. The code-behind focuses the header's edit
  box with the text pre-selected, reusing the ✎ focus pattern. Sections
  keep existing only through folders — no phantom empty sections, no
  schema change.

## 2. Spacing fixes (from the pixel review)

- **Label/Section row rebalance:** the Section ComboBox column shrinks
  180 → 140 and the "Section:" label's left margin 12 → 8, roughly
  doubling the starved Label TextBox (which truncated "Scans in").
- **Chip cap on a row boundary:** the alerts ScrollViewer `MaxHeight`
  64 → 60 (exactly two chip rows — no more sliced third row peeking above
  the add box) and gains `Padding="0,0,6,0"` so the scrollbar no longer
  overlaps the chips' ✕ buttons.
- **Group separation in the list:** the section-header template's root
  gets `Margin="0,6,0,2"` so a header no longer hugs the previous group's
  last row.
- **File-types orphan wrap:** the five type checkboxes' right margins
  14 → 10 so all five fit one row at the default window width.

## Non-goals

- No changes to rename, drag, grouping rules, or the default group's
  behavior. No config schema changes. No dashboard (Ready) changes.
- Remove/↑/↓ semantics unchanged.

## Testing

VM tests: per-header add lands after the group's last member with the
right section (named + default cases); "Add folder" inherits the selected
folder's section and position, and appends a default-group folder when
nothing is selected; `AddSection` generates unique names case-insensitively,
selects the new folder, and leaves its header in edit mode with `EditText`
prefilled. Spacing changes are XAML-only: verified by re-rendering the tab
with the off-screen capture harness and eyeballing the four fixes, plus the
usual suites and smoke dialogs.

## Delivery

Directly on `main` (established), commits per task, push after the full
gate + final review. User's visual acceptance on fresh captures.
