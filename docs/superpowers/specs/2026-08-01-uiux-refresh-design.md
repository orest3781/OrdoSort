# UI/UX refresh — design

**Date:** 2026-08-01
**Status:** Approved by user (direction chosen from visual-companion mockups:
"Crisp workbench"; accent strategy "steel + bronze highlight" grounded in the
logo's industrial monochrome identity). Program phase 3 of 4
(cleanup ✅ → workflow refinement ✅ → **UI/UX polish** → distribution/v1.0.0).

## Context

OrdoSort's visual layer is functional but generic: default-ish greys, mixed
margins, square corners, dotted focus rectangles. The theme system is strong —
`ThemePalette` (light + dark token tables), `ThemeManager` (live OS
switching), `Styles.xaml` (DynamicResource-based control styles), and
`ThemeTests` enforcing WCAG AA 4.5:1 on every shipped text pairing. The logo
identity is brushed steel / charcoal with subtle bronze edge highlights — no
brand color.

## Direction (user-selected)

**Crisp workbench**: compact, flat, high-contrast hierarchy. 4px radii,
decisive borders, no shadows, weightier type for primary actions. The app is
a dense professional tool; the refresh sharpens that rather than softening it.

## 1. Palette evolution

- The neutral scale moves from generic grey to a graphite/steel family
  (slightly cooler hue, a touch more contrast between surface levels and
  borders). Both light and dark tables evolve together.
- New accent tokens: **bronze** — target ≈`#8C6D3F` on light surfaces,
  ≈`#C9A96A` on dark. Exact values are tuned during implementation to pass
  `ThemeTests` at 4.5:1 wherever the accent renders text, and ≥3:1 where it
  renders component chrome (focus rectangles, indicators). The tuned values
  are added to the enforced pairing list so they can never regress.
- The accent appears in EXACTLY four roles: focus visuals, the ⏎
  Enter-target badge, selected tab/section indicators, and
  progress/working states. Nothing else. Route button colors and alert red
  are untouched (functional color).

## 2. Control language (Styles.xaml sweep)

- 4px corner radius on buttons, text boxes, combos, list items, tiles,
  chips, cards, toasts.
- Flat surfaces — no drop shadows anywhere.
- Hover: border thickens/darkens (no glow, no background washes beyond the
  existing subtle ones).
- Focus: a visible bronze focus rectangle (2px, 4px radius, 2px offset)
  replaces the dotted default, applied via the shared focus visual style.
- Type: primary action buttons and section headers go SemiBold; body stays
  as configured (the user-configurable app font/size is untouched).
- Spacing rhythm: margins/padding normalize to a 6 / 10 / 16 px scale
  (tight-group / control-gap / section-gap). Existing one-off values map to
  the nearest step. No layout restructuring — same rows, same panels.

## 3. Screen retouches (every window)

Consistency pass across: Ready (dashboard tiles + section headers + inbox
count), Processing (name box prominence, "will be filed as" line, route
buttons, confirmation card), Done summary, all six Settings tabs, Unlock
(incl. banner + Manage saved dialog), Bulk rename, Match & merge (+ Review
matches), Box labels (+ print preview), History, toasts, and the
empty-state screens (existing illustrations kept; framing/copy refreshed).
Dark title bar via the DWM immersive-dark-mode attribute so window chrome
follows the theme.

## 4. Guardrails & verification

- `ThemePalette`/`ThemeTests` remain the contrast authority; every new
  text pairing joins the enforced list.
- Suites + smoke `dialogs` + demo-full + launch sanity gate as usual.
- Pixels can't be judged by tests: delivery includes **before/after
  screenshots per screen** (captured from the running app against the
  demo-full workbench) for the user's visual acceptance pass. `git revert`
  is the rollback path if anything reads wrong on a real monitor.

## Non-goals

- No layout changes (rows, panels, window sizes, keyboard flow untouched).
- No custom-drawn title bar (DWM dark attribute only).
- No new illustrations or iconography.
- No route-color or alert-color changes.
- No new user-facing settings (the refresh is the default look; theme
  auto/light/dark and font settings keep working as-is).

## Delivery

Directly on `main` (established), commits per task, push after the full
gate plus screenshot capture. Final acceptance is the user's visual pass on
the delivered screenshots; fixes from that pass ride as a follow-up commit.
