# Debounce Pair Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Close findings 5.4 and 5.2 of `docs/superpowers/audits/2026-08-04-full-audit.md` — the two remaining places that do synchronous filesystem I/O on the UI thread on every keystroke.

**Architecture:** Both use `DebouncedProbe`, already proven at four sites in this codebase. They are **not** the same size, so they are separate tasks with separate review gates:

- **5.4 (tile preview)** lives in `SettingsViewModel`, which already constructs `DebouncedProbe`s, already takes a scheduler and `uiContext`, and already disposes them. Small and contained.
- **5.2 (Bulk Rename)** is structural: `BulkRenameViewModel` has a **parameterless constructor**, is **not `IDisposable`**, and is built at nine call sites — most of them tests that set a property and immediately assert on `Preview`. Debouncing breaks that synchronous contract, so the constructor, the disposal, and those tests all move together.

**Tech Stack:** C# / .NET 8, WPF, xUnit. Repo `S:\OrdoSort`, branch `main`, base `d76028d`.

## Global Constraints

- **`DebouncedProbe`'s contract** (`src/OrdoSort.Wpf/Services/DebouncedProbe.cs`): constructed with `(IWorkScheduler, SynchronizationContext?, Action<T> apply, int delayMs)`. `Trigger(compute, immediate)` runs `compute` off the UI thread and marshals the result to `apply`. `Resolve(fastPathResult, neutralValue, compute, immediate)` additionally lets a synchronously-known answer skip the probe entirely and shows a neutral value while one is pending. It has a generation guard so a stale result cannot overwrite a newer one, and `Dispose` bumps that generation. **Read it before using it** — do not reimplement debouncing by hand.
- **The compute closure runs off the UI thread.** It must produce data only. It must not touch an `ObservableCollection`, a bound property, or anything WPF — that is a thread-affinity crash waiting to happen. Only the `apply` callback touches UI state.
- **Discrete input is not a burst.** A checkbox click, a combo selection or a date pick should resolve **immediately**; only typed text needs the delay. `Trigger`/`Resolve` take `immediate: true` for exactly this. Debouncing a single click adds lag for no benefit.
- **The gate command is NOT plain `dotnet test`.** Smart App Control blocks the WPF test assembly by hash; when it does, `dotnet test` **silently skips the entire WPF suite and still exits 0**. Always run:
  ```bash
  dotnet build OrdoSort.sln -t:Rebuild -p:Deterministic=false -v minimal
  dotnet test OrdoSort.sln --no-build -v minimal
  ```
  **Baseline: Core 390 + Wpf 537 = 927 green.** A WPF line that is missing, reports a skip, or reports a much smaller number means the suite did not run.
- A stray `OrdoSort.exe` or leftover `dotnet.exe` MSBuild node breaks rebuilds — `tasklist | findstr OrdoSort` first.
- **The lesson, now at seven recorded instances:** every fix round here came from *an untested branch carrying the entire safety argument*. For a debounce the safety argument is **"the UI thread no longer does the I/O"** — so a test that merely proves the preview eventually updates would pass just as happily with the debounce removed. Ask what fails if the probe is deleted.
- Never `--no-verify`, never force, **never push**.

## File Structure

| File | Responsibility | Task |
|---|---|---|
| `src/OrdoSort.Wpf/ViewModels/SettingsViewModel.cs` | tile preview off the UI thread; stop recomputing for unselected rows | 1 |
| `src/OrdoSort.Wpf/ViewModels/BulkRenameViewModel.cs` | debounced plan; new ctor params; `IDisposable` | 2 |
| `src/OrdoSort.Wpf/MainWindow.xaml.cs:314` | dispose the bulk-rename view model | 2 |
| `tests/OrdoSort.Wpf.Tests/` | ~9 call sites that assume a synchronous `Refresh()` | 2 |

---

### Task 1: The Settings tile preview stops probing the disk on every keystroke

**Audit finding 5.4 [A].** `HookWatch` (`SettingsViewModel.cs:742-751`) subscribes to **every** `PropertyChanged` on **every** `WatchEditVm` and calls `RecomputeTilePreview()` unconditionally. That runs `FolderMonitor.Status` (`FolderMonitor.cs:38-73`), which does `Directory.Exists` plus `Directory.EnumerateFiles` — **recursively** when the folder is marked recursive — synchronously on the UI thread, for every keystroke in a label, path, colour or filetype field.

**There is a second, separate waste here worth fixing at the same time:** `RecomputeTilePreview` only ever renders `SelectedWatch`. So a keystroke on a *non-selected* row triggers a full filesystem enumeration whose result is then thrown away. That one needs no debounce — it needs an early return.

**Files:**
- Modify: `src/OrdoSort.Wpf/ViewModels/SettingsViewModel.cs`
- Create: `tests/OrdoSort.Wpf.Tests/TilePreviewProbeTests.cs`

**Interfaces:**
- Produces: a `DebouncedProbe<FolderStatus>` field alongside the existing probes, applied by a method that sets the `TilePreview*` properties. Follow the shape of the existing probes at `:544` — same construction, same disposal.

- [ ] **Step 1: Split the cheap work from the expensive work.** In `RecomputeTilePreview` (`:1277-1301`), everything except `FolderMonitor.Status(...)` is pure: palette lookups, `ParseColor`, `IdealForeground`, the label. Only `status` costs I/O. The cheap parts should keep updating instantly; only the status-derived properties (`TilePreviewCount`, `TilePreviewNote`, `TilePreviewHint`, and the alerting-dependent colours) wait on the probe.

- [ ] **Step 2: Skip rows that aren't selected.** `HookWatch`'s handler should return early when the sender is not `SelectedWatch` — the recompute reads nothing else. Keep the `Section` branch (`RebuildWatchRows` + `SectionChoices`) firing for **all** rows, because that one genuinely is global. Getting this backwards would break the dashboard grouping, so re-read the handler before editing.

- [ ] **Step 3: Write the failing tests.** Create `tests/OrdoSort.Wpf.Tests/TilePreviewProbeTests.cs`. The tests that matter:

1. **The UI thread does not do the enumeration.** Inject a scheduler seam and a `FolderMonitor.Status` seam (or a folder whose enumeration is observably slow) and assert the property setter returns promptly rather than blocking for the probe's duration. This is the finding; without it nothing here is pinned.
2. **A keystroke on a non-selected row does not probe at all.** Count probe invocations through the seam; changing a property on an unselected `WatchEditVm` must produce **zero**.
3. **The preview still becomes correct.** Pump/await the probe and assert the tile properties reflect the folder — so the fix is not "never probe".
4. **A discrete change resolves immediately** where the code passes `immediate: true`, if you use it here.

Follow the existing conventions: `SettingsViewModel`'s constructor already accepts a scheduler, a `uiContext` and a `probeDelayMs` (`:541-546`), and the WPF test project has `InlineWorkScheduler` for making scheduled work complete synchronously. **Read both before writing** rather than inventing a seam.

If `FolderMonitor.Status` cannot be substituted without changing its signature, say so and choose the least invasive seam — but do **not** make the test depend on real timing.

- [ ] **Step 4: Run — MUST FAIL.** Paste the output.

- [ ] **Step 5: Implement.** Construct the probe beside the existing ones, add it to `Dispose` alongside them (find how the others are disposed — if there is a list or an explicit sequence, match it), and route `RecomputeTilePreview` through it.

- [ ] **Step 6: Tests pass; full suites green.** Expected: Core 390, Wpf 537 + your new tests. State the count you added.

- [ ] **Step 7: Prove teeth.** Remove the debounce — call `FolderMonitor.Status` directly on the UI thread again — rebuild, and confirm test 1 fails. Separately, remove the not-selected early return and confirm test 2 fails. Restore both. **Two separate proofs**; paste both, and say *why* each failed.

- [ ] **Step 8: Commit** `perf(settings): the tile preview probes off the UI thread, and only for the selected row`.

---

### Task 2: Bulk Rename stops hitting the disk on every keystroke

**Audit finding 5.2 [A].** Every op setter — `Find`, `Replace`, `Prefix`, `Suffix` (`BulkRenameViewModel.cs:69,72,75,78`) and the discrete ones at `:63,66,82,85-97` — calls `Refresh()` synchronously. `Refresh` calls `Plan(...)`, whose `Free` check (`BulkRename.cs:159-161`) does a `File.Exists` per file, and more on each collision. On an SMB destination — a named design goal — that is a network round trip per file per keystroke, in the tool built for batches.

**This task is structural, and that is the risk.** `BulkRenameViewModel` currently has a **parameterless constructor** (`:54`), is **not `IDisposable`**, and is constructed at nine sites including `MainWindow.xaml.cs:314` and ~8 tests that set a property and immediately assert on `Preview`. Debouncing breaks that synchronous contract.

**Files:**
- Modify: `src/OrdoSort.Wpf/ViewModels/BulkRenameViewModel.cs`
- Modify: `src/OrdoSort.Wpf/MainWindow.xaml.cs:314`
- Modify: the existing tests that construct it
- Create: `tests/OrdoSort.Wpf.Tests/BulkRenameProbeTests.cs`

- [ ] **Step 1: Enumerate the blast radius first, before changing anything.** `grep -rn "new BulkRenameViewModel" src/ tests/`. For each site, note whether it depends on `Refresh()` having completed synchronously. **Write that list into your report** — it is the map for the rest of the task and the thing a reviewer will check you against.

- [ ] **Step 2: Split `Refresh` into compute and apply.** `Refresh` (`:207-235`) currently does both: it calls `Plan(...)` (the I/O) *and* mutates `Preview`, `NeedsNameCount`, `CountsLine` and raises change notifications. Separate them:
  - a **compute** function returning `List<PlannedRename>` — pure plus `File.Exists`, safe to run off the UI thread, touching no bound state;
  - an **apply** function taking that list and doing everything from `Preview.Clear()` onward, on the UI thread.

  **The compute closure must not touch `Preview` or any bound property.** Mutating an `ObservableCollection` off the UI thread is a crash, and an intermittent one.

- [ ] **Step 3: Add the constructor parameters, keeping existing call sites compiling.** Follow the proven shape from `RouteEditVm`/`WatchEditVm` (`SettingsViewModel.cs:58-63,210-215`): optional parameters defaulting to `scheduler ?? new TaskWorkScheduler()`, a nullable `uiContext`, and `probeDelayMs = 300`. Make the class `IDisposable`, disposing the probe.

- [ ] **Step 4: Wire the setters, distinguishing typed text from discrete input.** `Find`, `Replace`, `Prefix`, `Suffix` are typed — debounce them. `ReviewMode`, `ReceivedDate`, `CaseIndex` and the five `DeleteSeg*` flags are single clicks — resolve them **immediately**. Also check `:149,160,173,250,266`, which call `Refresh()` from non-setter paths (adding files, clearing, undo): those are discrete actions and should be immediate too. Decide each deliberately and record the classification in your report.

- [ ] **Step 5: Dispose it at the call site.** `MainWindow.xaml.cs:314` currently constructs the view model inline inside the `ShowDialog()` expression, so there is nothing to dispose. Restructure minimally — hold it in a local, `ShowDialog()`, then dispose — matching how `OnSettings` (`:326-340`) already does exactly this for `SettingsViewModel`.

- [ ] **Step 6: Fix the existing tests.** The ~8 that assume synchronous refresh should inject `InlineWorkScheduler` and a 0ms delay so the work completes inline. **Do not** weaken their assertions or add sleeps to make them pass — either is a false green, and this codebase has seven recorded instances of tests that pass for the wrong reason. If a test genuinely cannot be made deterministic, say so rather than papering over it.

- [ ] **Step 7: Write the new test — the one that pins the finding.** `BulkRenameProbeTests.cs`: with a blocking probe seam, setting `Find` must return promptly rather than waiting on the file checks; and the preview must still become correct once the probe completes. Also assert that a discrete toggle resolves immediately, so the Step 4 classification is pinned rather than assumed.

- [ ] **Step 8: Full suites green.** Expected: Core 390, Wpf 537 + your new tests, with the existing ones still passing on their **original** assertions. State the count.

- [ ] **Step 9: Prove teeth.** Remove the debounce (call the compute synchronously in the setter again), rebuild, confirm the new promptness test fails. Restore. Paste it, and say why it failed.

- [ ] **Step 10: Commit** `perf(bulkrename): the rename preview computes off the UI thread`.

---

### Task 3: Gate and record

- [ ] **Step 1: Release build and full suites.**

```bash
dotnet build OrdoSort.sln -c Release -t:Rebuild -p:Deterministic=false -v minimal
dotnet test OrdoSort.sln -c Release --no-build -v minimal
```

- [ ] **Step 2: Smoke.** `dotnet run --project tools/OrdoSort.Smoke -- demo-full` ends `All checks passed`, exit 0.

- [ ] **Step 3: Launch sanity, with hands on both surfaces.** This is where a debounce regression actually shows up, and no unit test covers the feel. Launch Debug with `--config demo-full\config.json`, then:
  - open **Bulk rename**, add the demo files, and type into `Find` — confirm the preview updates shortly after you stop typing, and that typing itself never stutters;
  - open **Settings → Dashboard**, select a watched folder, and type in its Label and Path — confirm the tile preview updates and the window stays responsive.

  Report what you observed in both. `Stop-Process` afterwards and confirm none remains.

- [ ] **Step 4: Update the audit document.** Mark findings **5.2** and **5.4** fixed with their commit SHAs, in the style the already-fixed findings use. Note the second fix folded into 5.4 (no probe at all for unselected rows). Correct the "What to fix, in order" list — item **9**. Commit `docs: mark the debounce pair done`.

- [ ] **Step 5: Report, do not push.**

## Model assignments

| Task | Implementer | Review |
|---|---|---|
| 1 Tile preview | sonnet | sonnet |
| 2 Bulk Rename | sonnet (structural: ctor, disposal, 9 call sites) | sonnet |
| 3 Gate | sonnet (hands-on UI check) | — |
