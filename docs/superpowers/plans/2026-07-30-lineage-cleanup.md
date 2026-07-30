# Lineage Cleanup Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Remove every predecessor-app reference (Python original / WinForms app) from the committed tree — comments, temp-path prefixes, one test method name, one `.gitignore` stanza — with zero behavior change.

**Architecture:** Pure text edits applied with exact old→new string pairs (use the Edit tool, never sed — the tree is LF-only and edits must stay byte-surgical). Three tasks: mechanical renames, comment rewrites, then gate + single commit + push.

**Tech Stack:** C# / .NET 8, xUnit, git. Repo: `S:\OrdoSort`, branch `main` (user-approved direct-to-main).

## Global Constraints

- **Zero behavior change.** Only comments, string constants used as temp-dir prefixes, one xUnit test method name, and `.gitignore` comment lines change.
- **One commit.** Tasks 1–2 leave changes uncommitted; Task 3 makes the single commit and pushes. Do not commit earlier.
- Baseline to preserve: `dotnet test OrdoSort.sln` = exactly **557 passed (Core 301 + Wpf 256)**, 0 failed, 0 skipped.
- Gate greps that must come back empty on the final commit (both exclude `docs/superpowers/`):
  - `git grep -inE "python|winforms|port of|ported" -- ':!docs/superpowers'`
  - `git grep -noE '"fr[a-z_]*_"' -- ':!docs/superpowers'`
- Line numbers cited below are as of commit `1158746` and are advisory — match by the quoted strings.
- "parity" between two current UI surfaces is NOT lineage and must remain (e.g. `SettingsViewModel.cs` "parity with the route detail panel").

---

### Task 1: Temp-path prefixes + .gitignore stanza

**Files:**
- Modify: `tools/OrdoSort.Smoke/Program.cs`, `tools/OrdoSort.Smoke/Reentrancy.cs`, 9 test files, `.gitignore`

**Interfaces:**
- Consumes: nothing from other tasks
- Produces: nothing later tasks reference; independently verifiable

- [ ] **Step 1: Apply the 13 prefix renames.** In each file, Edit the quoted string (each appears exactly once):

| File | old_string | new_string |
|---|---|---|
| `tools/OrdoSort.Smoke/Program.cs` | `"fr_smoke_"` | `"ordo_smoke_"` |
| `tools/OrdoSort.Smoke/Reentrancy.cs` | `"fr_reentry_"` | `"ordo_reentry_"` |
| `tests/OrdoSort.Wpf.Tests/ToolViewModelTests.cs` | `"frunlock_"` | `"ordounlock_"` |
| `tests/OrdoSort.Wpf.Tests/ToolViewModelTests.cs` | `"frbulk_"` | `"ordobulk_"` |
| `tests/OrdoSort.Wpf.Tests/ToolViewModelTests.cs` | `"frmm_"` | `"ordomm_"` |
| `tests/OrdoSort.Wpf.Tests/ShellFixture.cs` | `"frshell_"` | `"ordoshell_"` |
| `tests/OrdoSort.Wpf.Tests/SettingsViewModelTests.cs` | `"frset_"` | `"ordoset_"` |
| `tests/OrdoSort.Wpf.Tests/HistoryViewModelTests.cs` | `"frhist_"` | `"ordohist_"` |
| `tests/OrdoSort.Wpf.Tests/FolderWatchServiceTests.cs` | `"frwatch_"` | `"ordowatch_"` |
| `tests/OrdoSort.Core.Tests/ConfigNullKeysTests.cs` | `"frnull_"` | `"ordonull_"` |
| `tests/OrdoSort.Core.Tests/ConfigNewKeysTests.cs` | `"frnk_"` | `"ordonk_"` |
| `tests/OrdoSort.Core.Tests/ConfigHardeningTests.cs` | `"frcfg_"` | `"ordocfg_"` |
| `tests/OrdoSort.Core.Tests/AuditFailureTests.cs` | `"fraudit_"` | `"ordoaudit_"` |

- [ ] **Step 2: Replace the `.gitignore` `/sounds/` stanza comment.** Edit `.gitignore` — old_string (6 lines):

```
# Scratch audio: source material for the synthesized alert sounds. The ones
# the app actually ships live in src/OrdoSort.Wpf/Assets/sounds/.
# Anchored: a bare "sounds/" matches a folder of that name at ANY depth, so it
# also swallowed the shipped assets folder named in the line above. The three
# wavs there survived only because they were already tracked - a fourth one
# would have been ignored without a word.
```

new_string (4 lines):

```
# Scratch audio: source material for the synthesized alert sounds. The ones
# the app actually ships live in src/OrdoSort.Wpf/Assets/sounds/.
# Anchored: a bare "sounds/" would match a folder of that name at ANY depth —
# including the shipped assets folder named in the line above.
```

(The `/sounds/` pattern line below the comment is untouched.)

- [ ] **Step 3: Verify prefixes are gone and tests still pass**

Run: `git grep -noE '"fr[a-z_]*_"' -- ':!docs/superpowers'` against the working tree files (`grep -rnoE '"fr[a-z_]*_"' --include="*.cs" src tests tools`)
Expected: no output.
Run: `dotnet test OrdoSort.sln --verbosity minimal`
Expected: 557 passed (Core 301 + Wpf 256).

- [ ] **Step 4: Do NOT commit** (single commit happens in Task 3).

---

### Task 2: Lineage comment rewrites (31 sites)

**Files:**
- Modify: 15 files under `src/`, 7 under `tests/`, 2 under `tools/` — exact edits below

**Interfaces:**
- Consumes: nothing from Task 1 (independent edits)
- Produces: nothing later tasks reference

- [ ] **Step 1: Apply every edit below with the Edit tool, exact old→new.** Each old_string appears exactly once in its file.

**`src/OrdoSort.Core/Config.cs`** (3 edits):

1. old: `    // Python parity: hand-edited per-route keys survive a load/save round trip`
   new: `    // Hand-edited per-route keys survive a load/save round trip`
2. old (2 lines):
```
/// DPAPI-protected ("dpapi:&lt;base64&gt;", written by the app) or legacy
/// plaintext (hand-edited / migrated from the Python config).</summary>
```
   new:
```
/// DPAPI-protected ("dpapi:&lt;base64&gt;", written by the app) or legacy
/// plaintext (hand-edited).</summary>
```
3. old: `    // Appearance (Python parity: same key names, so an old config round-trips)`
   new: `    // Appearance (key names are stable, so existing configs round-trip)`

**`src/OrdoSort.Wpf/App.xaml.cs`**:

old (2 lines):
```
/// beside the config and surface as a dialog — the app survives (the Python
/// original's excepthook behavior).</summary>
```
new: `/// beside the config and surface as a dialog — the app survives.</summary>`

**`src/OrdoSort.Wpf/MainWindow.xaml.cs`**:

old: `        // Python-parity window lifecycle: the Ready dashboard is a compact`
new: `        // Window lifecycle: the Ready dashboard is a compact`

**`src/OrdoSort.Wpf/Services/FolderWatchService.cs`**:

old (2 lines):
```
/// <summary>Live folder monitoring with the exact semantics the WinForms app
/// proved out: any Created/Deleted/Renamed restarts a 1.5 s debounce (lets a
```
new:
```
/// <summary>Live folder monitoring: any Created/Deleted/Renamed restarts a
/// 1.5 s debounce (lets a
```

**`src/OrdoSort.Wpf/Services/HotkeyParser.cs`**:

old (2 lines):
```
/// "Ctrl+Shift+M") into real key gestures. The WinForms app hardwired
/// Ctrl+1-9 and treated the config field as a label; here the field binds.</summary>
```
new:
```
/// "Ctrl+Shift+M") into real key gestures — the config field genuinely
/// binds; it is never just a decorative label.</summary>
```

**`src/OrdoSort.Wpf/Services/PasswordVault.cs`**:

old (3 lines):
```
/// <summary>Saved Unlock passwords, DPAPI-protected per Windows user. The
/// Python original stored these in plaintext; legacy plaintext values still
/// read fine and are re-protected the next time Settings saves.</summary>
```
new:
```
/// <summary>Saved Unlock passwords, DPAPI-protected per Windows user.
/// Hand-edited plaintext values still read fine and are re-protected the
/// next time Settings saves.</summary>
```

**`src/OrdoSort.Wpf/Services/ViewerInput.cs`**:

old (2 lines):
```
/// <summary>Viewer gestures the Python app's users have in their fingers,
/// grafted onto Edge's PDF viewer: Shift+scroll zooms (anchored at the
```
new:
```
/// <summary>Familiar viewer gestures, grafted onto Edge's PDF viewer:
/// Shift+scroll zooms (anchored at the
```

**`src/OrdoSort.Wpf/Services/WebViewPdfViewer.cs`**:

old: `/// bundled PDF library — the same deliberate trade the WinForms app made.</summary>`
new: `/// bundled PDF library — a deliberate size-for-dependency trade.</summary>`

**`src/OrdoSort.Wpf/Theme/ThemeManager.cs`**:

old: `/// changes it — matching the Python original's colorSchemeChanged behavior.</summary>`
new: `/// changes it.</summary>`

**`src/OrdoSort.Wpf/Theme/ThemePalette.cs`** (2 edits):

1. old: `/// by ThemeTests — the same contract the Python original kept.</summary>`
   new: `/// by ThemeTests.</summary>`
2. old (3 lines):
```
/// background. The single source of truth for text on route buttons and
/// dashboard tiles (the WinForms app duplicated a cruder luminance shortcut
/// in three places).</summary>
```
   new:
```
/// background. The single source of truth for text on route buttons and
/// dashboard tiles.</summary>
```

**`src/OrdoSort.Wpf/ViewModels/BulkRenameViewModel.cs`**:

old (2 lines):
```
/// never overwrites; one batch undo. Port of the WinForms dialog with the
/// logic finally unit-testable.</summary>
```
new: `/// never overwrites; one batch undo. The logic is fully unit-testable.</summary>`

**`src/OrdoSort.Wpf/ViewModels/SettingsViewModel.cs`**:

old: `/// key survives by construction, killing the Python result_config() footgun.</summary>`
new: `/// key survives by construction.</summary>`
(Note: line 328's "JSON-cloning the original" refers to the original Config object — not lineage; leave it.)

**`src/OrdoSort.Wpf/ViewModels/ShellViewModel.cs`** (3 edits):

1. old (2 lines):
```
/// Session. No WPF types — the whole lifecycle is unit-tested headless, which
/// the WinForms MainForm never could be.</summary>
```
   new: `/// Session. No WPF types — the whole lifecycle is unit-tested headless.</summary>`
2. old: `    /// fresh daily backup for the NEW db — a gap in the WinForms port), save`
   new: `    /// fresh daily backup for the NEW db), save`
3. old (2 lines):
```
    /// the configured word separator as the boundary (Python-parity muscle
    /// memory; Enter stays free to commit).</summary>
```
   new:
```
    /// the configured word separator as the boundary (Enter stays free to
    /// commit).</summary>
```

**`src/OrdoSort.Wpf/ViewModels/TileViewModel.cs`**:

old (2 lines):
```
/// real focusable button in the view (the WinForms tiles were mouse-only
/// panels). Back/Fore are recomputed by the flash tick.</summary>
```
new:
```
/// real focusable button in the view (keyboard-accessible, not a mouse-only
/// panel). Back/Fore are recomputed by the flash tick.</summary>
```

**`src/OrdoSort.Wpf/Views/ProcessingView.xaml.cs`**:

old: `                    // Python-parity: the word Tab just added is SELECTED, so`
new: `                    // The word Tab just added is SELECTED, so`

**`src/OrdoSort.Wpf/Windows/MatchMergeWindow.xaml.cs`**:

old: `        // a fresh WebView2 per run — the WinForms version leaked one per file`
new: `        // a fresh WebView2 per run — never reused, so nothing leaks across files`

**`tests/OrdoSort.Core.Tests/ConfigNewKeysTests.cs`** (2 edits):

1. old: `/// <summary>The Python-parity keys: exact JSON names, defaults, validation.</summary>`
   new: `/// <summary>The config keys: exact JSON names, defaults, validation.</summary>`
2. old: `    public void NewKeysRoundTripWithExactPythonNames()`
   new: `    public void NewKeysRoundTripWithExactJsonNames()`
   (Test method rename — xUnit discovers by attribute, so this is behavior-neutral; total test count is unchanged.)

**`tests/OrdoSort.Core.Tests/QcTests.cs`** (2 edits):

1. old (3 lines):
```
/// <summary>QC probes for suspected porting gaps vs the Python original.
/// These encode the EXPECTED (Python-parity or better) behaviour — a failure
/// here is a porting bug to fix.</summary>
```
   new:
```
/// <summary>QC probes for edge cases in config handling and filing rules.
/// These encode the EXPECTED behaviour — a failure here is a real bug to
/// fix.</summary>
```
2. old: `        // Python parity: hand-edited per-route keys must not be lost on save`
   new: `        // Hand-edited per-route keys must not be lost on save`

**`tests/OrdoSort.Wpf.Tests/FilingLoopTests.cs`**:

old (3 lines):
```
/// <summary>The filing loop, headless: real Session + History + temp folders,
/// fake viewer. This is the coverage the WinForms app only had via the manual
/// smoke tool.</summary>
```
new:
```
/// <summary>The filing loop, headless: real Session + History + temp folders,
/// fake viewer.</summary>
```

**`tests/OrdoSort.Wpf.Tests/SettingsViewModelTests.cs`**:

old (2 lines):
```
        // the Python settings dialog wiped anything it didn't carry by hand —
        // the clone-then-patch build makes that class of bug impossible
```
new (2 lines):
```
        // the clone-then-patch build makes it impossible for the settings
        // dialog to wipe keys it doesn't know about
```

**`tests/OrdoSort.Wpf.Tests/ShellFixture.cs`**:

old (2 lines):
```
/// and a real SQLite history — only the viewer and dialogs are fakes. This is
/// the app the WinForms version could only exercise through the smoke tool.</summary>
```
new: `/// and a real SQLite history — only the viewer and dialogs are fakes.</summary>`

**`tests/OrdoSort.Wpf.Tests/ThemeTests.cs`**:

old (2 lines):
```
/// <summary>The visual contract: every text pairing the theme ships meets
/// WCAG AA (4.5:1) in BOTH schemes. Ported from the Python app's theme tests.</summary>
```
new:
```
/// <summary>The visual contract: every text pairing the theme ships meets
/// WCAG AA (4.5:1) in BOTH schemes.</summary>
```

**`tools/OrdoSort.Smoke/DemoReset.cs`**:

old (2 lines):
```
/// set-aside folders, and a ready-to-use demo\config.json. Self-contained (no
/// Python) — used by reset.bat. The demo folder is resolved relative to the
```
new:
```
/// set-aside folders, and a ready-to-use demo\config.json. Self-contained —
/// used by reset.bat. The demo folder is resolved relative to the
```

**`tools/OrdoSort.Smoke/SmokeUi.cs`**:

old (2 lines):
```
/// would hang the harness. Replaces the WinForms SuppressDialogs hook that
/// leaked into the production form.</summary>
```
new: `/// would hang the harness.</summary>`

- [ ] **Step 2: Sweep for anything missed**

Run: `grep -rniE "python|winforms|port of|ported" --include="*.cs" --include="*.xaml" src tests tools`
Expected: no output. If a hit remains, apply the spec's rewrite rule (keep the constraint, drop the predecessor) and note it in your report.

- [ ] **Step 3: Build and test**

Run: `dotnet build OrdoSort.sln && dotnet test OrdoSort.sln --verbosity minimal`
Expected: build succeeds; 557 passed (Core 301 + Wpf 256), 0 failed, 0 skipped.

- [ ] **Step 4: Do NOT commit** (single commit happens in Task 3).

---

### Task 3: Gate, single commit, push

**Files:** none new — verification, then git.

**Interfaces:**
- Consumes: the edited working tree from Tasks 1–2
- Produces: the pushed cleanup commit on `origin/main`

- [ ] **Step 1: Confirm the diff is text-only**

Run: `git diff --stat` and `git diff | grep -E "^[+-]" | grep -vE "^[+-]{3}" | grep -vE "^[+-]\s*(//|///|#|rem )" | grep -viE "fr[a-z_]*_|ordo[a-z_]*_|NewKeysRoundTripWithExact"`
Expected: the second command prints nothing (every changed line is a comment, a prefix string, or the test method name). Investigate anything it prints before proceeding.

- [ ] **Step 2: Full test gate**

Run: `dotnet test OrdoSort.sln --verbosity minimal`
Expected: 557 passed (Core 301 + Wpf 256).

- [ ] **Step 3: Commit (single commit, exact message)**

```bash
git add -A
git status --short
```
Confirm only expected files are staged (the ~24 edited files; nothing under `bin/`, `obj/`, `demo/`). Then:

```bash
git commit -m "chore: neutralize predecessor-app references (comments, temp prefixes, .gitignore)

Co-Authored-By: Claude Fable 5 <noreply@anthropic.com>
Claude-Session: https://claude.ai/code/session_017qkNhAzYikJcFjhTdLzXJT"
```

- [ ] **Step 4: Run the committed-tree gate greps**

Run: `git grep -inE "python|winforms|port of|ported" -- ':!docs/superpowers'`
Expected: no output.
Run: `git grep -noE '"fr[a-z_]*_"' -- ':!docs/superpowers'`
Expected: no output.

- [ ] **Step 5: Push and verify**

```bash
git push origin main
git ls-remote origin main
```
Expected: push accepted (no force); ls-remote SHA equals `git rev-parse main`.
