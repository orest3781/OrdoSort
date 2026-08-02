# Theme Coverage Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Close the three theme-coverage gaps the audit measured — an unusable dark-mode calendar, stock Aero-blue list selection, and an unthemed print-preview toolbar — each proven by rendering.

**Architecture:** New styles in `Theme/Styles.xaml` for the `Calendar` family, `ListBoxItem`, and `DocumentViewer` chrome, following this file's existing conventions. Every text colour theme-bound; every fix verified by off-screen render + WCAG math, with regression tests extending `HighlightContrastTests`.

**Tech Stack:** C# / .NET 8, WPF, xUnit. Repo `S:\OrdoSort`, branch `main`.

## Global Constraints

- **Contrast trap (bit this codebase three times):** the implicit `TextBlock` style (`Styles.xaml:30-32`) pins `Foreground = Theme.Text`, and **a style setter outranks an inherited value**. Any label that is an auto-wrapped or bare `TextBlock` needs a **LOCAL** `Foreground` (bind to the container ancestor). **`ControlTemplate.Resources` was MEASURED non-functional for this trap — do not use it.** `AccessText` is immune (it is not a `TextBlock` and no implicit style targets it).
- Targets: every text surface ≥ **4.5:1** in BOTH palettes. Dark-mode calendar day numbers are currently **1.12–1.95:1**.
- Selection surfaces must resolve to `Theme.Accent` / `Theme.AccentText` — proven by asserting resolved colours equal palette values, not by eye.
- Palette: Light Surface 255,255,255 · WindowBg 247,248,249 · Text 23,26,31 · SubtleText 84,90,99 · Accent 45,50,58 · AccentText 255,255,255 · Border 186,192,200. Dark Surface 38,41,45 · WindowBg 26,28,31 · Text 233,235,238 · SubtleText 168,173,180 · Accent 205,210,218 · AccentText 23,26,31 · Border 76,82,90. (`Theme.SurfaceHover` exists — reuse it, don't invent a colour.)
- Do NOT change palette values or existing highlight colours. Out of scope: `DataGridRow` hover affordance, `ScrollViewer`.
- Harness rules: replicate `SmokeUi.Boot`; re-apply `ShutdownMode` AFTER `InitializeComponent` or windows render 0x0; drain the dispatcher at `DispatcherPriority.Render` before capture; popups are separate HWNDs — render the popup's `Child`. **Before trusting any "before" measurement, confirm the compiled assembly lacks the fix.** Find glyphs by scanning for max contrast — never hand-pick a coordinate.
- Do NOT run the smoke `screenshots` mode as a gate (always exits 1); it is fine as a rendering tool.
- Baseline **694** green (Core 359 + Wpf 335) — must stay green; this plan ADDS tests.

---

### Task 1: Calendar family (the critical one)

**Files:**
- Modify: `src/OrdoSort.Wpf/Theme/Styles.xaml` (new styles; correct the stale comment at the DatePicker block ~line 605 claiming the popup "keeps a readable light face")
- Test: `tests/OrdoSort.Wpf.Tests/HighlightContrastTests.cs` (extend)

**Interfaces:**
- Produces: implicit styles for `Calendar`, `CalendarItem`, `CalendarDayButton`, `CalendarButton`.

- [ ] **Step 1: Write the failing test FIRST**, extending the existing file's fixture/traversal conventions. It must open a real `DatePicker` drop-down (or host a `Calendar` directly), walk to the day-number text element, read its **resolved** Foreground and the surface behind it, and assert ≥4.5:1 in both palettes:

```csharp
[Theory]
[InlineData(false)]
[InlineData(true)]
public void CalendarDayNumbersMeetWcagAa(bool dark)
{
    // build a Calendar with the app's resources + palette applied,
    // ApplyTemplate + Measure/Arrange, resolve a CalendarDayButton's text element,
    // read effective Foreground and the rendered background behind it:
    Assert.True(ThemePalette.ContrastRatio(fg, bg) >= 4.5,
        $"calendar day ({(dark ? "dark" : "light")}): {fg} on {bg} = {ThemePalette.ContrastRatio(fg, bg):F2}");
}
```

- [ ] **Step 2: Run it — it MUST FAIL** in the dark case near 1.12–1.95. Paste the failure output. If it passes unfixed, STOP and report BLOCKED — the premise would be wrong.

- [ ] **Step 3: Implement the Calendar family styles** in `Styles.xaml`, following the file's existing style/template conventions:
  - `Calendar` + `CalendarItem`: surface `Theme.Surface`, border `Theme.Border`, 4px flat chrome consistent with the rest of the file; header (month/year) button text and the day-name strip bound to `Theme.Text`; navigation arrows `Theme.SubtleText`.
  - `CalendarDayButton`: default text `Theme.Text`; `IsMouseOver` → `Theme.SurfaceHover`; `IsSelected` → `Theme.Accent` background with `Theme.AccentText` text; `IsToday` visually marked (border or weight) while staying ≥4.5:1; `IsInactive` (adjacent month) → `Theme.SubtleText`; disabled → `Theme.SubtleText` at reduced opacity.
  - `CalendarButton` (month/year picker cells): same treatment as day buttons.
  - **Every text colour theme-bound.** Where a label would be an auto-wrapped/bare `TextBlock`, give it a LOCAL `Foreground` per the Global Constraints.
  - Replace the stale "keeps a readable light face" comment with what is now true.

- [ ] **Step 4: Run the test — it must PASS**, printing the new ratios for both palettes.

- [ ] **Step 5: Render proof.** Off-screen-render the open calendar in both palettes; save `calendar-fixed-light.png` and `calendar-fixed-dark.png` to the scratchpad ROOT (they must survive — the controller opens them). Confirm by eye that every day number, the day-name strip, today, and the selected day are legible.

- [ ] **Step 6: Suites** — `dotnet build OrdoSort.sln && dotnet test OrdoSort.sln -v minimal` → 694 + new, 0 failed.

- [ ] **Step 7: Commit**

```bash
git add src/OrdoSort.Wpf/Theme/Styles.xaml tests/OrdoSort.Wpf.Tests
git commit -m "fix(theme): theme the DatePicker calendar — dark mode was unreadable

Day numbers measured 1.12-1.95:1 in dark mode: the popup kept a hardcoded
light face while the text went near-white. The Calendar family is now
fully theme-bound, with today/selected using Accent/AccentText.

Co-Authored-By: Claude Opus 5 (1M context) <noreply@anthropic.com>
Claude-Session: https://claude.ai/code/session_017qkNhAzYikJcFjhTdLzXJT"
```

---

### Task 2: ListBoxItem selection + DocumentViewer chrome

**Files:**
- Modify: `src/OrdoSort.Wpf/Theme/Styles.xaml`
- Test: `tests/OrdoSort.Wpf.Tests/HighlightContrastTests.cs` (extend)

**Interfaces:**
- Consumes: nothing from Task 1 (independent styles).
- Produces: implicit `ListBoxItem` style; `DocumentViewer` chrome styling.

- [ ] **Step 1: Write the failing test FIRST** — a `ListBoxItem` case asserting BOTH that contrast ≥4.5 AND that the selected background **equals `Theme.Accent`** and its text equals `Theme.AccentText` (the current Aero blue passes contrast, so a contrast-only assertion would not fail):

```csharp
[Theory]
[InlineData(false)]
[InlineData(true)]
public void SelectedListBoxItemUsesTheAccentPalette(bool dark)
{
    var p = dark ? ThemePalette.Dark : ThemePalette.Light;
    // build a ListBox with the app's resources, select an item, resolve the row:
    Assert.Equal(p.Accent, selectedBackground);       // not Aero blue
    Assert.Equal(p.AccentText, resolvedForeground);
    Assert.True(ThemePalette.ContrastRatio(resolvedForeground, selectedBackground) >= 4.5);
}
```

- [ ] **Step 2: Run it — it MUST FAIL** on the palette-equality assertion (stock blue). Paste the output.

- [ ] **Step 3: Implement the `ListBoxItem` style** in `Styles.xaml`, mirroring the `DataGridCell` convention so the app's selection surfaces agree: default `Background=Transparent` + `Foreground=Theme.Text`; `IsMouseOver` → `Theme.SurfaceHover`; `IsSelected` → `Theme.Accent` / `Theme.AccentText`. Remember the LOCAL-`Foreground` trap for the item's label.

- [ ] **Step 4: Run the test — it must PASS.**

- [ ] **Step 5: `DocumentViewer` chrome.** Render `PrintPreviewWindow` in dark mode first and confirm the white island. Then theme its chrome (background/toolbar host surfaces) so no stock-white region remains, and re-render to prove it. If part of the stock `ToolBar` template proves impractical to reach, do the reachable part and record **precisely** what remains and why — a partial, honestly-reported fix is acceptable here; a silent one is not.

- [ ] **Step 6: Render proof.** Save `list-fixed-dark.png` and `printpreview-fixed-dark.png` to the scratchpad ROOT.

- [ ] **Step 7: Suites** — full solution green.

- [ ] **Step 8: Commit**

```bash
git add src/OrdoSort.Wpf/Theme/Styles.xaml tests/OrdoSort.Wpf.Tests
git commit -m "fix(theme): list selection uses the app palette; print preview chrome themed

ListBoxItem rendered stock Aero blue instead of Theme.Accent, disagreeing
with DataGridRow. DocumentViewer's toolbar was a white island in dark mode.

Co-Authored-By: Claude Opus 5 (1M context) <noreply@anthropic.com>
Claude-Session: https://claude.ai/code/session_017qkNhAzYikJcFjhTdLzXJT"
```

---

### Task 3: Gate and push

- [ ] **Step 1:** `dotnet build OrdoSort.sln -c Release && dotnet test OrdoSort.sln -c Release -v minimal` — clean; record totals.
- [ ] **Step 2:** `dotnet run --project tools/OrdoSort.Smoke -- demo-full` — ends "All checks passed".
- [ ] **Step 3:** Launch sanity — build Debug, `Start-Process` with `--config demo-full\config.json`, ~5s, confirm non-zero MainWindowHandle, `Stop-Process`, confirm none remains.
- [ ] **Step 4:** `git push origin main && git ls-remote origin main` — fast-forward, SHAs match, never force.
