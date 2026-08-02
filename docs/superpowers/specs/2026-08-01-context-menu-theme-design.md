# Context-menu theming — design

**Date:** 2026-08-01
**Status:** Approved by user (QC report: "right click menus are too light").

## Context

The app defines no ContextMenus of its own; every right-click menu is WPF's
built-in text-editing menu (Cut/Copy/Paste on TextBoxes, PasswordBoxes,
editable ComboBoxes — every window). The UI/UX refresh retemplated the menu
BAR (all four MenuItem roles + the menu-key Separator), so items inside any
menu are themed — but `ContextMenu` itself was never styled, so its stock
light popup chrome shows through: a bright plate in dark mode, off-palette
in light mode.

## Change

One addition to `src/OrdoSort.Wpf/Theme/Styles.xaml`, beside the existing
menu templates: an implicit `ContextMenu` style (with
`OverridesDefaultStyle`) whose template mirrors the themed menu-popup
chrome exactly — `Theme.Surface` background, 1px `Theme.Border`, 4px
CornerRadius, 4px Padding, flat, `Theme.Text` foreground. NO popup-edge
margin (unlike the Menu templates, which own their Popup and its
transparency, ContextMenu creates its own popup — a margin there risks an
opaque dead strip). Items keep the existing implicit MenuItem templates
untouched.

## Non-goals

- The PDF pane's right-click menu is WebView2's native Chromium menu — not
  themable from WPF; out of scope.
- No changes to the menu bar, MenuItem templates, or any other style.

## Verification

The off-screen capture harness (job scratch dir) gains a mode that opens a
populated ContextMenu (Cut/Copy/Paste-shaped items + a separator) against
the booted app resources and renders it in both themes; the light chrome
must be gone, chrome must match the menu-bar dropdowns. Plus: both suites
green, smoke dialogs exit 0.

## Delivery

Directly on `main` (established), single implementation commit, push after
the final review.
