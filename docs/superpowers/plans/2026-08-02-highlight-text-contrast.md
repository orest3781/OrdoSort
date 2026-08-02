# Highlighted-row Text Contrast Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Highlighted/selected rows in every affected container render their text at ≥4.5:1 in both themes, with a regression test that fails before the fix and passes after.

**Architecture:** Measure first (the remedy is chosen by harness comparison, because the obvious one failed in this codebase before), apply one shared remedy to each broken template in `Theme/Styles.xaml`, then close the CI gap with a headless test that asserts *resolved* colours rather than palette pairs.

**Tech Stack:** C# / .NET 8, WPF, xUnit. Repo `S:\OrdoSort`, branch `main`.

## Global Constraints

- Root cause, established: the implicit `TextBlock` style (`Theme/Styles.xaml:30-32`) sets `Foreground = Theme.Text`; a **style setter outranks an inherited value**, so container `Foreground` triggers never reach `TextBlock` glyphs.
- Measured baseline to beat: ComboBox highlighted row **1.35:1 light / 1.27:1 dark**. Target ≥ **4.5:1** both themes.
- Candidate remedy order — **(1)** `ControlTemplate.Resources` implicit `TextBlock` style, `BasedOn` the app style, `Foreground = {Binding Foreground, RelativeSource={RelativeSource AncestorType=<container>}}`; **(2)** fallback: explicit content element with a *local* `Foreground`. Prior art: a `Style.Resources` remedy was proven non-functional for PrimaryButton labels — do not assume (1) works, prove it.
- Containers in scope, expectations to be confirmed by measurement: `ComboBoxItem` (broken, measured), `DataGridCell` (expected broken), `MenuItem` submenu header (expected fine — `AccessText`, not `TextBlock`) plus its explicit `Gesture` TextBlock (unknown), `TabItem` (not affected — its trigger does not flip Foreground).
- Do NOT change highlight colours or palette roles; do NOT delete the implicit `TextBlock` style's Foreground setter (rejected in the spec — blast radius).
- Harness pattern: scratch WPF app replicating `SmokeUi.Boot`; re-apply `ShutdownMode` AFTER `InitializeComponent` or windows render 0x0; drain the dispatcher at `DispatcherPriority.Render` before capture or highlight state reads false. Popups are separate HWNDs — render the popup's `Child`. Delete the harness when done; keep only named PNGs.
- Do NOT run the smoke `screenshots` mode (known quirk: always exits 1).
- Baseline **686** tests green (Core 359 + Wpf 327) — must stay green; this plan ADDS tests.

---

### Task 1: Measure every container, choose the remedy

**Files:** none changed — investigation only. Harness under the session scratchpad, deleted after.

**Interfaces:**
- Produces: a verdict per container (broken / fine, with measured ratios both themes), and a proven remedy choice — (1) or (2) — that later tasks apply.

- [ ] **Step 1: Baseline-measure all four containers, both themes.** For each of `ComboBoxItem` (in an open drop-down), `DataGridCell` (a selected row in a small `DataGrid` with a `DataGridTextColumn`), `MenuItem` (an open submenu item, both its header AND its `Gesture` text via `InputGestureText`), and `TabItem` (selected header): resolve the actual text element in the visual tree, read its **effective** `Foreground` (`GetValue` on the real element, not the container), read the bar/background behind it, and compute contrast with the same formula as `ThemePalette.ContrastRatio`.

Report a table: container × theme × (fg RGB, bg RGB, ratio, PASS/FAIL at 4.5).

Expected: ComboBoxItem FAILs (~1.35 / ~1.27) — if it does not reproduce, STOP and report BLOCKED, because the premise is wrong.

- [ ] **Step 2: Trial the candidate remedies on ComboBoxItem only.** In the harness (not the repo), apply candidate (1) — a `ControlTemplate.Resources` implicit `TextBlock` style `BasedOn="{StaticResource {x:Type TextBlock}}"` with

```xaml
<Setter Property="Foreground"
        Value="{Binding Foreground, RelativeSource={RelativeSource AncestorType=ComboBoxItem}}" />
```

Re-measure. If the highlighted row now passes ≥4.5 in BOTH themes **and** the resting row is unchanged, remedy (1) is proven — record the measured numbers.

If it fails, trial candidate (2) (explicit content element with local `Foreground`) and record why (1) failed — that "why" is valuable; the codebase has now hit this trap twice.

- [ ] **Step 3: Confirm the remedy reaches DataTemplate items.** Still in the harness, give the ComboBox an `ItemTemplate` of a bare `<TextBlock Text="{Binding}" />` (this is the shape `KvpValueTemplate` and the font picker use) and re-measure the highlighted row. The chosen remedy MUST fix this shape too, or it is not a single-point fix — report which shapes it covers.

- [ ] **Step 4: Report** to `C:\Users\stoic\.superpowers\sdd\2026-08-02-highlight-text-contrast\task-1-report.md`: the full baseline table, the remedy trial results with measured ratios, the DataTemplate result, and an explicit "apply remedy (N) to containers: …" recommendation. Delete the harness. No commit (nothing changed).

---

### Task 2: Apply the remedy + regression test

**Files:**
- Modify: `src/OrdoSort.Wpf/Theme/Styles.xaml` (the templates Task 1 found broken)
- Test: `tests/OrdoSort.Wpf.Tests/ThemeTests.cs` (or a new `HighlightContrastTests.cs` if cleaner)

**Interfaces:**
- Consumes: Task 1's proven remedy and its broken-container list.

- [ ] **Step 1: Write the failing test FIRST.** A headless WPF test that resolves what a container actually renders. Shape (adapt names to the suite's conventions; the assertion is the requirement):

```csharp
public static IEnumerable<object[]> HighlightCases()
{
    foreach (var dark in new[] { false, true })
    {
        yield return new object[] { "ComboBoxItem", dark };
        yield return new object[] { "DataGridCell", dark };   // include only if Task 1 found it broken
    }
}

[Theory, MemberData(nameof(HighlightCases))]
public void HighlightedRowTextMeetsWcagAa(string container, bool dark)
{
    // build the container with the app's resource dictionaries + palette applied,
    // ApplyTemplate + Measure/Arrange, force the highlight/selected state,
    // walk the visual tree to the real text element, read its EFFECTIVE Foreground
    // and the bar background behind it, then:
    Assert.True(ThemePalette.ContrastRatio(fg, bg) >= 4.5,
        $"{container} ({(dark ? "dark" : "light")}): {fg} on {bg} = {ThemePalette.ContrastRatio(fg, bg):F2}");
}
```

The test must construct a `TextBlock`-content item (the broken shape), not an `AccessText` one. If a WPF STA/dispatcher fixture is needed, follow whatever the existing WPF tests already use.

- [ ] **Step 2: Run it — it MUST FAIL** with the measured ~1.3 ratios.

Run: `dotnet test tests/OrdoSort.Wpf.Tests --filter HighlightedRowTextMeetsWcagAa -v minimal`
Expected: FAIL, message showing ratios near 1.35 / 1.27. Paste the failure text into the report — this is the proof the test has teeth.

- [ ] **Step 3: Apply the remedy** from Task 1 to each container it found broken, in `Theme/Styles.xaml`. Add a brief comment at each site stating WHY the nearer-scope style is needed (a style setter outranks inheritance; the app-level implicit `TextBlock` style would otherwise pin `Theme.Text`).

- [ ] **Step 4: Run the test — it must PASS**, and print the new ratios.

- [ ] **Step 5: Full suites** — `dotnet build OrdoSort.sln && dotnet test OrdoSort.sln -v minimal` → 686 + the new cases, 0 failed.

- [ ] **Step 6: Visual confirmation.** Rebuild the Task 1 harness just far enough to render the fixed drop-down in both themes; save `dropdown-fixed-light.png` and `dropdown-fixed-dark.png` to the session scratchpad root (they must survive — the controller opens them). Delete the harness.

- [ ] **Step 7: Commit**

```bash
git add src/OrdoSort.Wpf/Theme/Styles.xaml tests/OrdoSort.Wpf.Tests
git commit -m "fix(theme): highlighted-row text now takes the accent foreground

Container triggers flipped Foreground to AccentText, but the label is a
TextBlock and the app's implicit TextBlock style pinned Theme.Text — a
style setter outranks inheritance, so highlighted rows rendered at
~1.3:1. A nearer-scope style in the affected templates restores it.
Regression test asserts resolved colours, not palette pairs.

Co-Authored-By: Claude Opus 5 (1M context) <noreply@anthropic.com>
Claude-Session: https://claude.ai/code/session_017qkNhAzYikJcFjhTdLzXJT"
```

---

### Task 3: Gate and push

- [ ] **Step 1:** `dotnet build OrdoSort.sln -c Release && dotnet test OrdoSort.sln -c Release -v minimal` — clean; record totals.
- [ ] **Step 2:** `dotnet run --project tools/OrdoSort.Smoke -- demo-full` — ends "All checks passed". (Do NOT run `screenshots`.)
- [ ] **Step 3:** Launch sanity — build Debug, `Start-Process` the exe with `--config demo-full\config.json`, wait ~5s, confirm the process has a non-zero MainWindowHandle, `Stop-Process`, confirm none remains.
- [ ] **Step 4:** `git push origin main && git ls-remote origin main` — fast-forward, SHAs match, never force.
