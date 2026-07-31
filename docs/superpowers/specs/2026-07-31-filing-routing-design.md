# Filing & routing refinement — design

**Date:** 2026-07-31
**Status:** Approved by user (walkthrough decisions, 2026-07-30); sub-project 2 of 4
in the workflow-refinement program (config split ✅ → **filing/routing** → dashboard → tools polish)

## Context

Filing has exactly two naming modes — *Insert at the `--`* and *Full replace* —
implemented in `Naming.ApplyName` with per-route overrides via
`Naming.ResolveMode`, and a mode-dependent pickup rule in `Scanner.Eligible`.
The user wants Prefix, Append, date stamping, and a custom template — with the
template system as the engine and everything else a preset of it. Separately,
the *Enter files to the last-used destination* checkbox has a dead unchecked
state (Enter does nothing); the user wants unchecked to mean "Enter files to
the first destination," presented as an explicit choice.

## Goal

One naming engine with five user-facing choices (Insert at `--`, Full
replace, Prefix, Append, Custom template), available globally and per-route,
with live previews everywhere naming is chosen — and an Enter key that always
files somewhere explicit.

## Naming modes

### Modes and semantics

`Naming.Modes` grows to `insert | replace | prefix | append | template`.
Given typed name `SMITH JOHN` and original `20240115--1042.pdf`:

| Mode | Config value | Result stem | Notes |
|---|---|---|---|
| Insert at `--` | `insert` | `20240115-SMITH JOHN-1042` | unchanged; splices at the FIRST `--` |
| Full replace | `replace` | `SMITH JOHN` | unchanged |
| Prefix | `prefix` | `SMITH JOHN-20240115--1042` | typed name + `-` + original stem |
| Append | `append` | `20240115--1042-SMITH JOHN` | original stem + `-` + typed name |
| Custom template | `template` | rendered pattern | see grammar |

A blank typed name preserves the original stem in **every** mode (today's
rule, extended). Prefix/append/template never require a `--` marker.

### Template grammar

A template is literal text plus tokens:

- `{name}` — the typed name (after the existing uppercase/word-separator
  polishing; it arrives at the engine already polished, as today).
- `{original}` — the original filename stem (no extension).
- `{date}` — today's date as `yyyyMMdd` (matches the document ecosystem's
  `20240115--1042` convention). The date is injected (`Func<DateTime>` /
  value parameter) so tests fix the clock; no other formats in v1.

Validation (at settings time AND at load): a `template`-mode config must have
a non-empty template containing at least one token; any `{...}` that is not
one of the three tokens is rejected with a readable error naming the bad
token; a stray unmatched `{` or `}` is rejected. Rendered output flows
through the existing `RejectIllegal` guard at commit time like every other
mode, and the live "will be filed as" preview flags illegal results before
commit, as today.

### Engine changes (Core)

- `Config` gains `naming_template` (string, default `""`); `Route` gains
  `naming_template` (nullable) beside its existing `naming_mode` override.
  Both round-trip. Validation placement: the GLOBAL mode+template are
  validated at load (as `naming_mode` is today) and at settings time;
  per-route templates are validated at settings time and again at commit
  time (readable error, file stays put — the existing route-override
  pattern), never at load, so a hand-edited route can't brick startup.
- `Naming.ApplyName` handles the three new modes; template rendering takes
  the template string and the injected date. `Naming.BuildTarget` threads
  the new inputs through (template + today); `ResolveMode` resolution rule:
  a route override wins for BOTH mode and template (a route with
  `naming_mode: "template"` uses its own `naming_template`, falling back to
  the global template only if its own is absent).
- `Scanner.Eligible` pickup rule becomes: `insert` requires the `--` marker
  (it splices into it); **every other mode picks up every PDF**. Pickup is
  decided by the global session mode, as today — per-route overrides apply
  at commit time only.

## Enter behavior

- The Filing page's *Enter files to the last-used destination* checkbox
  becomes a radio pair: **Enter files to: ◉ last-used destination /
  ○ first destination**. The config key stays `enter_commits` (bool):
  `true` = last-used, `false` = first destination.
- **Enter always files** (when routes exist and a session is on Processing):
  in first-destination mode the target is route 0; in last-used mode the
  target is the last-used route, and **before any route has been used this
  session, route 0 stands in**. The old "press a route button first" status
  hint is retired.
- The route buttons' Enter-target marker follows:
  `enterTarget = EnterCommits ? (_lastRoute ?? 0) : 0` (no marker when there
  are no routes). `ShellViewModel.OnEnterAsync` and `MarkRouteState` are the
  touch points.

## UI

### Settings → Filing

- The two radio buttons become five, each keeping the worked before→after
  example style (`20240115--12345.pdf → …`). Custom template shows a
  template textbox (enabled only in that mode) with the same live example
  treatment.
- The existing live `FilingExample` box renders whichever mode+template is
  selected, combining it with UPPERCASE and the word separator, as today.
- The Typing section swaps the checkbox for the Enter radio pair.

### Settings → Destinations (per-route override)

- The *Naming mode* override combo grows the new choices (blank = inherit
  global, as today). Choosing *Custom template* reveals a per-route template
  box. The route Preview reflects the effective mode.

## Non-goals

- `{date:format}` custom date formats, `{counter}` tokens, or conditional
  template syntax.
- Changing pickup to per-route granularity.
- A third "Enter does nothing" option (explicitly declined during design).
- Touching the collision counter, suffix, or `RejectIllegal` behavior.

## Testing

- Core (`Naming`): each new mode's stem construction; blank-name rule per
  mode; template rendering of all three tokens with a fixed date; unknown
  token, empty template, and unmatched-brace validation errors; per-route
  template override resolution (route template wins; falls back to global);
  `BuildTarget` end-to-end for a template with suffix + collision.
- Core (`Scanner`): pickup — insert restricts to `--`, each other mode picks
  up every PDF.
- Core (`Config`): new keys round-trip; a `template` mode with an invalid
  template fails load with a readable error naming the problem.
- Wpf (headless): Enter behavior — first-destination mode files to route 0;
  last-used mode before any use files to route 0; after using route 2, Enter
  refiles to route 2; marker state matches in all three cases. Settings
  round-trip of the five modes + template + enter radio through the VM.
- Full suites stay green; baseline 589 (Core 321 + Wpf 268) grows only by
  additions.

## Delivery

Feature work directly on `main` (established user preference), commits per
task, push after the full gate (build + suites + demo-full self-check).
