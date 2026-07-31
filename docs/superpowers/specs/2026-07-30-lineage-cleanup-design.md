# Lineage cleanup — design

**Date:** 2026-07-30
**Status:** Approved by user (approach A: full neutralization; implemented directly on `main`)

## Context

The 2026-07-30 rebrand made the committed tree free of the banned name tokens
(FileRouter / Sendu / Paper Trail), but the final whole-branch review found
softer residue the token sweep could not see: temp-path prefixes derived from
"FileRouter" (`fr…_`), a `.gitignore` comment telling a story about the old
repo's history, and roughly twenty code comments that reference the app's
predecessors ("the Python original", "the WinForms app"). This is the first
sub-project of the broader refinement program (cleanup → workflow → UI/UX →
distribution).

## Goal

Complete the clean-break identity at the code level: no reference to
predecessor applications anywhere in the committed tree (outside
`docs/superpowers/`, which records history deliberately), with **zero
behavior change** — comments and constant strings only.

## Scope

### 1. Temp-path prefixes (13 sites)

Mechanical map: leading `fr` → `ordo`, remainder unchanged.

| File | Old → New |
|---|---|
| `tools/OrdoSort.Smoke/Program.cs:37` | `fr_smoke_` → `ordo_smoke_` |
| `tools/OrdoSort.Smoke/Reentrancy.cs:29` | `fr_reentry_` → `ordo_reentry_` |
| `tests/OrdoSort.Wpf.Tests/ToolViewModelTests.cs:10` | `frunlock_` → `ordounlock_` |
| `tests/OrdoSort.Wpf.Tests/ToolViewModelTests.cs:267` | `frbulk_` → `ordobulk_` |
| `tests/OrdoSort.Wpf.Tests/ToolViewModelTests.cs:463` | `frmm_` → `ordomm_` |
| `tests/OrdoSort.Wpf.Tests/ShellFixture.cs:26` | `frshell_` → `ordoshell_` |
| `tests/OrdoSort.Wpf.Tests/SettingsViewModelTests.cs:10` | `frset_` → `ordoset_` |
| `tests/OrdoSort.Wpf.Tests/HistoryViewModelTests.cs:8` | `frhist_` → `ordohist_` |
| `tests/OrdoSort.Wpf.Tests/FolderWatchServiceTests.cs:7` | `frwatch_` → `ordowatch_` |
| `tests/OrdoSort.Core.Tests/ConfigNullKeysTests.cs:10` | `frnull_` → `ordonull_` |
| `tests/OrdoSort.Core.Tests/ConfigNewKeysTests.cs:8` | `frnk_` → `ordonk_` |
| `tests/OrdoSort.Core.Tests/ConfigHardeningTests.cs:9` | `frcfg_` → `ordocfg_` |
| `tests/OrdoSort.Core.Tests/AuditFailureTests.cs:11` | `fraudit_` → `ordoaudit_` |

These are temp-directory name prefixes used by tests and smoke tools; the
only requirements are uniqueness and greppability as app-owned. Line numbers
are as of commit `2dbf941` and are advisory; match by string.

### 2. `.gitignore` `/sounds/` stanza

Keep the anchoring rationale, drop the old-repo anecdote. Replace the four
comment lines above `/sounds/` with:

```
# Scratch audio: source material for the synthesized alert sounds. The ones
# the app actually ships live in src/OrdoSort.Wpf/Assets/sounds/.
# Anchored: a bare "sounds/" would match a folder of that name at ANY depth —
# including the shipped assets folder named in the line above.
```

The `/sounds/` pattern line itself is unchanged.

### 3. Lineage comments (~20 sites)

Rewrite rule: **keep the documented constraint, drop the predecessor
reference.** The constraint each comment protects must survive in
present-tense form. Worked examples of the rule:

- `// Python parity: hand-edited per-route keys survive a load/save round trip`
  → `// Hand-edited per-route keys survive a load/save round trip`
- `// Appearance (Python parity: same key names, so an old config round-trips)`
  → `// Appearance (key names are stable, so existing configs round-trip)`
- `viewer gestures the Python app's users have in their fingers`
  → `viewer gestures users expect from PDF viewers`
- `the same deliberate trade the WinForms app made`
  → delete the clause; the sentence already states the trade.

Known sites (line numbers as of commit `2dbf941`, advisory): `Config.cs`
(3), `App.xaml.cs`, `MainWindow.xaml.cs`, `FolderWatchService.cs`,
`HotkeyParser.cs`, `PasswordVault.cs`, `ViewerInput.cs`,
`WebViewPdfViewer.cs`, `ThemeManager.cs`, `ThemePalette.cs` (2),
`BulkRenameViewModel.cs`, `SettingsViewModel.cs:329`,
`ShellViewModel.cs` (3), `TileViewModel.cs`. The definitive list is derived
at implementation time by a case-insensitive sweep for
`python|winforms|ported|original` over `src/ tests/ tools/`, judging each
hit against the rule.

**Guard:** "parity" between two *current* UI surfaces is not lineage and
stays — e.g. `SettingsViewModel.cs:201` ("parity with the route detail
panel"). Only references to predecessor applications are in scope.

**Note on PasswordVault:** its comment says "legacy plaintext values still
[load]". The plaintext-tolerant *behavior* is kept (behavior changes are out
of scope); the rewritten comment describes it as accepting hand-edited
plaintext values, without attributing them to a predecessor app.

## Non-goals

- Any behavior, API, or test-logic change. Strings that affect runtime
  behavior beyond temp-dir naming are untouched.
- Renaming config keys (their stability is a documented feature).
- Touching `docs/superpowers/` (deliberate history).

## Verification gate

1. `dotnet build OrdoSort.sln` succeeds.
2. `dotnet test OrdoSort.sln` — exactly 557 passed (301 Core + 256 Wpf).
3. `git grep -inE "python|winforms|port of|\bported\b" -- ':!docs/superpowers'` on the commit → empty.
4. `git grep -noE '"fr[a-z_]*_"' -- ':!docs/superpowers'` on the commit → empty.
5. The diff touches only comments, the `.gitignore` stanza, and the 13
   prefix strings — no code statements. (Exception: the test method rename
   NewKeysRoundTripWithExactPythonNames → NewKeysRoundTripWithExactJsonNames,
   forced by gate 3.)

## Delivery

One commit directly on `main` (user-approved), pushed to
`origin/main`. Suggested message: `chore: neutralize predecessor-app
references (comments, temp prefixes, .gitignore)`.
