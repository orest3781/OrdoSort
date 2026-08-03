# UI audit, second pass — components, behaviour, and WPF best practice

**Date:** 2026-08-02 · **Commit audited:** `bc43f33` · **Follow-up to:** `2026-08-02-ui-audit.md`

The first pass covered what a user *sees* — theme coverage, accessibility,
copy, layout. This pass covers how the UI *behaves*: data binding, object
lifetime, threading, WPF idiom, and platform behaviour. Four passes, one of
them empirical (a live binding-error sweep across every window in both
palettes). No repo file was changed; everything below is a finding.

---

## The empirical result first: data binding is clean

WPF swallows binding failures silently — a mistyped path yields an empty
control and a trace warning nobody reads. So every window was booted with a
`PresentationTraceSources.DataBindingSource` listener attached at Warning
level, in both palettes, with lists and grids populated so item templates
actually instantiated, and Settings' six tabs individually realised.

**Result: 1 distinct message, 0 real defects.** Eleven of twelve windows
produced *zero* binding messages. The single message is the ComboBoxItem
foreground binding (`Theme/Styles.xaml:723-725`) resolving late during
construction — it fires only before `Show()`, never after layout, and the
rendered result is already proven correct by a passing WCAG test
(`HighlightContrastTests`, 12.89:1 light / 11.48:1 dark). Benign.

The listener was validated against a deliberately-broken binding first, so the
clean result is real rather than a silent harness failure. This is a genuine
quality signal and worth stating plainly.

*Side finding:* `demo-full`'s `history.sqlite` contains zero filings, so the
History grid's row bindings and its `Reverted` trigger go completely
unexercised by the workbench. Anyone writing tests against that fixture should
know.

---

## Critical

### C1 — Blocking file I/O on the UI thread, per keystroke, against network paths

**Found independently by two passes, from different angles** — which is why it
leads.

- `SettingsViewModel.cs:713-775` — the Inbox / Set-aside / History-db /
  section-file live notes call `Directory.Exists`, `File.Exists` and
  `Config.ReadDoc` synchronously **on every keystroke**.
- `SettingsViewModel.cs:36` — `RouteEditVm.Problem` calls
  `Config.ValidateRoute`, which **creates and deletes a real probe file**,
  synchronously on every keystroke of the destination-folder box
  (`SettingsWindow.xaml:408`).
- `SettingsViewModel.cs:206-213` — `WatchEditVm.Problem` does synchronous
  `Directory.Exists` per keystroke on the Dashboard folder box.

This app is built for network shares, and it states the principle itself:
`ShellViewModel.cs:230-233` and `:737-741` deliberately keep exactly these
calls off the UI thread. The Settings window is the one place that regressed.
Typing a UNC path into the destination box means a probe-file round-trip to a
possibly-unreachable share on each character — the classic WPF freeze.

**Fix direction:** debounce and move off-thread, reusing the pattern
`ShellViewModel` already uses; keep the note text optimistic until the check
returns.

### C2 — Enter, the primary commit gesture, bypasses the error safety net

`ShellViewModel.cs:963` (`OnEnter`, wired from `ProcessingView.xaml.cs:64`)
calls `OnRouteAsync` **directly** rather than through `RouteCommand`. That
skips `AsyncRelayCommand`'s `OnError` channel — the app's only guard against
an unobserved exception on a filing action. Clicking a destination button is
protected; pressing Enter, the gesture the app is designed around, is not.

### C3 — A failed history-db swap leaves a disposed object in use

`ShellViewModel.cs:1057-1071` (`ApplySettingsAsync`): if `new History(newDb)`
throws after `old.Dispose()`, `_history` still references the disposed
instance and the fault is unobserved (fire-and-forget via `ApplySettings` at
`:1042`). Autocomplete, CSV export and the History window then fail silently
for the rest of the session, with no message to the user.

---

## Important

### Lifetime & robustness

- **I1 — WebView2 instances leak per review session.**
  `TriageWindow.xaml.cs:44-45` / `MatchMergeWindow.xaml.cs:41-43` create a
  fresh WebView2 for each "Review matches" pass and never dispose it on close,
  accumulating browser processes across repeated sessions.
- **I2 — Triage ignores viewer-init failure.** `TriageWindow.xaml.cs:63-67`
  discards `_pdf.InitAsync()`'s bool result, unlike `MainWindow.xaml.cs:104-107`
  which checks it. A WebView2 failure leaves the review pane silently blank
  with no explanation.

### Platform behaviour

- **I3 — No DPI-awareness manifest.** `OrdoSort.Wpf.csproj` declares no
  `ApplicationManifest` and no `app.manifest` exists. The app therefore runs
  DPI-*unaware*: bitmap-scaled and blurry at 125%/150% — the default on most
  current laptops — and worse on mixed-DPI multi-monitor setups.
- **I4 — Culture-sensitive dates reach filenames, folders, labels and the audit
  log.** `BulkRenameViewModel.cs:128,188`, `Core/Unlock.cs:42`,
  `Core/History.cs:72`, `Core/BoxLabels.cs:217,220` format with
  `CurrentCulture`. On a shared deployment, two stations with different locale
  or calendar settings produce **different filenames for the same document** —
  and the box-label destruction dates and audit-log timestamps inherit the same
  variance. User-facing display should stay culture-aware; anything written
  into a name, a path or a record must be `InvariantCulture`.
- **I5 — High Contrast is never consulted.** `Theme/ThemeManager.cs:79-88`
  overwrites `SystemColors.*` unconditionally and `SystemParameters.HighContrast`
  appears nowhere in the tree. A user who has switched Windows to high contrast
  — an accessibility setting, not a preference — has it silently overridden.
- **I6 — Fixed-pixel label columns vs configurable text size.**
  `SettingsWindow.xaml:147` (`Width="130"`, repeated ~10×) is fixed while the
  app validates a base text size of 6–72pt (`SettingsViewModel.cs:1344-1346`).
  At the large end, labels overflow their column.
- **I7 — IME composition can commit a document mid-keystroke.**
  `ProcessingView.xaml.cs:63-66` handles `Key.Enter` — which files the document —
  with no `Key.ImeProcessed` or composition guard, and `ProcessingView.xaml:18-25`
  forces `CharacterCasing="Upper"`, which interacts badly with in-progress
  composition. Any user typing a CJK name can file a document by confirming a
  candidate.

### Responsiveness

- **I8 — History re-materialises its whole list per keystroke.**
  `HistoryViewModel.cs:97-108` does `Rows.Clear()` + per-item `Add()` on every
  Find-box keystroke instead of filtering an `ICollectionView`. History is the
  one collection that grows without bound, so this is the instance that matters.

---

## Minor (selected)

- **M1** `UnlockWindow.xaml:135-136` — an `ItemsControl` inside an explicit
  `ScrollViewer` with a `StackPanel` panel: no virtualization, plus two
  `DataTrigger`s per item. Fine at current result counts; would degrade on a
  large unlock batch.
- **M2** `Views/RgbToBrushConverter.cs:11-12` allocates a new frozen brush per
  call rather than caching by `Rgb` — hit on every tile flash tick and theme
  switch.
- Remaining minors (6 lifetime, 6 platform, 5 idiom) are itemised in the four
  pass reports.

---

## What holds up

- **Data binding: effectively spotless** (see above) — the strongest single
  result in either audit.
- **Theming discipline is airtight**: 216 of 216 `DynamicResource` uses target
  `Theme.*`/font resources, with zero `StaticResource Theme.` regressions —
  meaning live theme switching cannot silently freeze a colour anywhere.
- **Converters** are null-safe shared singletons with deliberate `ConvertBack`
  throws.
- **Virtualization is never explicitly disabled** on any `DataGrid` or `ListBox`.
- **`AsyncRelayCommand`** bakes in a reentrancy guard *and* a mandatory
  `OnError` channel — the pattern C2 is a gap against, not an absence of.
- **Disposal and unsubscription** are correct and race-free in `ShellViewModel`,
  `FolderWatchService` and `UnlockViewModel`; `ProcessingView` correctly
  re-pairs its DataContext-change subscription; command `CanExecute` wiring is
  exhaustive with no staleness bugs.
- **Every dialog sets `Owner` and `CenterOwner`**, and the live-theme mechanism
  correctly re-themes every open *and future* window.
- The off-UI-thread SMB discipline is real and followed everywhere except the
  Settings regression in C1.

---

## Suggested order of work

1. **C1** — the freeze risk, and it contradicts a principle the codebase
   already holds elsewhere. Debounce + off-thread.
2. **C2, C3** — two small correctness fixes on the primary commit path and the
   settings-apply path.
3. **I4** — invariant dates for anything written into a name, path or record.
   Cross-machine filename divergence is a data problem, not a UI one.
4. **I3** — add the DPI manifest; it is a few lines and affects every user on a
   scaled display.
5. **I1, I2, I7** — viewer lifetime, init-failure reporting, IME guard.
6. **I5, I6, I8**, then minors.
