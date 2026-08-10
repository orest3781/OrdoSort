# v1.0 Release Audit — Tests, Build and Packaging

**Date:** 2026-08-09 · **Audited:** `session/header-pickers` at `6c11ead` · **Method:** read-only, with the build and suites run directly.

> Recorded by the coordinating session from the auditor's report. That agent had a read-only toolset and could not create this file itself; its findings are reproduced here so the audit record is complete alongside the core, UI and security passes.

**Counts: Critical 0 · Important 3 · Minor 5.**
**Verdict: releasable as v1.0.** Fix the release-zip omission before tagging; ship honest release notes about the unsigned-binary experience.

---

## Part A — are the tests worth trusting?

**Suites, run by the auditor:** Release rebuild with `-p:Deterministic=false`, 0 errors; `dotnet test --no-build` → **Core 616/616, Wpf 1525/1525**, matching the stated baseline exactly.

**The `dotnet test` pitfall did not reproduce.** This repo has treated "plain `dotnet test` silently skips the entire WPF suite and still exits 0" (Smart App Control blocking the test assembly by hash) as a standing constraint. On this machine, a plain `dotnet test` with an incremental build also ran all 2141 tests. **Machine-state-dependent — keep the explicit rebuild, but the claim is weaker than it has been stated.**

**Static/process-wide state — all three guards verified sound.** `Commit.RaceHookForTests` (`UndoRaceCollection` + membership test, `UndoFailureTests.cs:250-318`, full pairwise check across `UndoFailureTests`/`PipelineTests`/`AuditFailureTests`); `Unlock.LargeFileThresholdBytes` (`UnlockThresholdCollection`, `UnlockNeverOverwritesTests.cs:243-292`); `App._crashDir` (`CrashDirTestCollectionMembershipTests`, `CultureInvariantDatesTests.cs:157-191`). All present, correctly wired, complete membership coverage.

**Test seams.** All six documented seams present as cited. A **seventh** mutable test-only static exists and was undercounted: `ThemeManager.IsHighContrast` (`ThemeManager.cs:51`) — old (commit `cedbfa2`), single-consumer, well documented. Not a new arrival. *(An eighth, `Commit.SkipRaceHookForTests`, was added afterwards by the `SkipFile` fix in `5e8caee`, mirroring the adjacent `RaceHookForTests`.)*

**Sample of the ~800 tests added 2026-08-09** — `MatchMergeHeaderMappingTests`, `ZipMergeTests`, `LabelMakerViewModelTests`' sibling-sweep test, `AboutWindowVersionTests`, `ViewerNavigationPolicyTests`' WebView2 guard tests — all assert real observable outcomes (disk content, resolved column names, real assembly metadata versus a second hardcoded copy), with explicit "what fails if the guard is deleted" reasoning in comments. **No mock-verification patterns (`Verify`/`Received`) exist anywhere in the suite.** Sample only; not exhaustive.

**Known flakes confirmed as coded**, none flaked during the audit run: `UnlockProbeWritesNothingTests.NothingChangesInTheFixtureDirectoryOrTemp` (temp-file parallelism), `FocusRingCoverageTests.TabItemShowsTheBronzeFocusRing`, `WebViewPdfViewerGuardBehaviourTests` (all five together, COM `Class not registered`).

### Important 3 — `LabelPreview.cs` has no tests

`src/OrdoSort.Wpf/Views/LabelPreview.cs` (151 lines) builds the physical printed label sheet — box numbers and destroy-by dates — and has **zero** references anywhere in `tests/`. Printed output that is wrong is expensive to discover and impossible to recall.

---

## Part B — can this actually ship?

**Packaging verified live.** Published via `publish.bat`'s exact command: single-file, framework-dependent (`--self-contained false`), win-x64, ~6.5MB `OrdoSort.exe`. **Launched it** — a real window opened and first run created `config.json` and `history.sqlite` correctly.

**Version confirmed in real file metadata**, not merely the csproj: `FileVersion 1.0.0.0`, `ProductVersion 1.0.0`, matching `Directory.Build.props:14`.

### Important 1 — THIRD-PARTY-NOTICES never shipped in the release artifact

`.github/workflows/release.yml`'s zip step copied only the bare `ordosort.exe` into the release archive, not the publish folder. So the notices file — correctly copied beside the exe by `OrdoSort.Wpf.csproj:33-34` for local publishes — **never reached a downloaded release**, and `AboutWindow.xaml.cs:51-52`'s "View third-party notices" button (`if (!File.Exists(path)) return;`) silently did nothing for every real user. A licence-notice promise broken in the shipped artifact, plus a dead UI button.

*Fixed in `bdc8ce3`: both zips now include the file, copied from the repo root, with a guard that fails the build if it is missing.*

### Important 2 — releases are unsigned

`Get-AuthenticodeSignature` on the built exe returns `NotSigned`. `.github/workflows/release.yml:66-82` wires Azure Trusted Signing but gates it on secrets that are not configured, so releases ship unsigned. **A user's first run hits Windows SmartScreen's "Windows protected your PC", which defaults to "Don't run"** — "More info → Run anyway" is the only path through. Needs a code-signing certificate the owner does not yet have; the workflow switches on automatically once the secrets exist.

### Minor

- `publish.bat`'s comment overstates .NET 8 Desktop Runtime presence on "modern Windows" — Windows ships .NET Framework, not .NET 8. Local dev script only; the release pipeline also builds a self-contained variant, so real releases are not exposed.
- Local publish output is not purely single-file: three WebView2 XML doc-comment files (~815KB) land beside the exe. Cosmetic.
- `docs/superpowers/plans/2026-08-09-v1-release-blockers.md` has every checkbox unchecked despite Tasks 1–3 having landed (`3f4f7bf`, `6c11ead`, `feacd55`, `1599c8e`). Stale planning doc, not a code defect.

### .NET 8 EOL

Both projects target `net8.0` / `net8.0-windows`. **.NET 8 reaches end of life on 2026-11-10**, roughly three months after a v1.0 ship. Noted, not migrated.

---

## Not checked

Exhaustive review of all ~800 tests added that day (targeted sample only); the `OrdoSort.Smoke` gate (run separately by the coordinating session — `All checks passed`, cohorts unmoved); and reproduction of the Smart App Control pitfall, which did not occur on this machine.
