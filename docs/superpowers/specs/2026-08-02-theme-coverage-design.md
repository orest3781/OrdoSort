# Theme coverage — design

**Date:** 2026-08-02
**Status:** Fixes for defects found by a measured, app-wide theme audit (user-directed: "go through every element and make sure the theme is applied")

## Context

A full audit — static sweep of every control type used in the app's XAML against
every styled `TargetType`, plus off-screen rendering of the transient states a
window gallery cannot capture — found the theme is applied nearly everywhere.
`ScrollBar`, `ToolTip`, `DataGridRow` selection, all twelve window chromes, and
every previously-fixed surface measured correct in both palettes.

Three genuine gaps remain. `Theme/Styles.xaml` defines **zero** styles for the
`Calendar` family, `ListBoxItem`, `DataGridRow`, or `ScrollViewer`.

## Defects to fix

### 1. DatePicker drop-down calendar — CRITICAL, dark mode unusable

Measured day-number contrast in dark mode: **1.12–1.95:1** (AA needs 4.5).
Rendered proof: the numbers are simply absent to the eye. A user cannot pick a
date in dark mode.

Mechanism: the calendar popup keeps its own hardcoded near-white face, while
dark mode's `SystemColors.ControlTextBrushKey` override turns the day-number
text near-white as well. The header and day-name strip survive **only by
accident** — they are hardcoded literal colours, never theme-bound.

This also corrects the standing comment in `Styles.xaml` claiming the popup
"keeps a readable light face": the face is light, but its text is not readable.

Reachable at: `BulkRenameWindow`'s *received:* field.

### 2. `PrintPreviewWindow`'s `DocumentViewer` toolbar — unthemed stock chrome

A genuine "white island in dark mode". Reachable at: Box labels → Print….

### 3. `ListBoxItem` selection — off-brand

Selection and hover render in generic Windows Aero blue rather than
`Theme.Accent`. Contrast passes (8.50–17.44:1), so this is a consistency
defect, not an accessibility one — but it is stock Windows chrome sitting
inside a themed app, which is exactly what this audit was asked to eliminate.
`DataGridRow` already uses `Theme.Accent` correctly, so the two selection
surfaces currently disagree.

Reachable at: Settings route list and monitored-folder list, `ManageSavedWindow`,
`LabelMakerWindow` client list, `UnlockWindow`.

## Design

Add themed styles to `Theme/Styles.xaml`, following the conventions already
proven in this file:

- **Calendar family** — `Calendar`, `CalendarItem`, `CalendarDayButton`,
  `CalendarButton`. The popup surface becomes `Theme.Surface` with
  `Theme.Border`; day numbers, day-name strip and header text bind to
  `Theme.Text`; adjacent-month and disabled days use `Theme.SubtleText`;
  today and the selected day use `Theme.Accent` / `Theme.AccentText`; hover
  uses `Theme.SurfaceHover`. **Every text colour must be theme-bound, not
  hardcoded** — the current accidental-survival must not be reproduced.
- **`ListBoxItem`** — `IsSelected` and `IsMouseOver` triggers using
  `Theme.Accent` / `Theme.AccentText` and `Theme.SurfaceHover`, matching the
  `DataGridCell` convention so the app's two selection surfaces agree.
- **`DocumentViewer`** — theme its toolbar/background chrome enough to remove
  the white island. If its stock template proves impractical to retemplate
  (it hosts a `ToolBar` whose own chrome is notoriously deep), the acceptable
  fallback is theming the host surfaces so no stock-white region remains, and
  recording precisely what could not be reached and why.

**Contrast-trap warning for the implementer:** this file's implicit `TextBlock`
style pins `Foreground = Theme.Text`, and **a style setter outranks an
inherited value**. Any new item template whose label is an auto-wrapped or bare
`TextBlock` must carry a **local** `Foreground` (the remedy proven for
`ComboBoxItem`); `ControlTemplate.Resources` was measured non-functional for
this trap and must not be used.

## Out of scope

- `DataGridRow` hover affordance (audit found none in either theme; a polish
  item, not a theming gap).
- `ScrollViewer` (transparent host; measured fine).
- Changing any palette value or existing highlight colour.

## Verification

Every fixed surface is proven by rendering, not by reading XAML — the same
method that found these defects:

1. Calendar open, both palettes: day numbers, day names, header, today,
   selected, adjacent-month — each ≥4.5:1, with the dark-mode day number
   (currently 1.12:1) explicitly re-measured.
2. `ListBoxItem` selected and hovered, both palettes: ≥4.5:1 and using
   `Theme.Accent`/`AccentText` (assert the resolved colours equal the palette
   values — "not Aero blue" must be proven, not eyeballed).
3. `PrintPreviewWindow` rendered in dark mode with no stock-white region.
4. Regression tests extend the existing `HighlightContrastTests` pattern —
   asserting **resolved** colours walked from the real visual tree, never
   palette pairs.
5. Full suites green (baseline **694**) plus the new cases.

## Delivery

Directly on `main`, commits per task, pushed after the gate (build + suites +
demo-full self-check + launch sanity).
