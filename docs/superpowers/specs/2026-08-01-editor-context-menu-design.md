# Editor context menus — design

**Date:** 2026-08-01
**Status:** Approved by user (second QC round: the previous ContextMenu
chrome style could not reach the real menus).

## Context

WPF builds the TextBox/PasswordBox right-click menu from PRIVATE
`ContextMenu`/`MenuItem` subclasses. Implicit styles match exact types
only, so both the earlier `ContextMenu` chrome style and the retemplated
`MenuItem` roles miss the real editor menus entirely — captured evidence:
in dark mode the genuine menu is a stock light plate with near-invisible
pale text and the stock blue hover; even in light mode the blue hover is
off the design language. The app defines no menus of its own, so the
prior fix themed nothing user-visible.

## Change

Hand every text-editing control an explicit menu of REAL types, in
`src/OrdoSort.Wpf/Theme/Styles.xaml`:

- Two `x:Shared="False"` ContextMenu resources placed before the implicit
  TextBox style (each control gets its own instance):
  - `EditorContextMenu`: Cut / Copy / Paste / separator / "Select all",
    each a plain `MenuItem` bound to the matching `ApplicationCommands`
    command. Command routing supplies enable/disable exactly like stock
    (Copy greys without a selection) and auto-fills the Ctrl+X/C/V/A
    gesture text. Sentence-case headers per app copy.
  - `PasswordContextMenu`: Paste only — mirroring the stock PasswordBox
    menu (cut/copy are meaningless there).
- One setter in the implicit TextBox style
  (`ContextMenu` → `EditorContextMenu`) and one in the implicit
  PasswordBox style (`ContextMenu` → `PasswordContextMenu`). Editable
  ComboBoxes inherit through their inner TextBox picking up the implicit
  TextBox style; the two `BasedOn` TextBox styles (ProcessingView,
  SettingsWindow header edit) inherit the setter too.

Because these are real `MenuItem`s inside a real `ContextMenu`, the
already-shipped themed templates apply: dark surface plate, accent hover,
themed separator, subtle gesture text.

## Non-goals

- The WebView2 PDF pane's native menu (unreachable from WPF).
- Spell-check/IME suggestion items: the app never enables SpellCheck
  (verified by grep), so the static menu loses nothing.
- No changes to the Menu bar, MenuItem templates, or the prior ContextMenu
  chrome style (it stays — it now themes these explicit menus' chrome).

## Verification

The real-menu capture harness (job scratch dir) re-captures the actual
TextBox context menu, normal + hover, both themes: themed plate, readable
text, accent hover — no stock light plate, no blue. Both suites green,
smoke dialogs exit 0.

## Delivery

Directly on `main` (established), single implementation commit, push after
the final review.
