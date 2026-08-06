# Security and Unlock Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Close audit findings 4.2, 4.3, 1.2 and 1.3, and settle 1.4 by record. These are the last places where the app's own promise — *"OrdoSort only ever moves files, so the document is either where it started or where it was going"* — is not literally true, plus the two remaining local-privilege exposures.

**Architecture:** Four independent fixes in `OrdoSort.Core` and `UnlockViewModel`, then a gate. They are separate tasks because a reviewer could reject any one while approving the others, and because two of them (4.3 and 4.2) can change what an existing user's config does — a class of risk worth its own gate each.

**Tech Stack:** C# / .NET 8, WPF, xUnit. Repo `S:\OrdoSort`, branch `main`, base `512b999`.

## Global Constraints

- **The app's promise is the spec.** No change may weaken *"only ever moves files… either where it started or where it was going"*, and where a fix cannot make that literally true, it must say so in a comment rather than leave a reader to assume.
- **Decisions already taken (do not re-open):** v1.0 ships on **.NET 8**; migration to a newer runtime is a separate change. **No `LICENSE` file** is added — the user will decide that later. A `THIRD-PARTY-NOTICES` file is a separate program's work, not this one's.
- **The gate command is NOT plain `dotnet test`.** Smart App Control blocks the WPF test assembly by hash; when it does, `dotnet test` **silently skips the entire WPF suite and still exits 0**. Always run:
  ```bash
  dotnet build OrdoSort.sln -t:Rebuild -p:Deterministic=false -v minimal
  dotnet test OrdoSort.sln --no-build -v minimal
  ```
  **Baseline: Core 390 + Wpf 546 = 936 green.**
- A stray `OrdoSort.exe` or leftover `dotnet.exe` MSBuild node breaks rebuilds — `tasklist | findstr OrdoSort` first.
- **`WaitFor` hazard, learned on the previous branch:** a scheduler `calls==N` counter increments at *schedule* time, before the apply runs. Only post-apply state (`Preview.Count`, etc.) can safely guard an index. Do not write `WaitFor(calls==N)` then index a collection.
- **The pattern is at ten instances.** The safety argument of each task below is stated explicitly. Ask what fails if the guard is deleted — not whether the feature still works.
- Never `--no-verify`, never force, **never push**.

---

### Task 1: Side files cannot be written outside the config's folder

**Audit finding 4.2 [A].** `Config.ResolveBeside` (`Config.cs:256-259`) returns `sectionPath` verbatim when `Path.IsPathRooted(sectionPath)`. `Save`/`TrySave` then writes JSON to it unconditionally. On the shared-config deployment this app explicitly supports, anyone who can write `config.json` on the share gets a file of their choosing overwritten **on every other station's local disk** at the next settings save. That is a share-write to local-arbitrary-write escalation.

The four keys are `destinations_file`, `monitored_folders_file`, `alerts_file`, `box_labels_file`. They are *side files*, designed to sit beside the config.

**Files:**
- Modify: `src/OrdoSort.Core/Config.cs`
- Create: `tests/OrdoSort.Core.Tests/SideFilePathConfinementTests.cs`

- [ ] **Step 1: Verify-then-decide — can the UI even produce an absolute side-file path?** Look at the Settings "Data files" section. If it offers a file browser for these four keys, then absolute paths are a *shipped capability* and rejecting them outright is a breaking change; if it only offers a filename, confinement costs nothing. **Report what you found — it decides Step 3's shape.**

- [ ] **Step 2: Write the failing tests.** Assert that a side-file path which escapes the config's directory is refused, by `Save` and by load: an absolute path (`C:\Windows\Temp\evil.json`), a traversal (`..\..\evil.json`), and — on Windows — a rooted-without-drive path (`\evil.json`). Assert a plain filename and a *nested relative* path (`data\destinations.json`) still work, because forbidding those would be over-tight.

- [ ] **Step 3: Implement confinement.** Resolve the candidate with `Path.GetFullPath` and confirm it is inside the config's directory before use. Reject with a `ConfigException` naming the key and the offending value — silently ignoring would relocate a user's data without telling them.

  **If Step 1 found the UI does offer absolute paths**, do not silently break those users: reject on *write* (the exposure) while continuing to *read* an existing absolute path, and say clearly in your report that you split it that way and why.

- [ ] **Step 4: Full suites green.** Expected: Core 390 + your tests, Wpf 546.

- [ ] **Step 5: Prove teeth.** Remove the confinement check, rebuild, confirm the escape tests fail. Restore. Paste it.

- [ ] **Step 6: Commit** `fix(config): side files stay beside the config`.

---

### Task 2: Legacy plaintext passwords are protected on load, not only on save

**Audit finding 4.3 [A].** `ReprotectLegacyPlaintext` (`UnlockViewModel.cs:224-231`) has exactly three callers (`:170`, `:196`, `:210`), all on save paths. **Nothing sweeps at load.** A hand-edited or legacy plaintext entry stays plaintext in `config.json` forever if the saved-password list is never touched.

**There is a real consequence to get right, and it is why this is its own task.** DPAPI here is `CurrentUser` scope with null entropy — which is the correct choice, do **not** change it to `LocalMachine`. That means protecting an entry makes it readable only by the Windows account that protected it. On a shared config, sweeping plaintext converts an entry every station could read into one only the sweeping user can. That is the *point* — plaintext readable by everyone is the exposure — but the user must be told rather than silently losing a shared password.

**Files:**
- Modify: `src/OrdoSort.Wpf/ViewModels/UnlockViewModel.cs` (and `PasswordVault` only if needed)
- Create/extend: a test file for the sweep

- [ ] **Step 1: Write the failing test.** Load a config containing a plaintext saved password; assert that after load it is protected on disk (`PasswordVault.IsProtected` true) and that `Reveal` still returns the original secret to the user who swept it. Add a test that an already-protected entry is left byte-identical — a re-protect churns the config for nothing and would rewrite a shared file on every open.

- [ ] **Step 2: Run — MUST FAIL.** Paste it.

- [ ] **Step 3: Implement the load-time sweep**, and **persist it** — protecting in memory only would leave the plaintext on disk, which is the whole finding. Reuse `ReprotectLegacyPlaintext` rather than writing a second implementation.

- [ ] **Step 4: Tell the user when it happens.** If any entry was converted, surface a one-time, plainly-worded note: their saved passwords are now protected for this Windows account, and a colleague on another machine will need to re-enter theirs. Do not bury it in a log. Use the existing `IDialogService` — check what it offers before adding anything.

- [ ] **Step 5: Full suites green. Step 6: Prove teeth** — remove the load-time call, confirm the test fails because the on-disk value is still plaintext. Restore, paste it.

- [ ] **Step 7: Commit** `fix(unlock): legacy plaintext passwords are protected when the config loads`.

---

### Task 3: Unlock can no longer overwrite or delete a file it did not create

**Audit finding 1.2 [V].** Across all of `OrdoSort.Core` there are exactly three places that can delete or overwrite: a self-created write probe (benign), backup pruning, and `Unlock`. Two specific gaps:

- `Unlock.cs:146` — the buffered path's `place` is `target => File.WriteAllBytes(target, unlockedBytes)`. `target` came from `CollisionFree(...)`, so it was free *at check time*; `WriteAllBytes` **truncates whatever is there now**. On the shared folders this app targets, another station can create that exact name in the gap.
- `Unlock.cs:250` (and `:271`, `:277`) — the failure paths call `RemoveQuietly(target)`, deleting `target` **even if this call never created it**.

Note the large-file path already moves from a local temp, and `File.Move` does not overwrite — so only the buffered path has the truncate. Verify that before relying on it.

**Files:**
- Modify: `src/OrdoSort.Core/Unlock.cs`
- Create: `tests/OrdoSort.Core.Tests/UnlockNeverOverwritesTests.cs`

- [ ] **Step 1: Write the failing tests.** (a) With a file already present at the name `CollisionFree` would pick, the buffered unlock must **not** truncate it — assert the pre-existing content survives byte-for-byte. (b) When `place` fails and `target` was **not** created by this call, the pre-existing file must still be there afterwards. Forcing (a) deterministically needs a seam; this repo has precedent — `Commit.RaceHookForTests` (`Commit.cs`) does exactly this for the same class of race. Follow that shape and keep the seam `internal`.

- [ ] **Step 2: Run — MUST FAIL.** Paste it.

- [ ] **Step 3: Implement create-only semantics.** Replace the buffered `File.WriteAllBytes` with an exclusive create — `new FileStream(target, FileMode.CreateNew, …)` — which fails atomically if the name is taken rather than truncating. Then gate every `RemoveQuietly(target)` on *this call having created it*. The precedent is `Config.WriteAtomicNew`, added earlier in this codebase for the identical problem; read it before writing.

- [ ] **Step 4: Full suites green. Step 5: Prove teeth** — revert to `WriteAllBytes`, confirm test (a) fails **because the pre-existing content was destroyed**, not for an incidental reason. Restore, paste it.

- [ ] **Step 6: Commit** `fix(unlock): never truncate or delete a file this call didn't create`.

---

### Task 4: An interrupted in-place unlock can't re-enter the queue, and says what it left

**Audit finding 1.3 [A].** `PlaceAndSwap`'s in-place path does two moves — `File.Move(src, archived)` at `Unlock.cs:267`, then `File.Move(target, src)` at `:284`. A crash or power loss between them leaves `X.unlocking.pdf` plus the archived original, and the document is in **a third place the reassurance copy never names**.

Two moves cannot be made atomic, so this task does not pretend to eliminate the window. It closes the half that is fixable and documents the half that is not:

- **The intermediate is named `.unlocking.pdf`** (`:241`), so it matches a `*.pdf` watch/inbox filter and can be picked up as a document to file — turning a crash into a *spurious document in the user's queue*. That is fixable outright: give the intermediate an extension the scanner does not match.
- **The residual window** gets an honest comment naming the three possible on-disk states after an interrupted in-place unlock.

**Files:**
- Modify: `src/OrdoSort.Core/Unlock.cs`
- Extend: an existing Unlock test file

- [ ] **Step 1: Confirm the pickup is real before fixing it.** Check what the inbox scanner and `FolderMonitor.ParseFiletypes` actually match. If `X.unlocking.pdf` would *not* be picked up, say so and skip the rename — do not make a cosmetic change and call it a fix.

- [ ] **Step 2: Write the failing test.** Assert that the intermediate name used during an in-place swap is not one the scanner would treat as a document. Assert the successful path still ends with the unlocked file at the original name and the original archived — the rename must not change the outcome.

- [ ] **Step 3: Implement**, if Step 1 confirmed it. Change the intermediate's extension only; the final `File.Move(target, src)` restores the real name, so nothing downstream sees the temporary one.

- [ ] **Step 4: Record finding 1.4 rather than fixing it.** `Commit.cs:24,44-47` — an interrupted cross-volume `File.Move` (copy-then-delete) can leave a partial file at the destination; on retry the collision counter gives the *real* document the `" (2)"` suffix while the partial keeps the canonical name. Whether Win32 `MoveFileEx` cleans up its own partial on a non-crash failure is **[U]** — unproven, and proving it needs a disk-full or kill test across two volumes. **Do not attempt the fix.** Add a comment at `Commit.cs` naming the window and pointing at audit finding 1.4, so the next reader does not re-derive it.

- [ ] **Step 5: Full suites green. Step 6: Commit** `fix(unlock): an interrupted swap can't leave a document in the queue`.

---

### Task 5: Gate and record

- [ ] **Step 1: Release build and full suites.**
```bash
dotnet build OrdoSort.sln -c Release -t:Rebuild -p:Deterministic=false -v minimal
dotnet test OrdoSort.sln -c Release --no-build -v minimal
```

- [ ] **Step 2: Smoke.** `dotnet run --project tools/OrdoSort.Smoke -- demo-full` ends `All checks passed`, exit 0.

- [ ] **Step 3: Launch sanity, exercising the unlock path.** Launch Debug with `--config demo-full\config.json`, open **Unlock**, and run it against a demo PDF. Confirm it completes and the folder is left as expected. Then `Stop-Process` and confirm none remains. If the demo corpus has no encrypted PDF, say so rather than claiming a check you could not perform.

- [ ] **Step 4: Update the audit document.** Mark **4.2**, **4.3**, **1.2** and **1.3** fixed with their SHAs, in the established style. For **1.4**, record that it was deliberately left with a code comment and why (needs dynamic proof). Correct the "What to fix, in order" list. Commit `docs: mark the security and unlock findings done`.

- [ ] **Step 5: Report, do not push.**

## Model assignments

| Task | Implementer | Review |
|---|---|---|
| 1 Side-file confinement | sonnet (verify-then-decide on the UI) | sonnet |
| 2 Plaintext sweep | sonnet (shared-config consequence) | sonnet |
| 3 Unlock create-only | sonnet | sonnet |
| 4 Intermediate name + 1.4 record | sonnet | sonnet |
| 5 Gate | sonnet | — |
