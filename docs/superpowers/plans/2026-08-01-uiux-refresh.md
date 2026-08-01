# UI/UX Refresh Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** The "crisp workbench" refresh — graphite/steel palette with a bronze accent in four roles, 4px flat control language with decisive borders and a bronze focus visual, spacing rhythm and per-window retouches, dark title bar — delivered with before/after screenshots for the user's visual acceptance.

**Architecture:** Screenshot tooling first (so the BEFORE set exists), then the palette (token evolution + new `AccentBronze`, WCAG-pinned), then the shared control language in `Styles.xaml`, then two per-window retouch waves, then the gate + AFTER screenshots + delivery. Pixels can't be unit-tested: `ThemeTests` guards contrast, suites guard behavior, and the user's screenshot pass is final acceptance.

**Tech Stack:** C# / .NET 8, WPF, xUnit, DWM interop. Repo `S:\OrdoSort`, branch `main` (established: commits per task, push only in the final task). Suites baseline: Core 375 + Wpf 311 = 686.

## Global Constraints

- Palette targets (tune only if a `ThemeTests` pairing fails, staying within the same hue family): LIGHT — WindowBg (247,248,249), Surface (255,255,255), Text (23,26,31), SubtleText (84,90,99), Border (186,192,200), new BorderStrong (120,128,138), Accent → graphite primary (45,50,58), AccentText (255,255,255), new AccentBronze (140,109,63), TileDefaultBg (228,230,233). DARK — WindowBg (26,28,31), Surface (38,41,45), Text (233,235,238), SubtleText (168,173,180), Border (76,82,90), BorderStrong (110,118,128), Accent (205,210,218), AccentText (23,26,31), AccentBronze (201,169,106), TileDefaultBg (54,58,63). Warning/Danger/Success/StatusAmber values unchanged.
- Bronze appears in EXACTLY four roles: focus visuals, the ⏎ Enter-target badge, selected tab/section indicators, progress/working states. Route colors and alert red untouched.
- Control language: 4px radii everywhere (buttons drop from 6); flat (no shadows); hover = border moves Border→BorderStrong (no glow); focus = 2px AccentBronze rounded rectangle, 2px offset, via a shared `FocusVisualStyle` resource; primary buttons + section headers SemiBold; spacing rhythm 6/10/16 — normalize only values deviating >2px from the nearest step within areas a task touches, never restructure layout.
- Non-goals (verbatim from spec): no layout changes, no custom-drawn title bar (DWM dark attribute only), no new illustrations/iconography, no route/alert color changes, no new settings.
- Screenshots: `S:\tmp\ordosort-refresh-shots\before\` and `...\after\` (untracked), both themes per window, captured via the new smoke command against demo-full.
- Reviewer rule: `git show COMMIT:path` only; scratch files deleted with a clean-tree check.
- Suites stay green throughout; `ThemeTests` additions only (no pairing removed).

---

### Task 1: Smoke `screenshots` command + BEFORE capture

**Files:**
- Create: `tools/OrdoSort.Smoke/Screenshots.cs`
- Modify: `tools/OrdoSort.Smoke/Program.cs` (dispatch line beside the `dialogs` entry)

**Interfaces:**
- Produces: `dotnet run --project tools/OrdoSort.Smoke -- screenshots <outdir> [light|dark|both]` — renders each app window off-screen against `demo-full/config.json` and writes `<name>-<theme>.png` files. Task 6 re-uses it verbatim.

- [ ] **Step 1: Implement `Screenshots.Run(string[] args)`** — pattern it on `DialogCheck.Run` (same STA bootstrapping, same window construction list incl. MainWindow with the demo-full config, SettingsWindow, UnlockWindow, ManageSavedWindow, BulkRenameWindow, MatchMergeWindow, LabelMakerWindow, HistoryWindow). Per window per theme:

```csharp
// force the theme (the config's theme field drives ThemeManager exactly as at startup)
// show off-screen so WPF performs a real render pass:
win.WindowStartupLocation = WindowStartupLocation.Manual;
win.Left = -20000; win.Top = 0; win.ShowActivated = false;
win.Show();
win.UpdateLayout();
var src = (FrameworkElement)win.Content;
var bmp = new RenderTargetBitmap((int)Math.Ceiling(win.ActualWidth),
    (int)Math.Ceiling(win.ActualHeight), 96, 96, PixelFormats.Pbgra32);
bmp.Render(win);
var enc = new PngBitmapEncoder();
enc.Frames.Add(BitmapFrame.Create(bmp));
using (var fs = File.Create(Path.Combine(outdir, $"{name}-{theme}.png"))) enc.Save(fs);
win.Close();
```

For MainWindow, capture three states: Ready (default), Processing (start a session the way `Reentrancy`/the filing smoke does), Done if cheaply reachable (skip with a console note if not — record what was skipped). Adapt construction details from `DialogCheck`/`Reentrancy`; anything that can't render headlessly gets skipped WITH a printed note, never silently.

- [ ] **Step 2: Dispatch** — `if (args.Length > 0 && args[0] == "screenshots") return Screenshots.Run(args);` beside the other entries.
- [ ] **Step 3: Capture the BEFORE set** — `dotnet run --project tools/OrdoSort.Smoke -- screenshots S:\tmp\ordosort-refresh-shots\before both` (run `demo-full` first if the workbench is missing). Verify the PNG list covers every window in both themes; record the file list + any skips in your report.
- [ ] **Step 4: Suites still green** (`dotnet test OrdoSort.sln -v minimal` — the command is new code, nothing touched).
- [ ] **Step 5: Commit** — `feat(smoke): off-screen window screenshot capture` (+ standard trailers from `git log -1 --format=%B`).

---

### Task 2: Palette evolution + AccentBronze

**Files:**
- Modify: `src/OrdoSort.Wpf/Theme/ThemePalette.cs` (both static instances + the record gains `BorderStrong` and `AccentBronze` positional members), `src/OrdoSort.Wpf/Theme/ThemeManager.cs` (publish the two new brushes as `Theme.BorderStrong` / `Theme.AccentBronze` resources — mirror how existing tokens map), `tests/OrdoSort.Wpf.Tests/ThemeTests.cs`

**Interfaces:**
- Produces: `Theme.BorderStrong` and `Theme.AccentBronze` DynamicResource brushes for Tasks 3-5; palette values per Global Constraints.

- [ ] **Step 1: Tests first** — extend the pairing enforcement in `ThemeTests` (read how it enumerates pairs; add in the same style): `(AccentBronze, WindowBg)`, `(AccentBronze, Surface)` at ≥4.5 (bronze renders text in the ⏎ badge), `(Text, TileDefaultBg)` if not already present, and keep every existing pair. Run → the new pairs FAIL against the old palette (bronze token doesn't exist → compile-red first).
- [ ] **Step 2: Apply the palette values from Global Constraints** — record members added at the END of the positional list (existing constructions use named args? read and update both instances + any test constructions). Publish the new brushes in ThemeManager.
- [ ] **Step 3: Green** — `ThemeTests` filter, then full Wpf suite. If any pairing fails, tune within the hue family (graphite stays cool-grey, bronze stays warm) and record the final values in your report.
- [ ] **Step 4: Commit** — `feat(theme): graphite palette with bronze accent tokens`.

---

### Task 3: Control language — Styles.xaml sweep

**Files:**
- Modify: `src/OrdoSort.Wpf/Theme/Styles.xaml`

**Interfaces:**
- Consumes: `Theme.BorderStrong`, `Theme.AccentBronze`.
- Produces: shared `x:Key="BronzeFocusVisual"` FocusVisualStyle applied by every focusable styled control; 4px radii app-wide.

- [ ] **Step 1:** Define once at the top:

```xaml
    <Style x:Key="BronzeFocusVisual">
        <Setter Property="Control.Template">
            <Setter.Value>
                <ControlTemplate>
                    <Border BorderBrush="{DynamicResource Theme.AccentBronze}"
                            BorderThickness="2" CornerRadius="4" Margin="-2"
                            SnapsToDevicePixels="True" />
                </ControlTemplate>
            </Setter.Value>
        </Setter>
    </Style>
```

- [ ] **Step 2:** Sweep every styled control: radii → 4 (Button/ToggleButton drop from 6; TextBox/PasswordBox already 4 — verify; Thumb, list items, combos, chips in window XAML come in Tasks 4-5); every existing `FocusVisualStyle` setter → `{StaticResource BronzeFocusVisual}`; hover triggers change `BorderBrush` to `{DynamicResource Theme.BorderStrong}` (replace any hover background-wash-only triggers — keep existing subtle washes where present, ADD the border change); Button/ToggleButton default `FontWeight` stays as-is but add a `x:Key="PrimaryButton"`-check: read whether a PrimaryButton style exists (the Settings OK uses one) and set `FontWeight="SemiBold"` there + on section-header TextBlock styles (`SectionText` if that's the key — verify names by reading).
- [ ] **Step 3:** Build + full Wpf suite green; run smoke `dialogs` (all windows still construct).
- [ ] **Step 4: Commit** — `feat(theme): crisp-workbench control language`.

---

### Task 4: MainWindow wave — views, tiles, toasts, dark title bar

**Files:**
- Create: `src/OrdoSort.Wpf/Services/TitleBarChrome.cs` (DWM helper)
- Modify: `src/OrdoSort.Wpf/MainWindow.xaml(+.cs)`, `src/OrdoSort.Wpf/Views/ReadyView.xaml`, `ProcessingView.xaml`, `DoneView.xaml` — spacing rhythm + tile/section-header/confirmation-card polish; the ⏎ badge renders `Theme.AccentBronze`

**Interfaces:**
- Produces: `TitleBarChrome.ApplyDarkTitleBar(Window window, bool dark)`:

```csharp
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace OrdoSort.Wpf.Services;

/// <summary>Dark title bar via DWMWA_USE_IMMERSIVE_DARK_MODE (attr 20,
/// Win10 1903+/Win11). Failure is cosmetic — swallow it.</summary>
public static class TitleBarChrome
{
    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr,
        ref int value, int size);

    public static void ApplyDarkTitleBar(Window window, bool dark)
    {
        try
        {
            var hwnd = new WindowInteropHelper(window).EnsureHandle();
            var v = dark ? 1 : 0;
            _ = DwmSetWindowAttribute(hwnd, 20, ref v, sizeof(int));
        }
        catch { /* cosmetic only */ }
    }
}
```

- [ ] **Step 1:** Wire the title bar: find where ThemeManager knows the effective scheme (light/dark) and how windows learn theme changes (`SettingsApplied` / theme-change event); apply `ApplyDarkTitleBar` to every open window on startup and on theme change (MainWindow forwards to owned windows, or each window applies in its Loaded — pick the smaller change after reading ThemeManager's event surface; document the choice).
- [ ] **Step 2:** Views retouch per the rhythm (6/10/16, >2px-deviation rule): Ready — section headers SemiBold, tile radius 4, tile internal padding to rhythm; Processing — name box gets 1.5px-feel prominence (BorderStrong at rest is too much — keep Border at rest, BorderStrong on focus is automatic via template; increase its FontSize by 1 step ONLY if currently identical to body — check), the ⏎ badge foreground → `Theme.AccentBronze`, confirmation card radius 4; Done — summary card radius/spacing. Toasts (find the toast XAML/window): radius 4, border, rhythm.
- [ ] **Step 3:** Suites + `dialogs` green; quick manual launch (`Start-Process` with demo-full, confirm it renders, close).
- [ ] **Step 4: Commit** — `feat(ui): main-window wave — rhythm, bronze badge, dark title bar`.

---

### Task 5: Settings + tools + History wave

**Files:**
- Modify: `SettingsWindow.xaml`, `UnlockWindow.xaml`, `ManageSavedWindow.xaml`, `BulkRenameWindow.xaml`, `MatchMergeWindow.xaml`, `TriageWindow.xaml`, `LabelMakerWindow.xaml`, `PrintPreviewWindow.xaml`, `HistoryWindow.xaml`, empty-state framing in `ReadyView`/`DoneView`/tool windows where the `Assets` illustrations render

**Interfaces:** consumes Tasks 2-3 resources; no new ones.

- [ ] **Step 1:** Per window, apply the rhythm rule + radius-4 on any local Border/chip/card elements + selected-tab indicator: TabItem selected state gets a 2px `Theme.AccentBronze` bottom underline (Settings' `SectionTab` style — read it; the underline replaces/joins whatever selected treatment exists). Alert chips (Dashboard tab) radius 10 → 4 for consistency with the new language.
- [ ] **Step 2:** Empty states: keep illustrations; wrap each in the standard card treatment (Surface, Border, radius 4, 16px padding) if not already; copy tweaks ONLY where text refers to outdated UI (grep for stale phrases — e.g., anything still describing the multiline alerts box or the old unlock picker).
- [ ] **Step 3:** Progress/working states (History lazy-load, Unlock progress, Match & merge progress — find the indicators): foreground/brush → `Theme.AccentBronze` where they currently use Accent.
- [ ] **Step 4:** Suites + `dialogs` green.
- [ ] **Step 5: Commit** — `feat(ui): settings, tools and history wave`.

---

### Task 6: Gate, AFTER screenshots, push, deliver

- [ ] **Step 1:** `dotnet build OrdoSort.sln -c Release && dotnet test OrdoSort.sln -c Release -v minimal` — green (record totals; expect 686 + ThemeTests additions).
- [ ] **Step 2:** `demo-full` → "All checks passed"; `dialogs` → exit 0.
- [ ] **Step 3:** AFTER screenshots: `dotnet run --project tools/OrdoSort.Smoke -- screenshots S:\tmp\ordosort-refresh-shots\after both` — verify the file list matches the BEFORE set (same names; any new skip must be explained).
- [ ] **Step 4:** Launch sanity (Start-Process, window check, clean stop — visually confirmable dark title bar noted in the report if the machine theme is dark).
- [ ] **Step 5:** Push (`git push origin main`, ancestry-checked, never force; ls-remote match; no tags).
- [ ] **Step 6:** Controller delivers the before/after screenshot pairs to the user for the acceptance pass.
