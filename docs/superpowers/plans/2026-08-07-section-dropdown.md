# Section Drop-down Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Fix a user-reported defect in Settings → Monitored folders: the Section drop-down omits sections, and moving the last folder out of a section destroys it with no way back.

**Reported symptom, verbatim:** *"in the monitored folders list there are 3 sections with 1 folder each. when i select the first folder and use the Section dropdown, only the section below the first section is listed in the dropdown. when i move the first folder into the second section, the first section is removed."*

**Root cause, confirmed:** `SettingsViewModel.SectionChoices` (`:1721-1726`) filters with `!ReferenceEquals(w, SelectedWatch)` — deliberately listing only the sections used by the *other* folders. Sections have no independent existence: one exists only while some folder carries its name (`AddSection()` at `:1564` has to invent a placeholder "New folder" to create one). With one folder per section, the selected folder **is** what keeps its own section alive, so excluding it removes that section from its own drop-down; and moving the last folder out erases the section from the list, every drop-down, and any route back short of retyping the name.

**Decision taken by the product owner (do not re-open):** an emptied section **stays for the rest of the Settings session** — visible in the list and offered in the drop-down so folders can be moved back — and only disappears when OK is clicked with nothing in it. Not permanently persisted; sections stay derived at rest.

**Tech Stack:** C# / .NET 8, WPF, xUnit. Repo `S:\OrdoSort`, branch `main`, base `bbf70d7`.

## Global Constraints

- **The gate command is NOT plain `dotnet test`.** Smart App Control blocks the WPF test assembly by hash; when it does, `dotnet test` **silently skips the entire WPF suite and still exits 0**. Always:
  ```bash
  dotnet build OrdoSort.sln -t:Rebuild -p:Deterministic=false -v minimal
  dotnet test OrdoSort.sln --no-build -v minimal
  ```
  **Baseline: Core 426 + Wpf 579 = 1005 green.** Core.Tests takes ~53s by design (a 5,000-iteration concurrency test) — not a hang.
- **`FocusRingCoverageTests.TabItemShowsTheBronzeFocusRing` is environment-sensitive** — either result is fine, never chase or weaken it.
- **This session cannot drive the real UI** — screen capture returns black and input injection is denied. Verify headlessly; the WPF suite builds real windows off-screen on a shared STA fixture (`[Collection(HighlightContrastTests.Name)]` + `HighlightContrastFixture`), and `WatchListRowTemplateTests` shows the pattern for driving the real `SettingsWindow`.
- **`IsTextSearchEnabled="False"` on the Section combo is load-bearing** (`SettingsWindow.xaml:845`) — WPF's text search clobbered bound values, and it also suppresses an ItemsSource-swap reset. Do not turn it on.
- **A diagnosis left a file in the tree:** `tests/OrdoSort.Wpf.Tests/SectionDropdownReproTests.cs`, six passing scenarios asserting *correct* behaviour. Keep it and extend it rather than starting fresh; if any of its assertions contradict the fixed behaviour, update them deliberately and say which and why.
- **The pattern is at twelve.** Three of my own hypotheses about this bug were wrong and six headless tests passed against them — the tests exercised what was *assumed* fragile. Ask what fails if the guard is deleted, and prefer a test that reproduces the user's actual report.
- Never `--no-verify`, never force, **never push**.

---

### Task 1: Every section is listed, and an emptied one survives the session

**Files:**
- Modify: `src/OrdoSort.Wpf/ViewModels/SettingsViewModel.cs`
- Extend: `tests/OrdoSort.Wpf.Tests/SectionDropdownReproTests.cs`

- [ ] **Step 1: Write the failing test from the user's own words first.** Three sections, one folder each. Select the first folder and assert `SectionChoices` contains **all three** section names, including its own. This is the reported bug; it must fail now.

- [ ] **Step 2: Add the session-sticky test.** Move that folder into the second section, then assert: the first section is still present in the list, still offered in `SectionChoices`, and the folder can be moved back into it. Assert too that a section which never had members and was never created this session does **not** appear — sticky must mean "existed this session", not "anything ever typed".

- [ ] **Step 3: Run — both MUST FAIL.** Paste the output.

- [ ] **Step 4: Drop the exclusion.** Remove `!ReferenceEquals(w, SelectedWatch)` from `SectionChoices` so every section in use is offered, the selected folder's included. Update the doc comment — it currently says "OTHER monitored folders", which is the bug written down as intent.

- [ ] **Step 5: Make an emptied section sticky for the session.** Track the section names seen this session (case-insensitive, first-seen casing wins — that rule already governs `RebuildWatchRows`). `RebuildWatchRows` should emit a header for every sticky name even when it has no members, and `SectionChoices` should offer them. Keep an emptied section in its existing position rather than moving it to the end.

  **Points to decide deliberately and record:** what seeds the sticky set (the sections present when Settings opened, plus any created or typed since); whether `AddSection()` still needs to invent a placeholder "New folder" once empty sections can exist on their own — if it doesn't, that is a simplification, but check nothing else depends on that placeholder; and what a header rename does to the sticky entry.

- [ ] **Step 6: Confirm nothing persists that shouldn't.** Clicking OK with an empty section must not write a phantom entry into `monitored-folders.json` — sections stay derived at rest. Assert this on the built result, not just in the UI.

- [ ] **Step 7: Check the neighbours still work** — drag-and-drop onto a section (`DropWatch`), the header ✎ rename (`CommitSectionRename`, which rewrites every member's Section), the per-header ＋, and the default group's "always visible, pinned first when empty" rule. The default group already survives emptiness; make sure sticky sections don't disturb its ordering.

- [ ] **Step 8: Full suites green.** Expected: Core 426, Wpf 579 + your new tests. State the count.

- [ ] **Step 9: Prove teeth.** Restore the `!ReferenceEquals` filter and confirm the Step 1 test fails; separately disable stickiness and confirm the Step 2 test fails. **Two proofs**, and say *why* each failed.

- [ ] **Step 10: Commit** `fix(settings): the Section drop-down lists every section, and an emptied one survives`.

---

### Task 2: Gate and record

- [ ] **Step 1: Release build and full suites.**
```bash
dotnet build OrdoSort.sln -c Release -t:Rebuild -p:Deterministic=false -v minimal
dotnet test OrdoSort.sln -c Release --no-build -v minimal
```

- [ ] **Step 2: Smoke.** `dotnet run --project tools/OrdoSort.Smoke -- demo-full` ends `All checks passed`, exit 0.

- [ ] **Step 3: Reproduce the user's scenario headlessly, end to end** — build a config with three sections of one folder each, drive the real `SettingsWindow`, and walk exactly the steps in the report. Record what the drop-down contains at each step. This is the acceptance evidence; a passing unit suite is not the same as walking the user's path.

- [ ] **Step 4: Record it.** Add the finding and its fix to `docs/superpowers/audits/2026-08-07-qc-dropdowns-and-file-connections.md` under the drop-down section — it belongs with D1–D4, and it is worth noting that a QC sweep of all 11 combos **missed** it while a user found it in minutes. Commit `docs: record the Section drop-down defect and fix`.

- [ ] **Step 5: Report, do not push.**

## Model assignments

| Task | Implementer | Review |
|---|---|---|
| 1 Section choices + sticky | sonnet | sonnet |
| 2 Gate | sonnet | — |
