# Portable Saved Passwords Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Saved Unlock passwords work from a shared `config.json` on a network share, on every station, with no per-account or per-machine coupling and no dialog on opening the Unlock window.

**Reported by the owner, verbatim:** *"when i first open unlock pdfs, i get a dialog box saying that the passwords are tied to my account. i dont want this. the saved passwords should not be tied to a windows account, it should be accessible from a network share"*

## The decision, and why the obvious alternative was rejected

**DPAPI cannot satisfy this requirement in either scope.** `CurrentUser` binds the blob to one Windows account; `LocalMachine` binds it to one PC and additionally lets *any* account on that PC read it. Neither travels between stations sharing a `config.json`. Portability requires leaving DPAPI, not reconfiguring it.

**Owner's decision (do not re-open): store saved passwords in plaintext, with the share's own permissions as the security boundary.** Anyone who can read `config.json` can read the passwords; that is understood and accepted. The alternative offered — a passphrase entered once per station — was declined.

**This supersedes audit finding 4.3**, which reads *"DPAPI scope `CurrentUser` with null entropy is the correct choice here — do not 'fix' it to `LocalMachine`."* That ruling was correct for a single-station deployment and is wrong for this one. It must be updated, not silently contradicted — see Task 3.

**Tech Stack:** C# / .NET 8, WPF, xUnit. Repo `S:\OrdoSort`, branch `main`, base `da95b84`.

## Current state

| Piece | Where | Behaviour today |
|---|---|---|
| Storage | `Core/Config.cs:140` | `saved_passwords` lives in the **main `config.json`** — the file on the SMB share, read by every station. |
| Protect | `Wpf/Services/PasswordVault.cs` | `Protect` = DPAPI `CurrentUser`; `Reveal` returns `""` for anything it can't decrypt. |
| Forward sweep | `Wpf/ViewModels/UnlockViewModel.cs:214-257` | On **every** Unlock-window open, converts plaintext → protected, saves, and shows the complained-of dialog. Its own comment concedes it "silently breaks a shared password for a colleague on another machine". |
| Save paths | `UnlockViewModel` (three call sites) | Protect before persisting. |
| UI copy | `Windows/ManageSavedWindow.xaml` | Says "Encrypted for this Windows account". |

## Global Constraints

- **Do not rewrite the shared `config.json` on every window open.** `UnlockViewModel.cs:222-229` explains why at length: this constructor runs on every open, and an unconditional save is a real, conflict-prone write on an SMB share that can clobber a peer. Any migration must be **gated on having actually changed something**, exactly as the forward sweep was. `UnlockViewModelTests.LoadDoesNotRewriteAnAlreadyProtectedConfig` guards this — keep an equivalent guard.
- **Existing tests encode the behaviour being reversed.** `PasswordVaultTests`, the sweep tests in `UnlockViewModelTests`, and anything asserting "no plaintext in config" now assert the *opposite* of the intended design. **Update them deliberately, never delete them, and name every one you changed and why.** A test that asserted plaintext was absent is not obsolete — it should become a test that plaintext is present *and intended*.
- **Losing a password is the one unacceptable outcome.** Migration reads secrets that only decrypt on one machine+account. A bug here destroys data the user cannot recover.
- **The pattern is at twelve.** A test that proves a value round-trips is not a test that proves it is stored the way you think. Assert the **on-disk bytes**, not just the in-memory value.
- **The gate command is NOT plain `dotnet test`.** Smart App Control blocks the WPF test assembly by hash; `dotnet test` alone **silently skips the entire WPF suite and still exits 0**:
  ```bash
  dotnet build OrdoSort.sln -t:Rebuild -p:Deterministic=false -v minimal
  dotnet test OrdoSort.sln --no-build -v minimal
  ```
  **Baseline: Core 444 + Wpf 636 = 1080 green.** Core.Tests takes ~56s by design.
- **Two environment-sensitive suites — report, never chase, never weaken:** `FocusRingCoverageTests.TabItemShowsTheBronzeFocusRing`, and `WebViewPdfViewerGuardBehaviourTests` (all 5 fail together with COM `Class not registered`, pass on re-run).
- Never `--no-verify`, never force, **never push**.

---

### Task 1: Stop protecting, and migrate what is already protected

**Files:** `src/OrdoSort.Wpf/Services/PasswordVault.cs`, `src/OrdoSort.Wpf/ViewModels/UnlockViewModel.cs`, tests.

- [ ] **Step 1: Save plaintext.** New and edited saved passwords persist as plaintext. Remove the protect-on-save behaviour from all three save paths.

- [ ] **Step 2: Keep the ability to *read* a protected value.** Existing configs contain DPAPI blobs. `Reveal` must still decrypt them on the machine and account that produced them — this is the only route by which the owner's current passwords survive at all. Do not delete that code.

- [ ] **Step 3: Reverse the sweep.** Replace the plaintext→protected sweep with protected→plaintext, so a config opened on the machine that owns the blobs becomes portable for every other station.

  **Gate the save on having actually converted something** (constraint above). **An entry that cannot be decrypted must not be silently blanked** — today `Reveal` returns `""` for that case, which would quietly destroy a password. Leave it untouched and report it.

- [ ] **Step 4: Delete the dialog.** The one-time "protected for this Windows account" notice is the owner's actual complaint. It goes. Decide deliberately whether the migration needs *any* notice — a silent, successful conversion needs none; **entries that could not be decrypted do need telling, once**, because those are passwords the user must re-enter. Record your choice.

- [ ] **Step 5: Tests.** Assert the **on-disk JSON**, not just in-memory round-tripping: a newly saved password appears in plaintext in the file; a protected entry from the current account is converted on load; a config already fully plaintext is **not** re-saved; an undecryptable entry survives untouched rather than becoming `""`.

- [ ] **Step 6: Prove teeth.** Restore protect-on-save and confirm the on-disk plaintext test fails **because the file contains a `dpapi:` blob**. Separately, make the migration ungated and confirm the no-rewrite test fails. **Two proofs**, and say why each failed.

- [ ] **Step 7: Commit** `feat(unlock): saved passwords are portable across stations`.

---

### Task 2: Tell the truth in the UI

**Files:** `src/OrdoSort.Wpf/Windows/ManageSavedWindow.xaml`, any related copy.

- [ ] **Step 1: Replace "Encrypted for this Windows account".** It will be false. The replacement must be accurate and non-alarming: these are stored in the shared config, and whoever can open that folder can read them. **Do not editorialise or warn repeatedly** — state it once, plainly, where someone adding a password will see it.

- [ ] **Step 2: Search for every other place this is described** — dialogs, tooltips, help text, README. A stale reassurance about encryption is worse than no reassurance. Report each one found and what it now says.

- [ ] **Step 3: Verify off-screen in both palettes** — this session cannot drive the real UI (screen capture black, input injection denied); use the WPF suite's STA fixture. Confirm the new copy fits its container and does not clip. `UnlockWindow.xaml` has a documented history of a wrap-vs-`StackPanel` clipping bug — check yours actually wraps.

- [ ] **Step 4: Commit** `docs(unlock): say plainly where saved passwords are stored`.

---

### Task 3: Correct the record, then gate

- [ ] **Step 1: Update audit finding 4.3** in `docs/superpowers/audits/2026-08-04-full-audit.md:186`. Do **not** delete the old ruling — mark it superseded, state the deployment requirement that changed it, and record that `LocalMachine` was considered and rejected as *not solving the problem* (it is per-machine, so it fails on a share) rather than merely being less safe. The next reader must not re-litigate this.

- [ ] **Step 2: Release build and full suites.**
```bash
dotnet build OrdoSort.sln -c Release -t:Rebuild -p:Deterministic=false -v minimal
dotnet test OrdoSort.sln -c Release --no-build -v minimal
```

- [ ] **Step 3: Smoke.** `dotnet run --project tools/OrdoSort.Smoke -- demo-full` ends `All checks passed`, exit 0.

- [ ] **Step 4: Walk the owner's scenario end to end.** Build a config whose saved passwords are DPAPI-protected under the current account, open the Unlock window, and confirm: **no dialog appears**, the passwords still work, and the on-disk `config.json` now holds plaintext that another station could use. Then re-open and confirm the file is **not** written a second time. **This is the acceptance evidence.**

- [ ] **Step 5: Report, do not push.**

## Model assignments

| Task | Implementer | Review |
|---|---|---|
| 1 Storage + migration | sonnet | sonnet (read-only) |
| 2 UI copy | sonnet | — |
| 3 Record + gate | sonnet (read-only) | — |
