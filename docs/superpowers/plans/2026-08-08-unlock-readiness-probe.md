# Unlock Readiness Probe Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** When PDFs are dropped into the Unlock window, check each one and show, per file, whether a saved password already opens it — before the user clicks Unlock.

**Requested by the owner:** *"is it possible to auto check each pdf when a pdf is dragged into the unlock box and have text indicating that a required password is already saved"*

**Tech Stack:** C# / .NET 8, WPF, xUnit, PDFsharp. Repo `S:\OrdoSort`, branch `main`, base `0b01897`.

## Current state

| Piece | Where | Note |
|---|---|---|
| Drop → add | `Windows/UnlockWindow.xaml.cs:139-141` | `FileDrop` → `AddFilesAsync(paths)`. The hook already exists. |
| Add path | `ViewModels/UnlockViewModel.cs:358+` | Deliberately off the UI thread — `File.Exists` is a network round trip per path; dedupe is a `HashSet`, not `Contains`, because that was quadratic. **Preserve both.** |
| File list | `ViewModels/UnlockViewModel.cs:57` | `ObservableCollection<string>` — plain paths, no row type. |
| Encryption check | `Core/Unlock.cs:104-114`, `:176-189` | Open with **no** password: opens ⇒ not protected, throws ⇒ protected. The comment there records why opening *with* a password cannot answer this. |
| Candidate loop | `ViewModels/UnlockViewModel.cs:573-584` | `TryCandidates`: first success wins; any non-`wrong_password` status aborts immediately. |
| Candidate build | `ViewModels/UnlockViewModel.cs:433-451` | Typed password first, then each revealed saved password, skipping a duplicate of the typed one. Built once per run. |

## The four risks this plan exists to manage

1. **Probe cost.** PDFsharp's `Import` mode materialises the whole document; `Unlock.cs:163-170` says memory is proportional to content, which is why a streaming path exists at all. A 50-file drop against 5 saved passwords is up to 250 opens, possibly over a share, triggered by a gesture that today costs nothing.
2. **Probe/unlock divergence.** A file labelled ready that then fails is worse than no label. Note the two are *legitimately* different: `TryCandidates` tries the **typed** password first and at drop time there is none. The label must therefore claim only what the probe tested.
3. **Wrong password vs damaged file.** `Unlock.cs:127-134` separates `PdfReaderException` (wrong password) from every other exception (error), and `IsInUse(ex)` from both. A probe that collapses these will report a corrupt or open file as merely needing a password.
4. **Staleness.** A verdict that outlives the facts it was computed from is a lie. Saved passwords can change while the window is open.

## Global Constraints

- **The gate command is NOT plain `dotnet test`.** Smart App Control blocks the WPF test assembly by hash; when it does, `dotnet test` **silently skips the entire WPF suite and still exits 0**. Always:
  ```bash
  dotnet build OrdoSort.sln -t:Rebuild -p:Deterministic=false -v minimal
  dotnet test OrdoSort.sln --no-build -v minimal
  ```
  **Baseline: Core 426 + Wpf 614 = 1040 green.** Core.Tests takes ~56s by design — not a hang.
- **Two environment-sensitive suites — report the result, never chase, never weaken:** `FocusRingCoverageTests.TabItemShowsTheBronzeFocusRing`, and `WebViewPdfViewerGuardBehaviourTests` (all 5 fail together with COM `Class not registered` / wrong-state, and pass on a re-run).
- **This session cannot drive the real UI** — screen capture returns black, input injection is denied. Verify headlessly; the WPF suite builds real windows off-screen on a shared STA fixture (`[Collection(HighlightContrastTests.Name)]` + `HighlightContrastFixture`).
- **The probe must never write, move, or delete anything.** `Unlock`'s whole design is that a document is either where it started or where it was going. A read-only probe that creates a temp file is a bug even if it cleans up.
- **The pattern is at twelve.** A test that proves a probe *ran* is not a test that proves it was *right*. Ask what fails if the guard is deleted.
- Never `--no-verify`, never force, **never push**.

---

### Task 1: A read-only probe in Core

**Files:** `src/OrdoSort.Core/Unlock.cs`, `tests/OrdoSort.Core.Tests/`

- [ ] **Step 1: Add the probe.** A method that, given a path and an ordered candidate list, reports one of: **not encrypted**, **opens with candidate _i_**, **needs a password none of these supply**, **in use**, **unreadable**. It opens read-only and writes nothing anywhere — no temp file, no destination, no archive.

- [ ] **Step 2: Mirror the real unlock's exception discipline exactly.** `PdfReaderException` ⇒ wrong password (keep trying the next candidate); `IOException` where `IsInUse` ⇒ in use (stop); anything else ⇒ unreadable (stop). This is risk 3, and getting it wrong mislabels damaged files.

- [ ] **Step 3: Find the cheapest open mode that still answers correctly.** Try `PdfDocumentOpenMode.InformationOnly` for both the encryption check and the password check. **Verify by experiment, do not assume** — if a lighter mode fails to distinguish a wrong password from a right one, say so and use `Import`. **Report the measured cost either way** (time and peak memory for one encrypted file), because risk 1 is the reason this step exists.

- [ ] **Step 4: Test against real encrypted fixtures**, not mocks: not-encrypted, encrypted-and-first-candidate-matches, encrypted-and-later-candidate-matches, encrypted-and-none-match, damaged, empty candidate list.

- [ ] **Step 5: The agreement test — this is the one that matters.** For each fixture, assert the probe's verdict **matches what a real unlock actually does** with the same candidates. Probe says ready ⇒ unlock succeeds; probe says needs-a-password ⇒ unlock reports `wrong_password`. This is risk 2 written as a test, and it is the acceptance evidence for the whole task.

- [ ] **Step 6: Prove the probe wrote nothing.** Snapshot the fixture directory (names, sizes, mtimes) plus the temp directory before and after a probe run, and assert nothing changed. **Teeth:** make the probe write a temp file, confirm this test fails, revert.

- [ ] **Step 7: Commit** `feat(unlock): a read-only probe for whether a saved password opens a PDF`.

---

### Task 2: Per-file readiness in the Unlock window

**Files:** `src/OrdoSort.Wpf/ViewModels/UnlockViewModel.cs`, `src/OrdoSort.Wpf/Windows/UnlockWindow.xaml`, tests.

- [ ] **Step 1: Give the file list a row type.** `Files` is `ObservableCollection<string>` today; a per-file indicator needs a row carrying path + probe state, raising `PropertyChanged` when the state lands.

  **Walk every consumer before changing it** — `UnlockAsync`'s `Files.ToList()` at `:429` and its `paths[i]` indexing throughout, `ClearCommand`, the dedupe `HashSet`, `UnlockCommand`'s `Files.Count > 0`, and any test touching `Files`. Report the full list you found and what each needed.

- [ ] **Step 2: Probe newly added files only.** On add, probe just the new arrivals — never re-probe the whole list on every drop. Off the UI thread, gated for concurrency the way `UnlockAsync` gates its work, and cancellable: closing the window or clearing the list must not leave a probe running. Adding files while a probe is in flight must not corrupt either result.

- [ ] **Step 3: Say only what was tested.** The probe has no typed password, so the wording is *"a saved password opens this"* — **not** *"this will unlock"*. Distinguish at minimum: not protected / a saved password opens it / needs a password / couldn't be read. Keep the not-protected case quiet — it is not a problem and should not read like one.

- [ ] **Step 4: Handle staleness (risk 4).** A verdict must not outlive its inputs. Saved passwords change via `AddSavedPassword`, `RemoveSelectedSaved`, and the save banner — find every mutation point yourself rather than trusting this list. Decide deliberately between clearing verdicts and re-probing, and **record the choice and why**.

- [ ] **Step 5: The ItemTemplate trap.** `UnlockWindow.xaml:44-64` carries a comment explaining that an `ItemTemplate` assigns as a **local value**, outranking `Styles.xaml`'s `ListBoxItem` style, so its `TextBlock` must bind `Foreground` back to the ancestor `ListBoxItem` or selected rows render unreadable. **Any new element you add to that template hits the same trap.** Verify selected-row contrast in both palettes off-screen.

- [ ] **Step 6: Tests.** The probe runs on drop; the row shows the right state per outcome; a not-protected file doesn't nag; verdicts don't survive a saved-password change; a probe in flight doesn't corrupt a concurrent add; cancellation leaves nothing running. Assert **observable state**, not that a method was called.

- [ ] **Step 7: Full suites green.** Expected Core 426 + your Task 1 tests, Wpf 614 + yours. State the count and name every pre-existing assertion you changed.

- [ ] **Step 8: Prove teeth.** Break the probe→row wiring and confirm the readiness tests fail **because the state is wrong**, not because something didn't render. Paste it.

- [ ] **Step 9: Commit** `feat(unlock): show which dropped PDFs a saved password already opens`.

---

### Task 3: Gate

- [ ] **Step 1: Release build and full suites.**
```bash
dotnet build OrdoSort.sln -c Release -t:Rebuild -p:Deterministic=false -v minimal
dotnet test OrdoSort.sln -c Release --no-build -v minimal
```

- [ ] **Step 2: Smoke.** `dotnet run --project tools/OrdoSort.Smoke -- demo-full` ends `All checks passed`, exit 0.

- [ ] **Step 3: Drive the real window off-screen** with a mixed drop — not protected, opens with a saved password, needs one, damaged — and confirm each row reads correctly in **both palettes**, including selected-row contrast. **This is the acceptance evidence**; unit tests do not tell you whether the window reads right.

- [ ] **Step 4: Measure the drop cost** with the largest fixture set available and report wall-clock. If a realistic drop is slow enough to feel broken, that is a finding, not a footnote.

- [ ] **Step 5: Report, do not push.**

## Model assignments

| Task | Implementer | Review |
|---|---|---|
| 1 Core probe | sonnet | sonnet (read-only) |
| 2 UI wiring | sonnet | sonnet (read-only) |
| 3 Gate | sonnet (read-only) | — |
