# v1.0 Release Blockers Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Clear the mechanical items that gate a v1.0 release, plus one data-loss bug pulled forward from the deferred list.

**Tech Stack:** C# / .NET 8, WPF, xUnit. Repo `S:\ordosort-session` (worktree), branch `session/header-pickers`, base `b9739c6`.

**Verified still outstanding** (checked 2026-08-09, not assumed): no `<Version>`/`<AssemblyVersion>`/`<InformationalVersion>` anywhere; no About window; `SQLitePCLRaw` **2.1.6** transitively via `Microsoft.Data.Sqlite` 8.0.11; no `THIRD-PARTY-NOTICES`; `.claude/` absent from `.gitignore`; no WebView2 prerequisite check.

## Global Constraints

- **Work only in `S:\ordosort-session`.** `S:\OrdoSort` is a separate checkout with another Claude session active in it — never read from, write to, or `cd` into it.
- **The gate command is NOT plain `dotnet test`.** Smart App Control blocks the WPF test assembly by hash; `dotnet test` alone **silently skips the entire WPF suite and still exits 0**:
  ```bash
  dotnet build OrdoSort.sln -t:Rebuild -p:Deterministic=false -v minimal
  dotnet test OrdoSort.sln --no-build -v minimal
  ```
  **Baseline: Core 616 + Wpf 1518.** Core.Tests takes ~57s and the WPF suite ~89s by design.
- **`-p:Deterministic=false` is load-bearing** for the test run — do not "fix" it, and be careful that any version work does not disturb it.
- **Known flakes — report, never chase, never weaken:** `UnlockProbeWritesNothingTests.NothingChangesInTheFixtureDirectoryOrTemp` (temp-file parallelism, passes in isolation); `FocusRingCoverageTests.TabItemShowsTheBronzeFocusRing`; `WebViewPdfViewerGuardBehaviourTests` (all 5 fail together with COM `Class not registered`); `SettingsViewModelTests.ValidateRouteProbeRunsOncePerPauseNotPerKeystroke` — *observed 2026-08-15, after this plan was written*: it `Thread.Sleep(350)`s and then asserts an **exact** debounce call count, so a loaded parallel run can straddle the window and see 2 calls. Failed once in a full-solution run, then passed 3/3 in isolation and 1739/1739 on a re-run of the identical binary. Precisely the wall-clock-assertion trap `2026-08-05-history-indexes.md:23` warns against — the durable fix is to assert on the debounce seam rather than on elapsed time, which is a change to the test, not to the code under it.
- **This session cannot drive the real UI** — screen capture returns black, input injection denied. Verify off-screen on the WPF suite's STA fixture.
- **The pattern is at thirteen.** Assert observable state, and ask what fails if the code under test is deleted.
- Never `--no-verify`, never force, **never push**.

---

### Task 1: Version, and an About box that tells the truth

**Files:** `Directory.Build.props` (create if absent) or the csprojs, a new About window, `MainWindow.xaml(.cs)`, tests.

- [ ] **Step 1: Set the version in exactly one place.** `<Version>`, `<AssemblyVersion>`, `<FileVersion>` and `<InformationalVersion>` should derive from a single property so they can never disagree. **v1.0.0.** Check this does not disturb `-p:Deterministic=false` or the smoke tooling.

- [ ] **Step 2: An About box reachable from the app**, showing product name, version, and a pointer to the third-party notices from Task 3. Match the existing window conventions — theme resources, focus ring, Esc to close, `AutomationProperties.Name`. Look at an existing small dialog before inventing a shape.

- [ ] **Step 3: Test that the displayed version equals the assembly's** — not a hardcoded string in two places. A test asserting `"1.0.0" == "1.0.0"` proves nothing; read the assembly's own metadata and compare.

- [ ] **Step 4: Commit** `feat(app): version the build and add an About box`.

---

### Task 2: The WebView2 prerequisite

**Files:** startup path (`App.xaml.cs` and/or `ShellViewModel`), tests.

- [ ] **Step 1: Detect a missing runtime before it bites.** The PDF viewer needs the WebView2 runtime; on a machine without it, initialisation throws a COM exception — the same `Class not registered` this repo's own tests see intermittently. **Find where that surfaces today** and describe what the user currently experiences.

- [ ] **Step 2: Fail clearly, not obscurely.** If the runtime is absent, say so in plain language with what to install, and keep the rest of the app usable if it can be — filing documents should not require a viewer. **Decide deliberately whether the app blocks or degrades, and record the reasoning.**

- [ ] **Step 3: Test both branches** with the detection stubbed — present and absent. Do not make the test depend on the host machine's actual runtime.

- [ ] **Step 4: Commit** `feat(viewer): say so when the WebView2 runtime is missing`.

---

### Task 3: Dependencies and repository hygiene

- [ ] **Step 1: `SQLitePCLRaw` and CVE-2025-6965.** Currently **2.1.6**, arriving transitively through `Microsoft.Data.Sqlite` 8.0.11. **Establish the facts first**: which versions are affected, which fixed, and whether this app's usage is even reachable by the vulnerability. Then choose — bump `Microsoft.Data.Sqlite`, or pin the transitive package directly. **Report the CVE's actual content rather than assuming from its number**, and note that `journal_mode=TRUNCATE`, `synchronous=FULL` and `busy_timeout` are load-bearing for network shares and must keep working.

- [ ] **Step 2: `THIRD-PARTY-NOTICES`.** Enumerate every shipped dependency with its licence. Derive it from the actual package graph, not from memory. There is deliberately **no LICENSE file** — the owner is deciding that separately; do not add one.

- [ ] **Step 3: `.gitignore`.** Add `.claude/`. Check what else is untracked and shouldn't be — `.playwright-mcp/` and stray PNGs have appeared in this repo.

- [ ] **Step 4: Stale worktrees.** Six `agent-*` worktrees under `.claude/worktrees/` are long dead. **List them and their sizes before removing anything**, confirm each is genuinely unreferenced (`git worktree list`, `git worktree prune --dry-run`), and only then clean up. **Do not touch `S:\OrdoSort` or any worktree belonging to a live session.**

- [ ] **Step 5: Commit** the dependency change and the hygiene separately.

---

### Task 4: A rename that transits through a sibling's id destroys that sibling

**Files:** wherever the rename/commit path resolves target names, plus tests.

- [ ] **Step 1: Reproduce it first.** Renaming A→B where B is currently a *sibling's* id can sweep that sibling off disk. **Write the failing test before touching anything**, and state precisely which file is lost and when. This is the only data-loss bug left on the list, and this app's promise is that a document is either where it started or where it was going.

- [ ] **Step 2: Fix it so the sibling survives.** A rename must never remove a file it did not create, and must never claim a name that is currently occupied by a different document without the user knowing.

- [ ] **Step 3: Prove teeth.** Revert the fix, confirm the test fails **because the sibling's file is gone from disk** — assert its absence explicitly, not just a status string.

- [ ] **Step 4: Commit** `fix(rename): a rename can no longer sweep a sibling off disk`.

---

### Task 5: Gate

- [ ] **Step 1: Release build and full suites** against the baseline above.
- [ ] **Step 2: Smoke.** `dotnet run --project tools/OrdoSort.Smoke -- demo-full` ends `All checks passed`, exit 0.
- [ ] **Step 3: Confirm the shipped artefact carries the version** — inspect the built exe's file metadata, not the csproj.
- [ ] **Step 4: Report, do not push.**

## Model assignments

| Task | Implementer | Review |
|---|---|---|
| 1 Version + About | sonnet | — |
| 2 WebView2 | sonnet | — |
| 3 Deps + hygiene | sonnet | — |
| 4 Rename data loss | sonnet | sonnet (read-only) |
| 5 Gate | sonnet | — |
