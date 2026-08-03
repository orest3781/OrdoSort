# Audit remediation — design

**Date:** 2026-08-02
**Status:** Approved scope (user: "combine everything that needs to be fixed"; two scope decisions taken — see Decisions)
**Sources:** `docs/superpowers/audits/2026-08-02-ui-audit.md` (pass 1) and
`…-ui-audit-pass2.md` (pass 2)

## Context

Two audit passes over commit `bc43f33` produced ~35 findings: pass 1 covered
what a user sees (theme, accessibility, copy, layout), pass 2 how the UI
behaves (binding, lifetime, threading, WPF idiom, platform). Binding hygiene
came back effectively spotless and is not touched here. This spec consolidates
everything that warrants a fix into one remediation program.

## Decisions taken

| Question | Decision |
|---|---|
| Culture-sensitive dates in filenames/labels/log | **Forward-only.** New output uses `InvariantCulture`; existing files and history rows are left alone. No migration, no schema change. |
| Windows High Contrast | **Detect and step aside.** When the OS reports High Contrast, stop overriding `SystemColors` and let the OS palette through. No bespoke HC theme. |

## Scope — grouped by work package

### A. Correctness & safety

1. **DataGrid star columns collapse** (pass 1 C1) — `History` and `BulkRename`
   pin `Width="*"` columns to `MinWidth` 20 on first layout. Measured; a ±1px
   resize fixes them, proving it is the first-pass computation. **Confirm
   on-screen once before fixing** (the measurement was headless; a real user's
   window centering may already supply the nudge, which changes the fix).
2. **Blocking file I/O on the UI thread per keystroke** (pass 2 C1) —
   `SettingsViewModel.cs:713-775` (live path notes), `:36`
   (`RouteEditVm.Problem` → `Config.ValidateRoute`, which **creates and deletes
   a probe file**), `:206-213` (`WatchEditVm.Problem`). Against network paths
   this freezes the UI. The codebase already holds the opposite principle
   (`ShellViewModel.cs:230-233,737-741`) — restore it here: debounce, run
   off-thread, keep the note optimistic until the check returns.
3. **Enter bypasses the command error channel** (pass 2 C2) —
   `ShellViewModel.cs:963` calls `OnRouteAsync` directly instead of through
   `RouteCommand`, skipping `AsyncRelayCommand`'s `OnError`. Route the primary
   commit gesture through the command.
4. **Failed history swap leaves a disposed object** (pass 2 C3) —
   `ShellViewModel.cs:1057-1071`: if `new History(newDb)` throws after
   `old.Dispose()`, `_history` references a disposed instance and the fault is
   unobserved. Keep the old instance until the new one is constructed; surface
   the failure.
5. **Invariant dates for anything written** (pass 2 I4) —
   `BulkRenameViewModel.cs:128,188`, `Core/Unlock.cs:42`, `Core/History.cs:72`,
   `Core/BoxLabels.cs:217,220`. Display stays culture-aware; anything entering
   a filename, folder name, printed label or stored record becomes
   `InvariantCulture`. Forward-only.

### B. Robustness & platform

6. **WebView2 lifetime + init reporting** (pass 2 I1, I2) — dispose the
   per-review viewer on window close (`TriageWindow.xaml.cs:44-45`,
   `MatchMergeWindow.xaml.cs:41-43`); check `InitAsync()`'s result in
   `TriageWindow.xaml.cs:63-67` as `MainWindow` already does, and say so when it
   fails instead of showing a blank pane.
7. **DPI-awareness manifest** (pass 2 I3) — add `app.manifest` declaring
   per-monitor-v2 awareness and reference it from `OrdoSort.Wpf.csproj`.
8. **IME composition guard** (pass 2 I7) — `ProcessingView.xaml.cs:63-66` must
   ignore `Key.ImeProcessed` (and in-progress composition) before treating
   Enter as "file this document".
9. **High Contrast step-aside** (pass 2 I5) — `Theme/ThemeManager.cs:79-88`
   checks `SystemParameters.HighContrast` and skips the `SystemColors`
   override, re-evaluating when the setting changes.

### C. History window (perf + UX, one surface)

10. `HistoryViewModel.cs:97-108` filters via `ICollectionView` instead of
    `Clear()`+`Add()` per keystroke (pass 2 I8); the grid gains an empty-state
    message and `TextTrimming` on its fixed-width columns (pass 1 I7).

### D. Keyboard & accessibility

11. **Unlock: Enter does nothing** (pass 1 I1) — `UnlockWindow.xaml:12-24`
    needs `IsDefault` (or a Return `KeyBinding` on the password box).
12. **Settings hotkey capture swallows Esc** (pass 1 I2) —
    `SettingsWindow.xaml.cs:27-57` must let Escape close the dialog rather than
    record itself as the hotkey.
13. **Focus ring coverage** (pass 1 I3) — extend `BronzeFocusVisual`
    (`Styles.xaml:48,99`) to CheckBox, RadioButton, ComboBox, ListBoxItem and
    TabItem, which currently fall back to the OS dashed rectangle.
14. **Names for glyph-only controls + tab mnemonics** (pass 1 M5, M6) —
    `AutomationProperties.Name` on the four ↑/↓ reorder buttons and the ✎/＋
    glyph buttons; access keys on the six Settings tab headers.

### E. Copy & terminology

15. **One word per concept** — "Route" → "Destination" in user-facing text
    (`HistoryWindow.xaml:45`); the Settings tab titled "Dashboard" and its own
    header/Data-files label reconciled to "Monitored folders" (pass 1 I4, I5).
    Config keys and internal names are unchanged.
16. **Error and label copy** — replace "Something went wrong"
    (`ShellViewModel.cs:96-98`, `App.xaml.cs:20-22`) with a plain statement of
    what failed and what the user can do; "Start Processing" → sentence case
    (pass 1 I6, M4); informational Settings notes move off the amber status
    colour to `SubtleText` (pass 1 M3).

### F. Visual consistency

17. **One primary per window** — `LabelMakerWindow.xaml:11` vs `:20` (pass 1 I8).
18. **`FontSize="11"` → `CaptionText`** at ~20 sites (pass 1 I9).
19. **Extract `FieldRow`/`FieldLabel`** from `SettingsWindow.xaml:15-21` into
    `Theme/Styles.xaml` so the four windows hand-rolling the same row share it
    (pass 1 I10).
20. **Correct the documented spacing rhythm** to the practised 6/8/10/16
    (pass 1 I11).

### G. Verify-then-decide

21. **BulkRename "last" checkbox** renders without a visible label despite
    `Content="last"` (pass 1 M8) — measure; fix only if real.
22. **Triage shows both candidate rows selected** (pass 1 M9) — determine
    whether demo state or an alternating-row resolution bug; fix if real.

### H. Remaining minors

23. Ready-screen set-aside banner wraps mid-phrase (M1); Settings General tab
    dead space (M2); fixed 130px label columns vs 6–72pt text (pass 2 I6);
    `UnlockWindow` `ItemsControl` virtualization (pass 2 M1);
    `RgbToBrushConverter` per-call allocation (pass 2 M2); a test pinning the
    Appearance preview cards to `ThemePalette` (pass 1 M7).

## Explicitly not doing

- Rewriting existing filenames or history rows (forward-only decision).
- A bespoke high-contrast theme (step-aside decision).
- Anything in the binding layer — the sweep found no defects.
- `DataGridRow` hover affordance and `ScrollViewer` styling — measured fine in
  the theme audit and deliberately out of scope there.

## Verification standard

Every item is proven, not asserted, using the methods these audits established:

- Behaviour that renders → measured by off-screen render + resolved-value
  reads, both palettes.
- Threading fixes → prove the UI thread is not blocked (the work happens off
  the dispatcher), not merely that the code compiles.
- Anything with a "before" state → demonstrate the failing state first, then
  the fix, and confirm the compiled assembly under test lacks the fix before
  trusting a "before" measurement.
- Full suites stay green (baseline **728**) and grow with each behavioural fix.

## Delivery

Directly on `main`, one commit per work package, QC review after each package
before moving on, pushed after the full gate.
