# Audit Remediation Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

> Tasks 10–12 continue in 2026-08-03-audit-remediation-finish.md.

**Goal:** Fix everything the two UI audit passes found worth fixing — 3 critical correctness/threading defects, the platform and robustness gaps, the keyboard and copy issues, and the consistency drift — each proven by measurement and QC-reviewed before the next package starts.

**Architecture:** Twelve independently reviewable work packages ordered by risk: correctness first (layout defect, UI-thread I/O, error-channel bypass, disposal, invariant dates), then platform/robustness, then one-surface improvements, then keyboard/copy/consistency, then verify-then-decide items and minors. Each package ends green with its own tests.

**Tech Stack:** C# / .NET 8, WPF, xUnit. Repo `S:\OrdoSort`, branch `main`.

## Global Constraints

- **Decisions already taken (do not re-open):** invariant dates are **forward-only** (no rewriting of existing filenames or history rows, no schema change); High Contrast is **detect-and-step-aside** (no bespoke HC theme).
- Baseline **728** tests green (Core 359 + Wpf 369). Suites must stay green and grow with each behavioural fix.
- **Proof standard, established by these audits and non-negotiable:** demonstrate the failing state BEFORE the fix; **confirm the compiled assembly under test lacks the fix before trusting any "before" measurement** (a prior run produced a convincing false reading this way); find glyphs by scanning for max contrast, never a hand-picked coordinate; render both palettes where appearance is involved.
- **Harness rules:** replicate `SmokeUi.Boot`; re-apply `ShutdownMode` AFTER `InitializeComponent` or windows render 0x0; drain the dispatcher at `DispatcherPriority.Render` before reading or capturing; popups/drop-downs are separate HWNDs — render the popup's `Child`. Delete harnesses when done.
- **The precedence trap (bitten 5×, both directions):** a style Setter outranks INHERITANCE (bare/auto-wrapped `TextBlock` labels need a LOCAL `Foreground` bound to the container ancestor); a LOCAL value outranks a NAMED STYLE's Setter (never blanket-apply that remedy to a label already carrying `Style="{StaticResource SubtleText}"` — use `Style BasedOn` + a `DataTrigger` instead). `ControlTemplate.Resources` is measured non-functional for this trap.
- Do NOT run the smoke `screenshots` mode as a gate (always exits 1); it is fine as a rendering tool.
- Config keys, internal type names and the `routes` schema are NOT renamed by the copy work — user-facing text only.
- Commit per package; push only in Task 12.

---

### Task 1: Confirm, then fix, the collapsing DataGrid columns — DONE, 2026-08-02

**Files:** `src/OrdoSort.Wpf/Windows/HistoryWindow.xaml(.cs)`, `src/OrdoSort.Wpf/Windows/BulkRenameWindow.xaml(.cs)` · Test: `tests/OrdoSort.Wpf.Tests/`

Measured: both windows pin their `Width="*"` columns to `MinWidth` 20 on first layout (History: When 140 · **Original 20** · **Filed as 20** · Name 160 · Route 120 · Undone 70, 406px unused. BulkRename: **Current 20** · **New 20** · Note 220). Every ancestor reports correct width. Eight further `UpdateLayout()` passes changed nothing; a ±1px resize snapped them to 222px / 277px.

**Corrected disposition (Step 1 confirmed this, as directed):** a real,
interactively-shown window does NOT reproduce the collapse — on-screen, both
windows resolve to their genuine fair share (~222px/~277px), 0 and 5 rows
alike. The defect is confined to headless/off-screen rendering (this
project's own `Screenshots.cs` QA gallery, and this task's own regression
test) — no real user was ever affected. The audit record
(`docs/superpowers/audits/2026-08-02-ui-audit.md`) was updated to reflect
this: what was C1 (Critical) is now M10 (Minor) there. The fix still shipped
(an explicit `MinWidth="120"` floor) because it makes this project's own QA
screenshots trustworthy again, and because a follow-up measurement found it
also protects a real user who shrinks the History window to its own declared
minimum width. Full report:
`.superpowers/sdd/2026-08-02-audit-remediation/task-1-report.md`.

- [x] **Step 1: Confirm on-screen.** Launch the real app (`--config demo-full\config.json`), open History and Bulk rename, and observe whether the columns are collapsed in an interactively-shown window. Record the answer. **If they render correctly on-screen**, the defect is specific to `Show()`-without-resize and the fix target narrows — say so and adjust Step 3 accordingly rather than forcing the planned fix.

- [x] **Step 2: Write the failing test.** A headless test that constructs each window the way `Screenshots.cs` does, shows it off-screen, `UpdateLayout()`, and asserts each star column's `ActualWidth` is a fair share (> 100px), not `MinWidth`:

```csharp
[Theory]
[InlineData("History")]
[InlineData("BulkRename")]
public void StarColumnsGetTheirShareOfWidth(string window)
{
    // build the real window, show off-screen, UpdateLayout, then:
    foreach (var col in starColumns)
        Assert.True(col.ActualWidth > 100,
            $"{window}: star column '{col.Header}' is {col.ActualWidth}px (MinWidth is 20)");
}
```

- [x] **Step 3: Run it — it MUST FAIL** at 20px. Paste the output.

- [x] **Step 4: Implement.** Preferred fix in order of cleanliness: (a) give the star columns an explicit sensible `MinWidth` so even the unresolved pass is usable AND set `ColumnWidth`/explicit proportional widths that resolve on first pass; (b) if that does not hold, force one layout re-evaluation after the window is shown (e.g. re-assert widths on `ContentRendered`), which is what the ±1px nudge proved works. Do NOT ship a resize hack that visibly flickers — measure whichever you choose.

  Shipped (a)'s `MinWidth` half only: three separate code-behind attempts at
  the "resolve on first pass"/"force one layout re-evaluation" half (a column-
  Width toggle, and a genuine Window.Width nudge both in `Loaded` and
  post-Show, on- and off-screen) all measured as NOT actually re-resolving
  `ActualWidth` in-process, despite verifiably changing real layout geometry
  — see the Task 1 report for the measurements. Shipping only the proven part
  rather than a non-working trick was the deliberate call.

- [x] **Step 5: Test passes; run full suites** — `dotnet build OrdoSort.sln && dotnet test OrdoSort.sln -v minimal` → 730 (728 + 2 new), 0 failed.

- [x] **Step 6: Render proof.** Save `fixed-history-dark.png` and `fixed-bulkrename-dark.png` to the scratchpad root; confirm both star columns are wide and their headers legible.

- [x] **Step 7: Commit** `fix(ui): DataGrid star columns get their width on first layout`.

---

### Task 2: Get file I/O off the UI thread in Settings — DONE, 536eecd (+ 3820408)

**Files:** `src/OrdoSort.Wpf/ViewModels/SettingsViewModel.cs` · Test: `tests/OrdoSort.Wpf.Tests/SettingsViewModelTests.cs`

Three sites do synchronous I/O per keystroke: `:713-775` (live path notes — `Directory.Exists`/`File.Exists`/`Config.ReadDoc`), `:36` (`RouteEditVm.Problem` → `Config.ValidateRoute`, which **creates and deletes a real probe file**), `:206-213` (`WatchEditVm.Problem` → `Directory.Exists`). Against a slow or unreachable UNC path this freezes the window on every character.

- [x] **Step 1: Write the failing test.** Prove the current behaviour blocks: inject a path-checking seam whose probe blocks for ~300ms, set the bound property (as a keystroke would), and assert the setter returns promptly (e.g. < 50ms) — it will not today.

```csharp
[Fact]
public void TypingAPathDoesNotBlockOnTheProbe()
{
    // vm wired with a deliberately slow path-checker
    var sw = Stopwatch.StartNew();
    vm.Inbox = @"\\unreachable\share\inbox";
    sw.Stop();
    Assert.True(sw.ElapsedMilliseconds < 50,
        $"setting Inbox blocked for {sw.ElapsedMilliseconds}ms on the UI thread");
}
```

If `SettingsViewModel` has no seam for the filesystem check, introduce a minimal one (a `Func<string,bool>`/small interface defaulted to the real implementation) — that seam is part of this task.

- [x] **Step 2: Run it — MUST FAIL** (blocked for ~300ms). Paste the output.

- [x] **Step 3: Implement.** Debounce (~300ms after typing stops) and run the check off the dispatcher, following the pattern `ShellViewModel.cs:230-233,737-741` already uses. While a check is pending the note stays optimistic/neutral — never flash "does not exist" mid-typing. Ensure a later result cannot overwrite a newer one (drop stale results). `ValidateRoute`'s probe file must not run per keystroke at all.

- [x] **Step 4: Test passes.** Add a second test proving the note eventually reflects the result (pump/await the debounce), so the fix isn't "never check".

- [x] **Step 5: Full suites green.**

- [x] **Step 6: Commit** `fix(settings): path checks are debounced and off the UI thread`.

---

### Task 3: Route Enter through the command; make the history swap safe — DONE, 051d2ff

**Files:** `src/OrdoSort.Wpf/ViewModels/ShellViewModel.cs` · Test: `tests/OrdoSort.Wpf.Tests/`

Two defects in one file:
- `:963` — `OnEnter` calls `OnRouteAsync` directly, bypassing `AsyncRelayCommand`'s `OnError`. Clicking a destination is protected; pressing Enter — the app's primary gesture — is not.
- `:1057-1071` — `ApplySettingsAsync` disposes the old `History` before constructing the new one; if construction throws, `_history` references a disposed object and the fault is unobserved (fire-and-forget at `:1042`), silently breaking autocomplete, CSV export and the History window for the session.

- [x] **Step 1: Write both failing tests.** (a) Make a filing action throw and assert the error reaches the same channel a button press would (no unobserved exception, user sees the failure). (b) Make `new History(path)` throw and assert the shell still has a usable history afterwards and the user was told.

- [x] **Step 2: Run — both MUST FAIL.** Paste output.

- [x] **Step 3: Implement.** (a) `OnEnter` executes `RouteCommand` for the resolved index rather than calling `OnRouteAsync`. (b) Construct the new `History` FIRST; only dispose the old one once the new instance exists; on failure keep the old, report through the normal warning path, and leave `_history` valid.

- [x] **Step 4: Tests pass; full suites green.**

- [x] **Step 5: Commit** `fix(shell): Enter files through the command; history swap can't strand a disposed db`.

---

### Task 4: Invariant dates for anything written down — DONE, aa2a9f0

**Files:** `src/OrdoSort.Core/Unlock.cs:42`, `src/OrdoSort.Core/History.cs:72`, `src/OrdoSort.Core/BoxLabels.cs:217,220`, `src/OrdoSort.Wpf/ViewModels/BulkRenameViewModel.cs:128,188` · Test: `tests/OrdoSort.Core.Tests/`

Dates formatted with `CurrentCulture` reach filenames, folder names, printed labels and the audit log, so two stations with different locales produce different names for the same document. **Forward-only** — nothing already on disk is rewritten.

- [x] **Step 1: Write the failing test.** Run each formatting path under a deliberately different culture and assert the output is identical to the invariant one:

```csharp
[Theory]
[InlineData("de-DE")]
[InlineData("ja-JP")]     // non-Gregorian-influenced calendar/format
public void WrittenDatesAreCultureIndependent(string culture)
{
    var prev = CultureInfo.CurrentCulture;
    try
    {
        CultureInfo.CurrentCulture = new CultureInfo(culture);
        // exercise: locked_archive folder name, history timestamp,
        // box-label created/destroy strings, bulk-rename received-date stem
        Assert.Equal(expectedInvariant, actual);
    }
    finally { CultureInfo.CurrentCulture = prev; }
}
```

- [x] **Step 2: Run — MUST FAIL** under at least one culture. Paste output.

- [x] **Step 3: Implement.** `InvariantCulture` (or an explicit fixed pattern) at each of the six sites. **Display-only formatting stays culture-aware** — do not change dates shown in the History grid or status text unless they are also written. State in the commit which sites you judged "written" vs "displayed".

- [x] **Step 4: Tests pass; full suites green.**

- [x] **Step 5: Commit** `fix(core): dates written into names, labels and the log are culture-invariant`.

---

### Task 5: Viewer lifetime, init reporting, IME guard — DONE, 3e5c731 (+ b7b34ac)

**Files:** `src/OrdoSort.Wpf/Windows/TriageWindow.xaml.cs`, `src/OrdoSort.Wpf/Windows/MatchMergeWindow.xaml.cs`, `src/OrdoSort.Wpf/Views/ProcessingView.xaml.cs`

- [x] **Step 1: Implement three fixes.**
  (a) Dispose the per-review WebView2 on window close (`TriageWindow.xaml.cs:44-45`, `MatchMergeWindow.xaml.cs:41-43`) — currently one leaks per "Review matches" pass.
  (b) `TriageWindow.xaml.cs:63-67` — check `InitAsync()`'s bool as `MainWindow.xaml.cs:104-107` does, and tell the user when the viewer can't start instead of showing a blank pane.
  (c) `ProcessingView.xaml.cs:63-66` — ignore `Key.ImeProcessed` and in-progress composition before treating Enter as "file this document" (today a CJK user confirming a candidate files the document).

- [x] **Step 2: Test what is testable headlessly** — at minimum a test that Enter with `Key.ImeProcessed` does NOT commit, and one asserting the viewer is disposed on close. If the WebView2 disposal can't be asserted headlessly, say so explicitly rather than skipping silently.

- [x] **Step 3: Full suites green.**

- [x] **Step 4: Commit** `fix(ui): viewer disposal and init reporting; Enter ignores IME composition`.

---

### Task 6: DPI manifest + High Contrast step-aside — DONE, cedbfa2

**Files:** create `src/OrdoSort.Wpf/app.manifest`; modify `src/OrdoSort.Wpf/OrdoSort.Wpf.csproj`, `src/OrdoSort.Wpf/Theme/ThemeManager.cs`

- [x] **Step 1: DPI.** Add an `app.manifest` declaring per-monitor-v2 DPI awareness and reference it via `<ApplicationManifest>`. Today the app is DPI-unaware and bitmap-scales (blurry) at 125%/150%, the default on most current laptops.

- [x] **Step 2: High Contrast.** `ThemeManager.cs:79-88` currently overwrites `SystemColors.*` unconditionally. Consult `SystemParameters.HighContrast`: when true, skip the override entirely so the OS palette shows through, and re-evaluate when the setting changes (the same mechanism that already watches the light/dark preference). Per the approved decision this is step-aside, NOT a bespoke HC theme.

- [x] **Step 3: Test.** Assert the palette application is skipped when a High-Contrast flag seam reports true (introduce a tiny seam if `SystemParameters` can't be faked). Verify the manifest is actually embedded (inspect the built exe or assert the csproj property).

- [x] **Step 4: Launch sanity** — the app still starts and themes normally with HC off.

- [x] **Step 5: Full suites green. Commit** `feat(platform): per-monitor DPI awareness; step aside for High Contrast`.

---

### Task 7: History window — filtering, empty state, trimming — DONE, 10d5ac3

**Files:** `src/OrdoSort.Wpf/ViewModels/HistoryViewModel.cs`, `src/OrdoSort.Wpf/Windows/HistoryWindow.xaml` · Test: `tests/OrdoSort.Wpf.Tests/HistoryViewModelTests.cs`

- [x] **Step 1: Filtering.** `:97-108` currently does `Rows.Clear()` + per-item `Add()` on every Find keystroke; History is the unbounded collection. Replace with an `ICollectionView` filter (or equivalent) so typing doesn't re-materialise the list. Add a test asserting the row objects are not recreated per keystroke (e.g. same instances before/after a filter change).

- [x] **Step 2: Empty state.** The grid renders as a blank void today. Add a message consistent with the app's existing empty states (which echo their own button's wording) — e.g. "No filings recorded yet. Documents you file will appear here."

- [x] **Step 3: Trimming.** Add `TextTrimming="CharacterEllipsis"` (and tooltips carrying the full value) to the fixed-width `Name`/`Route` columns, which clip today.

- [x] **Step 4: Full suites green. Commit** `feat(history): filter without rebuilding, empty state, trimmed columns`.

---

### Task 8: Keyboard and accessibility — DONE, ad99128 (+ dddac41, cb50988)

**Files:** `src/OrdoSort.Wpf/Windows/UnlockWindow.xaml`, `src/OrdoSort.Wpf/Windows/SettingsWindow.xaml(.cs)`, `src/OrdoSort.Wpf/Theme/Styles.xaml`

- [x] **Step 1: Unlock — Enter does nothing.** `UnlockWindow.xaml:12-24`: give the Unlock button `IsDefault="True"` (or bind Return on the password box). Test: simulating Return with a password entered invokes unlock.

- [x] **Step 2: Settings — Esc is swallowed.** `SettingsWindow.xaml.cs:27-57` handles every key unconditionally (Tab exempted), so Esc records the hotkey "Escape" instead of closing the dialog. Let Escape through (and decide deliberately whether Escape should also cancel capture — state the choice).

- [x] **Step 3: Focus ring coverage.** `Styles.xaml:48,99` assigns `BronzeFocusVisual` only to Button/ToggleButton. Extend to CheckBox, RadioButton, ComboBox, ListBoxItem and TabItem, which currently show the OS dashed rectangle. Verify by rendering a focused instance of each in both palettes.

- [x] **Step 4: Names and mnemonics.** `AutomationProperties.Name` on the four ↑/↓ reorder buttons (`SettingsWindow.xaml:290-293,584-587`) and the ✎/＋ glyph buttons; access keys on the six Settings tab headers (both tab templates already set `RecognizesAccessKey="True"`, and no existing mnemonic may collide — check).

- [x] **Step 5: Full suites green. Commit** `feat(a11y): Enter in Unlock, Esc in Settings, focus ring coverage, control names`.

---

### Task 9: Copy and terminology — DONE, 8bdab38 (+ da84d2e, b837b84, 6ece125)

**Files:** `src/OrdoSort.Wpf/Windows/HistoryWindow.xaml`, `src/OrdoSort.Wpf/Windows/SettingsWindow.xaml`, `src/OrdoSort.Wpf/Views/ReadyView.xaml`, `src/OrdoSort.Wpf/ViewModels/ShellViewModel.cs`, `src/OrdoSort.Wpf/App.xaml.cs`

User-facing text only — config keys, `routes` schema and internal type names are unchanged.

- [x] **Step 1: One word per concept.** "Route" → "Destination" in user-facing text (`HistoryWindow.xaml:45`); reconcile the Settings tab titled "Dashboard" with its own section header and Data-files label so one name wins for monitored folders (`SettingsWindow.xaml:563,571,1211`). Sweep for other instances of each pair before editing so the change is complete.

- [x] **Step 2: The catch-all error.** `ShellViewModel.cs:96-98` and `App.xaml.cs:20-22` open with "Something went wrong" then dump raw exception text. Replace with a plain statement of what failed, that the file was left where it is, and where the detail went (crash.log) — matching the tone the filing loop already uses.

- [x] **Step 3: Sentence case.** "Start Processing" → "Start processing" (`ReadyView.xaml:145,149`), the only Title-Case button in the app.

- [x] **Step 4: Informational notes off the amber status colour** to `SubtleText` — amber elsewhere means needs-attention (Settings' "relative — resolved beside the config file" pair).

- [x] **Step 5: Full suites green** (update any test asserting the old strings). **Commit** `docs(ui): one word per concept; plain error copy; sentence case`.

---

### Task 10: Visual consistency

**Files:** `src/OrdoSort.Wpf/Windows/LabelMakerWindow.xaml`, `src/OrdoSort.Wpf/Theme/Styles.xaml`, and the ~20 `FontSize="11"` sites

- [ ] **Step 1: One primary per window.** `LabelMakerWindow.xaml:11` vs `:20` — "Print…" and "Save PDF…" are both weighted primary; pick one (Print… is the in-app path; state the reasoning) and demote the other.

- [ ] **Step 2: `FontSize="11"` → `CaptionText`** at the ~20 sites (`MatchMergeWindow.xaml:44,102,108`; `LabelMakerWindow.xaml:81,143,158,212`; `SettingsWindow.xaml:348,814,882,890,1163`; others — sweep). Where a site currently does `Style="SubtleText" FontSize="11"`, introduce/point at a proper style rather than stacking overrides.

- [ ] **Step 3: Extract `FieldRow`/`FieldLabel`** from `SettingsWindow.xaml:15-21` into `Theme/Styles.xaml` and point the four hand-rolling windows (BulkRename, LabelMaker, MatchMerge, Unlock) at the shared version. Keep each window's visual result unchanged — render before/after to prove it.

- [ ] **Step 4: Correct the documented spacing rhythm** in `Theme/Styles.xaml` to the practised 6/8/10/16 (`8` occurs 80×, more than `10`'s 62×).

- [ ] **Step 5: Full suites green. Commit** `refactor(ui): shared field-row style, caption sizing, one primary per window`.

---

### Task 11: Verify-then-decide, then the remaining minors

**Files:** as determined by Step 1

- [ ] **Step 1: Settle two open questions by measurement.**
  (a) `BulkRenameWindow.xaml:78` — the fifth "delete segment" checkbox renders with no visible label despite `Content="last"`. Measure the resolved foreground of that label; fix if it is the auto-wrap trap, or record it as a render artifact.
  (b) `TriageWindow` shows BOTH candidate rows in the selected treatment, which `SelectionMode="Single"` should forbid. Determine whether it is demo state or an alternating-row resolution bug; fix if real.

- [ ] **Step 2: Minors.** Ready-screen banner wrapping mid-phrase; Settings' General-tab dead space (decide: size-to-tab or accept, and record which); fixed 130px label columns vs the 6–72pt configurable text size; `UnlockWindow.xaml:135-136` `ItemsControl` virtualization; `RgbToBrushConverter.cs:11-12` per-call brush allocation (cache by `Rgb`).

- [ ] **Step 3: Pin the Appearance preview cards.** `SettingsWindow.xaml:997-1051` hand-picks ~22 hex colours to show both palettes at once — necessary, but they can drift from `ThemePalette`. Add a test asserting each preview swatch equals its `ThemePalette` counterpart.

- [ ] **Step 4: Full suites green. Commit** `fix(ui): audit minors and preview-card drift test`.

---

### Task 12: Full gate and push

- [ ] **Step 1:** `dotnet build OrdoSort.sln -c Release && dotnet test OrdoSort.sln -c Release -v minimal` — clean; record totals.
- [ ] **Step 2:** `dotnet run --project tools/OrdoSort.Smoke -- demo-full` — ends "All checks passed". (Do NOT run `screenshots` as a gate.)
- [ ] **Step 3:** Regenerate the gallery (`screenshots <outdir> both`, ignore its exit code) and compare against the pre-remediation renders — confirm no window regressed visually and the Task 1/7/8/9/10 changes look right in both palettes.
- [ ] **Step 4:** Launch sanity — Debug build, `Start-Process` with `--config demo-full\config.json`, ~5s, non-zero `MainWindowHandle`, `Stop-Process`, none remains.
- [ ] **Step 5:** `git push origin main && git ls-remote origin main` — fast-forward, SHAs match, never force.
