# Remove custom-template naming — design

**Date:** 2026-08-01
**Status:** Approved by user (full removal chosen over UI-only hiding).

## Context

The filing/routing sub-project (spec 2026-07-31) shipped five naming modes;
the fifth, `template` (a `{name}/{original}/{date}` pattern engine with a
Custom-template UI on the Filing tab and per-route overrides), is being
removed at the user's direction. Insert, Full replace, Prefix, and Append
remain.

## Goal

Excise the template engine completely — Core, config keys, both UI
surfaces, and tests — with existing configs degrading gracefully.

## Scope

- **Core (`Naming.cs`)**: `Modes` returns to `insert|replace|prefix|append`;
  delete `ModeTemplate`, `ValidateTemplate`, `ResolveTemplate`, the token
  regex/set, and the template rendering arm; `ApplyName` and `BuildTarget`
  drop their `template` and `today` parameters (both existed only for the
  template mode); `Commit.CommitFile` drops `globalTemplate`/`today`;
  `Session`'s template threading and the Shell/Settings preview call sites
  revert. `SkipFile` untouched.
- **Config**: remove `Config.NamingTemplate` and `Route.NamingTemplate`
  (typed members, normalization, and the load-time template validation
  block). **Migration rule**: `Normalize()` maps `naming_mode ==
  "template"` → `"replace"` (global AND per-route) before validation, so
  no existing config can fail to load. Orphaned `naming_template` values
  become unknown keys and survive inertly via the Extras round-trip
  (consistent with the hand-edit guarantee; no active stripping).
- **UI**: Filing tab back to four radios (template textbox, tokens caption,
  `TemplateNote` deleted; `FilingMode`'s `ModeTemplate` wrapper removed);
  Destinations loses the per-route template row + `DataTrigger` +
  `RouteEditVm.NamingTemplate` + the "Custom template" `ModeChoices` entry;
  both `HardErrors` template validation blocks removed; live examples
  simplify (no template branch).
- **Tests**: template-specific tests removed across NamingTests,
  NamingConfigTests, SettingsViewModelTests, PipelineTests; NEW migration
  tests: global `"template"` loads as `"replace"`; a route with
  `naming_mode: "template"` loads as `"replace"`; an orphaned
  `naming_template` key survives as an inert extra. The demo-full
  workbench is checked for any template-mode route (its
  naming-mode-override route moves to a surviving mode if needed).

## Non-goals

- Touching Insert/Replace/Prefix/Append semantics, the pickup rule, or the
  Enter behavior.
- Rewriting historical specs/plans (docs/superpowers is deliberate history).
- Removing the four-mode `naming_mode` machinery (per-route overrides of
  the surviving modes keep working exactly as shipped).

## Verification

Build + both suites green (totals shrink by the removed template tests,
grow by the three migration tests — record exact); smoke `dialogs` +
`demo-full` pass; `git grep -i "ModeTemplate\|naming_template\|ValidateTemplate\|ResolveTemplate"`
over src/ returns nothing (tests may keep the literal `"template"` string
only inside the migration tests).

## Delivery

Directly on `main` (established), commits per task, push after the gate.
