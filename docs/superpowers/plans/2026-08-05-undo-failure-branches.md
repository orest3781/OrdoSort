# Undo Failure Branches Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Close finding 1.5 of `docs/superpowers/audits/2026-08-04-full-audit.md` — `Commit.UndoAction`'s three failure branches have no test at all. Undo is the safety net for a mis-filed document, and its failure handling is the least-exercised code in the product.

**Architecture:** Mostly a test task, but not only. The promise being pinned is not "it throws" — it is **"the filed copy stays put and the session stays consistent"**, which is what `UndoAction`'s own doc comment claims and what a user relies on. So the tests assert *state after failure*, at both the `Commit` and `Session` levels. One genuine defect found during planning is settled by measurement in Task 1.

**Tech Stack:** C# / .NET 8, xUnit. Repo `S:\OrdoSort`, branch `main`, base `d930612`.

## Global Constraints

- **The app's promise is the spec.** Its error copy tells users: *"Nothing was deleted — OrdoSort only ever moves files, so the document is either where it started or where it was going."* A failed undo must leave the document exactly where it was, and the audit log must not claim otherwise.
- **The gate command is NOT plain `dotnet test`.** Smart App Control blocks the WPF test assembly by hash; when it does, `dotnet test` **silently skips the entire WPF suite and still exits 0**. Always run:
  ```bash
  dotnet build OrdoSort.sln -t:Rebuild -p:Deterministic=false -v minimal
  dotnet test OrdoSort.sln --no-build -v minimal
  ```
  **Baseline: Core 380 + Wpf 537 = 917 green.** A WPF line that is missing, reports a skip, or reports a much smaller number means the suite did not run.
- A stray `OrdoSort.exe` or leftover `dotnet.exe` MSBuild node breaks rebuilds — `tasklist | findstr OrdoSort` first.
- **Do not enshrine a bug in a test.** These branches have never been exercised. If a branch turns out to behave wrongly, the test asserts what it *should* do and the code is fixed — writing a test that locks in current-but-wrong behaviour is worse than no test.
- **The lesson that has now held seven times here, twice on the last branch alone:** every fix round came from *an untested branch carrying the entire safety argument*. This whole task is that lesson applied deliberately — but it applies recursively too. Ask what fails if the guard is deleted.
- Never `--no-verify`, never force, **never push**.

## File Structure

| File | Responsibility | Task |
|---|---|---|
| `tests/OrdoSort.Core.Tests/UndoFailureTests.cs` | NEW — the three branches and the state they must preserve | 1 |
| `src/OrdoSort.Core/Commit.cs` | possibly `UndoAction`'s `FileExistsRace` handling — decided by Step 5 | 1 |
| `docs/superpowers/audits/2026-08-04-full-audit.md` | mark 1.5 fixed | 2 |

---

### Task 1: Exercise the three branches, and settle the race leak

**The code under test** (`src/OrdoSort.Core/Commit.cs:90-101`):

```csharp
public static void UndoAction(string filedPath, string originalPath)
{
    if (!File.Exists(filedPath))
        throw new CommitError($"Can't undo: {Path.GetFileName(filedPath)} is no longer there");
    if (File.Exists(originalPath))
        throw new CommitError($"Can't undo: {Path.GetFileName(originalPath)} already exists again");
    var parent = Path.GetDirectoryName(originalPath);
    if (parent is null || !Directory.Exists(parent))
        throw new CommitError($"Can't undo: inbox folder is gone: {parent}");

    MoveNeverOverwrite(filedPath, originalPath);
}
```

**Files:**
- Create: `tests/OrdoSort.Core.Tests/UndoFailureTests.cs`
- Possibly modify: `src/OrdoSort.Core/Commit.cs` (Step 5 only, and only if Step 5's measurement says so)

- [ ] **Step 1: Read the caller before writing anything.** `Session.UndoLast()` (`src/OrdoSort.Core/Session.cs:126-148`) calls `Commit.UndoAction` **first** and mutates state only after it returns — `_undo.RemoveLast()`, the `Filed`/`Skipped` counters, `Pos`, and `_history.MarkReverted`. Confirm that ordering yourself. It means a failed undo should leave every one of those untouched. **That is the property worth pinning**, and no test covers it today.

- [ ] **Step 2: Write the failing tests.** Create `tests/OrdoSort.Core.Tests/UndoFailureTests.cs`. Cover each branch at the `Commit` level *and* the surviving-state property at the `Session` level. Follow the conventions of the existing Core tests — read `tests/OrdoSort.Core.Tests/` for how a `Session` is built with a temp inbox and a real `History` before writing against it; do not invent a fixture shape.

The tests must assert, for **each** of the three branches:

1. `CommitError` is thrown (not some other exception type), and its message names the specific problem — a user reading it should know which of the three situations they are in.
2. **The filed copy is still exactly where it was.** This is `UndoAction`'s documented promise and the thing the user actually depends on.
3. **Nothing else moved or vanished** — assert the directory contents, not just the one file.

And at the `Session.UndoLast()` level, for at least one branch:

4. After the failure: the undo stack still has its entry, `Filed`/`Skipped` are unchanged, `Pos` is unchanged, and — most important — **the history row is NOT marked reverted**. A log that says "reverted" while the document is still filed would break the app's central promise in the most damaging way available.

Constructing the three situations:
- *filed file gone*: commit a document, then delete the filed copy behind the app's back.
- *original name reused*: commit a document, then recreate a file at the original path.
- *inbox folder vanished*: commit a document, then delete the inbox directory.

- [ ] **Step 3: Run — all MUST FAIL** (the file does not exist yet). Paste the output.

- [ ] **Step 4: Confirm they pass against the current code**, and that they pass *for the right reason* — read each failure message rather than trusting the assertion count. If any branch does something other than what Step 2 asserts, **stop and report it**: that is a real defect, and the plan's Global Constraints say the code is fixed rather than the test relaxed. Say which branch and what it did.

- [ ] **Step 5: Settle the `FileExistsRace` leak by measurement, then decide.**

`MoveNeverOverwrite` (`Commit.cs:18-31`) throws a **private nested** `FileExistsRace` when the target exists at the last instant. `CommitFile` catches it at `:57` and `:62` and retries. **`UndoAction` does not catch it at all.**

So if the original filename reappears in the window between the guard at `:94` and the move at `:100`, a private exception type escapes the `OrdoSort.Core` assembly. Trace what the user then sees: `Session.UndoLast` does not catch it, and `ShellViewModel`'s undo command routes errors to `ReportUnexpected` — so instead of the actionable *"already exists again"* message its own sibling guard produces two lines earlier, the user gets the generic "Undoing the last filing didn't finish" plus a crash.log entry.

Write a test that forces it if you can (the window is small; a seam or a deliberately-crafted ordering may be needed). **If you cannot force it deterministically, say so plainly rather than shipping a timing-dependent test** — this codebase has a documented history of tests that pass either way.

Then decide, and record the decision with its reasoning:
- **Recommended:** catch `FileExistsRace` in `UndoAction` and rethrow it as the same `CommitError` the `:94` guard produces. It is the identical situation — the original name is occupied — merely detected a few microseconds later, and the user deserves the same actionable message. Small, contained, and it stops a private type leaking out of the assembly.
- **Or:** judge the window too small to matter and leave it, recording *why*, so the next reader does not re-derive the whole trace.

Either way this is a decision with evidence, not a silent omission.

- [ ] **Step 6: Full suites green.**

```bash
dotnet build OrdoSort.sln -t:Rebuild -p:Deterministic=false -v minimal
dotnet test OrdoSort.sln --no-build -v minimal
```

Expected: Core **380 + your new tests**, Wpf 537. State the exact number you added.

- [ ] **Step 7: Prove teeth.** For each of the three branches, delete its guard from `UndoAction`, rebuild, and confirm the matching test fails. Restore after each. Three separate proofs — a single "I deleted one and something failed" is not evidence for the other two. Paste the output.

**Watch for a trap:** deleting the `:92` guard (`!File.Exists(filedPath)`) may still fail the test, but via `File.Move` throwing rather than the assertion catching a wrong-but-plausible outcome. Read *why* each test failed, and say so — "it failed" is not the standard; "it failed because X" is.

- [ ] **Step 8: Commit** `test(core): pin undo's three failure branches and the state they must preserve`.

Record in the body: what each branch guarantees, the `FileExistsRace` decision and its reasoning, and — if Step 4 found one — any behaviour you had to fix rather than enshrine.

---

### Task 2: Gate and record

- [ ] **Step 1: Release build and full suites.**

```bash
dotnet build OrdoSort.sln -c Release -t:Rebuild -p:Deterministic=false -v minimal
dotnet test OrdoSort.sln -c Release --no-build -v minimal
```

Record both totals.

- [ ] **Step 2: Smoke.** `dotnet run --project tools/OrdoSort.Smoke -- demo-full` ends `All checks passed`, exit 0. It exercises real filing and a real undo against real files, so it is the direct check that Task 1 did not disturb the *success* path while testing the failure paths.

- [ ] **Step 3: Update the audit document.** In `docs/superpowers/audits/2026-08-04-full-audit.md`, mark finding **1.5** fixed with the commit SHA, in the style the already-fixed findings use. Record what the tests actually pin — the state-preservation property, not merely "it throws" — and the `FileExistsRace` decision from Task 1 Step 5. If Task 1 found and fixed a real defect, say so prominently: that would make this finding more than a test gap. Correct the "What to fix, in order" list (item 8). Commit `docs: mark undo's failure branches covered`.

- [ ] **Step 4: Report, do not push.**

## Model assignments

| Task | Implementer | Review |
|---|---|---|
| 1 Tests + race decision | sonnet (judgement on the leak; must not enshrine a bug) | sonnet |
| 2 Gate | haiku (mechanical: two commands, one doc edit) | sonnet |
