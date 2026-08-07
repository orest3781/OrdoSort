# Concurrency and Startup Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Close audit findings 2.4, 2.6 and 5.3 — a box-counter rollback across stations, two unsynchronized shared-state reads, and the daily whole-database copy that blocks first paint.

**Architecture:** Three independent fixes plus a gate. Task 3 (startup) is deliberately a verify-then-decide: the obvious fix trades a startup stall for a torn backup, so it must be measured before it is chosen.

**Tech Stack:** C# / .NET 8, WPF, xUnit. Repo `S:\OrdoSort`, branch `main`, base `689a61f`.

## Global Constraints

- **The gate command is NOT plain `dotnet test`.** Smart App Control blocks the WPF test assembly by hash; when it does, `dotnet test` **silently skips the entire WPF suite and still exits 0**. Always:
  ```bash
  dotnet build OrdoSort.sln -t:Rebuild -p:Deterministic=false -v minimal
  dotnet test OrdoSort.sln --no-build -v minimal
  ```
  **Baseline: Core 419 + Wpf 561 = 980 green.**
- **`FocusRingCoverageTests.TabItemShowsTheBronzeFocusRing` is environment-sensitive** on this machine — it failed all of one afternoon and later passed, proven pre-existing by rebuilding an older commit in a throwaway worktree. Either result is acceptable; never chase it, never weaken it.
- **Do not touch `journal_mode`, `synchronous` or `busy_timeout`** (`History.cs:5-15`). WAL is deliberately absent because it relies on shared memory that does not work over a network filesystem and is the documented way to corrupt a shared SQLite file.
- **Static/process-wide test state is this repo's leading false-green source — three instances so far.** If you add or touch any, put every mutating class in one shared `[Collection]` and add a reflection membership test, then **prove it by removing the attribute from *each* member**, not just one.
- Keep tests hermetic and **verify it by inspecting the filesystem** — a fixture on an earlier branch wrote to `C:\Users\stoic\AppData\Local` and `S:\` while its own comment claimed otherwise.
- **The pattern is at eleven.** Each task below states its safety argument. Ask what fails if the guard is deleted, not whether the feature still works.
- A stray `OrdoSort.exe` or leftover `dotnet.exe` MSBuild node breaks rebuilds — `tasklist | findstr OrdoSort` first.
- Never `--no-verify`, never force, **never push**.

---

### Task 1: Editing one field can't roll back another station's box counter

**Audit finding 2.4 [A].** `LabelMakerViewModel` tracks dirty state **per row**: `_dirty` is a `HashSet<LabelClientVm>` and `:194` adds the whole row on *any* `PropertyChanged`. Persist then writes the whole client back, including `NextNumber` parsed from the VM's `NextNumberText` (`:51`) — the value loaded when the window opened.

So: Station A opens Label maker. Station B prints labels, advancing client X's counter from 100 to 140. Station A edits X's **retention days** and closes. A's persist writes `NextNumber = 100` back over B's 140 — and the next print reissues numbers already on physical boxes.

**Files:** `src/OrdoSort.Wpf/ViewModels/LabelMakerViewModel.cs`; a test file under `tests/OrdoSort.Wpf.Tests/`.

- [ ] **Step 1: Write the failing test.** Simulate the interleaving against a real `BoxLabelStore`: open with X at 100, advance X to 140 on disk behind the VM's back, edit an unrelated field on X, persist, then assert the stored `NextNumber` is **140**. Assert too that a *deliberate* edit of `NextNumberText` still wins — the fix must not make the field read-only.

- [ ] **Step 2: Run — MUST FAIL**, showing 100 written over 140. Paste it.

- [ ] **Step 3: Implement.** Two shapes are reasonable; choose and justify. Either track dirtiness **per field** so an untouched `NextNumber` is never written, or — probably better here — have the merge inside `BoxLabelStore.Mutate` keep the **on-disk** `NextNumber` unless the user actually edited that field. `Mutate` already holds the exclusive lock, so it is the natural place to reconcile.

  **Do not weaken the existing duplicate-id refusal or the counter-ceiling guard** — both were added deliberately and have their own tests.

- [ ] **Step 4: Full suites green. Step 5: Prove teeth** — revert, confirm the test fails *because the counter went backwards*. Paste it.

- [ ] **Step 6: Commit** `fix(labels): editing a client can't roll back a peer's counter`.

---

### Task 2: Two unsynchronized reads of shared state

**Audit finding 2.6 [V]/[A].** Two independent defects, both small:

**(a) One `SqliteConnection`, used from two threads.** `History.cs:48` holds a single connection. `LogCommit` runs on a thread-pool thread during a commit; `ExportHistoryAsync` and the history window are reachable from the UI *during* processing. `HistorySwapping` gates those two entry points during a database swap but not during ordinary commits. `Microsoft.Data.Sqlite` connections are not safe for concurrent use.

**(b) `Session.Current` reads `Pos` twice.** `Session.cs:45` — `Pos < Queue.Count ? Queue[Pos] : null`. `Pos++` on the commit thread between the two reads throws `IndexOutOfRangeException` on the UI thread at the last document.

**Files:** `src/OrdoSort.Core/History.cs`, `src/OrdoSort.Core/Session.cs`, plus tests.

- [ ] **Step 1: Write the failing tests.** For (a), drive concurrent `LogCommit` and a read (`Rows`/`RankedNames`/`ExportCsv`) from two threads and assert no exception and no corruption — run it enough iterations to be meaningful, and say how many. For (b), the double-read is a race you may not be able to force deterministically; if not, assert the *shape* instead — that `Current` reads `Pos` once — and say plainly in the test's own doc comment what it does and does not prove.

- [ ] **Step 2: Run.** Paste the output. **If (a) does not fail, say so** — it may be that the interleaving is hard to hit, which is evidence about severity, not a reason to skip the fix.

- [ ] **Step 3: Implement.** For (a), serialize access — a private lock inside `History` around every command is the smallest correct change; giving readers their own connection is the alternative but multiplies file handles on a share. **Whatever you choose, make sure `Dispose` cannot run while a command is in flight.** For (b), read `Pos` once into a local.

- [ ] **Step 4: Confirm you did not serialize away the concurrency that matters.** `RankedNames` runs after every commit and `ExportCsv` can be slow; a lock that makes the UI wait on a long export would be a new defect. Check what holds the lock and for how long, and say so.

- [ ] **Step 5: Full suites green. Step 6: Prove teeth** for both. **Step 7: Commit** `fix(core): serialize the history connection and read Pos once`.

---

### Task 3: The daily backup stops blocking first paint — measured, then decided

**Audit finding 5.3 [V].** `ShellViewModel`'s constructor runs `HistoryBackup.BackupDaily` — a whole-file `File.Copy` of the history database — then opens SQLite, **synchronously, before `MainWindow`'s constructor returns and before `Show()`**. The history *swap* path at `:1118-1124` already does the same work correctly inside `_scheduler.Run`.

**This one has a trap, which is why it is verify-then-decide.** The backup is taken *before* the connection opens **on purpose** — the code comment says "while the file is at rest". Moving it after the open would copy a live database, and a torn backup of the audit log is a worse outcome than a slow start. The audit's own finding 1.3 already flags raw `File.Copy` of a live SQLite file as unproven.

Note also that this got *worse* recently: the history index added roughly 39% to the file's size, and the copy scales with it.

**Files:** `src/OrdoSort.Wpf/ViewModels/ShellViewModel.cs`, possibly `src/OrdoSort.Core/HistoryBackup.cs`; tests.

- [ ] **Step 1: Measure before deciding anything.** Build a realistically large history database (the index work used 100k rows; reuse that approach, and seed in **one transaction** — `LogCommit` per row is thousands of fsyncs and measures the disk, not the code). Time, separately: the `BackupDaily` copy, the SQLite open plus migration, and the whole constructor. **Report all three.** Then state the honest conclusion — on a local disk this may be tens of milliseconds and not worth restructuring; the app's documented deployment is an SMB share, where a whole-file copy is a very different proposition.

- [ ] **Step 2: Decide, with the trap in view.** Options, in rough order of risk:
  - **Do nothing but record the measurement** — legitimate if the numbers are small and the restructure's risk outweighs it. Say so and stop; that is a real outcome, not a failure.
  - **Move the backup off the UI thread but keep it before the open** — e.g. do backup-then-open together inside the existing `_scheduler.Run` pattern the swap path already uses, and have the shell wait on that before it is usable. Preserves the at-rest guarantee; costs a visible "starting" state.
  - **Defer the backup to just after startup** — cheapest, but copies a live database and makes finding 1.3's unproven tearing risk real. **Do not choose this without saying explicitly that you are accepting that trade**, and I would rather you didn't.

  Whatever you choose, the at-rest property must either be preserved or its loss stated in a code comment.

- [ ] **Step 3: Implement your choice.** If it is "do nothing", add a comment at the constructor recording the measurement and why, so the next reader doesn't re-derive it.

- [ ] **Step 4: If you changed anything, prove first paint improved** — measure the same way as Step 1 and report before/after. A restructure that doesn't move the number is not worth its risk.

- [ ] **Step 5: Full suites green. Step 6: Commit** with a message describing what you actually did.

---

### Task 4: Gate and record

- [ ] **Step 1: Release build and full suites.**
```bash
dotnet build OrdoSort.sln -c Release -t:Rebuild -p:Deterministic=false -v minimal
dotnet test OrdoSort.sln -c Release --no-build -v minimal
```

- [ ] **Step 2: Smoke.** `dotnet run --project tools/OrdoSort.Smoke -- demo-full` ends `All checks passed`, exit 0. It exercises real filing against a real database, which is the direct check on Task 2's lock.

- [ ] **Step 3: Launch sanity.** Debug, `--config demo-full\config.json`. Confirm it starts, file or view history, open **Label maker** and confirm a client's next-number still shows correctly, `Stop-Process`, confirm none remains. Report the startup time you observed.

- [ ] **Step 4: Update the audit document.** Mark **2.4**, **2.6** and **5.3** with their outcomes — fixed with SHAs, or for 5.3 recorded-with-measurement if that was the decision. Correct the "What to fix, in order" list. Commit `docs: mark the concurrency and startup findings done`.

- [ ] **Step 5: Report, do not push.**

## Model assignments

| Task | Implementer | Review |
|---|---|---|
| 1 LabelMaker counter | sonnet | sonnet |
| 2 Connection + Pos | sonnet (threading) | sonnet |
| 3 Startup | sonnet (measurement-led) | sonnet |
| 4 Gate | sonnet | — |
