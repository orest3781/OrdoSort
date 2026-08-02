# Editable ComboBox — design

**Date:** 2026-08-02
**Status:** Fix for a verified open defect (found 2026-08-01, user informed)

## Context

The Dashboard's Section field (`SettingsWindow.xaml:636`) is declared
`IsEditable="True"` so a user can **pick an existing section or type a new
one**. It does not work: the app's implicit `ComboBox` template
(`Theme/Styles.xaml:549-599`) contains no `PART_EditableTextBox` — only a
full-width `ToggleButton` and a `ContentPresenter` with
`IsHitTestVisible="False"`. WPF's `ComboBox` wires editing to a template part
of exactly that name, so with none present the control is **pick-only**:
there is no caret, and a section that doesn't exist yet cannot be typed.

Verified still present on 2026-08-02 (grep: no `PART_EditableTextBox`;
one consumer, the Section combo). This is the likely root of the user's
"section and folder creation is confusing" report — the contextual-creation
work addressed adjacent friction but not this.

## Goal

Make `IsEditable="True"` genuinely work in the themed ComboBox: typing a new
section name is possible, the drop-down still picks existing ones, and the
control keeps its themed appearance and behavior in both palettes.

## Design

### 1. Template parts

In the `ComboBox` `ControlTemplate`:

- Name the existing `ContentPresenter` `ContentSite` (it stays the
  non-editable display path, unchanged).
- Add a sibling `TextBox x:Name="PART_EditableTextBox"`, `Visibility="Collapsed"`
  by default, `VerticalAlignment="Center"`, `IsReadOnly` template-bound to the
  ComboBox's `IsReadOnly`.
- A `ControlTemplate.Trigger` on `IsEditable = True` sets
  `PART_EditableTextBox` visible and `ContentSite` collapsed.

The `ToggleButton` keeps its full-width chrome (it draws the border and the
arrow). The text box sits on top of it with a **right margin clear of the
arrow** (`8,0,28,0`), so clicking the arrow region still opens the drop-down
while clicking the text places a caret. The toggle stays `Focusable="False"`.

### 2. Keep the implicit TextBox style — override only chrome

The template's text box must NOT use `Style="{x:Null}"`. The app's implicit
`TextBox` style (`Styles.xaml:150-158`) supplies three things worth keeping
inside a combo: the themed **`EditorContextMenu`** (right-click Cut/Copy/
Paste/Select all — the subject of two prior QC rounds), `CaretBrush`, and
`SelectionBrush`.

Its chrome would otherwise double up, so the template sets **local values**,
which outrank style setters in WPF precedence:

| Property | Local value | Why |
|---|---|---|
| `Background` | `Transparent` | the ToggleButton border already paints the surface |
| `BorderThickness` | `0` | avoids a second border inside the combo's border |
| `Padding` | `0` | the template's margin does the spacing |
| `MinHeight` | `0` | prevents the style's chrome height forcing the combo taller |

`Foreground`, `CaretBrush`, `SelectionBrush` and `ContextMenu` are left to the
implicit style so both palettes and the themed menu apply for free.

### 3. `IsTextSearchEnabled` at the call site

The Section combo carries `IsTextSearchEnabled="False"`. Its recorded
rationale is stale (it was proven against a *default-templated* combo), but
the setting is **kept and re-justified**: with a real editable text box now
present, WPF text-search would auto-complete a typed prefix to an existing
section, clobbering a new name like "Incoming 2" the moment it matches
"Incoming". Pick-or-type requires it off. The spec records this so the
attribute is not later removed as cargo cult.

## Verification

Behavior is not reachable from the headless view-model suite, so it is proven
with the project's established off-screen WPF harness pattern (as used for the
radio-group, context-menu and combo investigations):

1. `PART_EditableTextBox` is present in the applied template and is a
   `TextBox` (the exact check that failed on 2026-08-01).
2. Typing into it updates the bound `Section` property (simulated input, then
   read the view model).
3. Clicking the arrow still opens the drop-down; picking an item sets the text.
4. Non-editable combos elsewhere are unchanged (`ContentSite` path still used
   — assert one renders its selection).
5. Both palettes render the editable state with readable contrast.

The harness is scratch, deleted after use; its results go in the report.

## Non-goals

- Making any other ComboBox editable (only the Section field declares it).
- Changing section semantics, grouping, or the creation buttons.
- Restyling the drop-down list, the arrow, or the DatePicker's own combo.

## Delivery

Directly on `main` (established), one commit, pushed after the gate: build +
full suites green (baseline **686** — Core 359 + Wpf 327) + harness evidence.
