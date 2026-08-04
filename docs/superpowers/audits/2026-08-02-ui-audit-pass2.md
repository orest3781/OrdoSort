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

---

## Verify-then-decide outcomes (2026-08-03, Task 5)

Two open questions the audit could not answer from a screenshot, settled by
measurement against the real, compiled `OrdoSort.dll` (confirmed pre-fix by
grepping the built assembly: zero occurrences of `ContentTemplate` or
`HeaderTemplate`, one occurrence of `DeleteSegLast`, before any change was
made). Full method and raw numbers in
`.superpowers/sdd/2026-08-03-audit-remediation-finish/task-5-report.md`.

### (a) BulkRenameWindow's fifth delete-segment checkbox — REAL DEFECT, fixed

`BulkRenameWindow.xaml:78`, `<CheckBox Content="last" .../>`, rendered with
no visible label while its siblings (`"1"`…`"4"`) showed theirs.

**Measured, not the anticipated trap.** This was suspected to be the
auto-wrap "Style Setter outranks inheritance" contrast trap this codebase
has hit five times before. It was not: a rendered-pixel WCAG probe of the
label's own bounds read exactly **1.00** (foreground == background, zero
paint at all) in both palettes — but the resolved Foreground DP, Brush/
element Opacity, Visibility and `UIElement.Clip` were all completely normal
at every level from the TextBlock up to the Window. A DP-only check would
have reported everything fine and missed the bug entirely.

**Root cause: layout overflow, not colour.** The five checkboxes' combined
desired width is 182px; they sit in a Grid column fixed at `Width="170"`
(shared with two TextBoxes on other rows in the same Grid, which do need
that width). No element reported a `.Clip`, and `StackPanel.ActualWidth`
itself read the full unclamped 182px — but WPF still applies an internal,
DP-invisible render clip when an element's arranged `RenderSize` exceeds
what its own parent's `Arrange` call gave it. Because the StackPanel packs
children left-to-right, the 12px overflow landed entirely on the LAST
child. Proof: a column-by-column pixel scan of the whole row found the
fifth checkbox's own glyph square painted fine, nothing painted to its
right; re-rendering the identical bound `CheckBox` with no surrounding
width constraint painted "last" perfectly (49 distinct rendered colours).

**Fix:** `Grid.ColumnSpan="3"` on the delete-segment `StackPanel`
(`BulkRenameWindow.xaml`). Columns 2–3 carry no other content on that row,
so this costs nothing and gives ~170+Auto+170px of headroom.

**After fix:** WCAG ratio **16.40:1 (light) / 14.30:1 (dark)** — comfortably
above the 4.5 floor, in the same range as the numbered siblings. Regression
test: `tests/OrdoSort.Wpf.Tests/BulkRenameDeleteSegLastLabelTests.cs`
(`DeleteSegLastCheckboxLabelPaintsAndMeetsWcagAa`), asserting the resolved
rendered contrast, not a palette-pair DP read — a DP-only assertion would
have passed even on the broken build.

### (b) TriageWindow's two candidate rows — NOT A DEFECT

`TriageWindow.xaml:41-42`'s `Candidates` DataGrid (`SelectionMode="Single"`)
appeared to render both rows in the selected treatment in the audit's
screenshot.

**Measured:** built off-screen with one file under review and two
candidates, selected index 0. `DataGridRow.IsSelected`: row 0 = **True**,
row 1 = **False**. The row container's own `Background` never encodes
selection (by design — Styles.xaml's alternating-row stripe is a separate
layer from the selected overlay), so the real answer is at the
`DataGridCell` level, where `Styles.xaml`'s `DataGridCell` style actually
applies the selected colour: cell 0 `IsSelected=True`, resolved/rendered
Background = **Theme.Accent** `(45,50,58)`; cell 1 `IsSelected=False`,
resolved/rendered Background = **Theme.WindowBg** `(247,248,249)` (the
correct alternating-row stripe, not Accent). Selection state and rendered
paint agree exactly, 1:1, for both rows — confirmed visually too
(`triage-two-candidates-light.png` in the same artifacts folder: row "Row
One" dark/selected, "Row Two" plain). `SelectionMode` was also confirmed
still `Single` and not overridden anywhere in code.

**Verdict:** demo/screenshot-reading artifact, not a bug. Nothing changed.

---

## Verify-then-decide outcomes (2026-08-04, Task 7)

Two more open questions from the M1/M2 minors above, settled by rendering the
real, compiled windows off-screen rather than guessing from a screenshot.
Full numbers, PNGs and the width/font sweep in
`.superpowers/sdd/2026-08-03-audit-remediation-finish/task-7-report.md`.

### (a) Ready screen's set-aside banner (M1) — REAL DEFECT, fixed

`ShellViewModel.cs:359-370` (`ApplyDeferred`) builds `DeferredAlert` as
`"⚠ {count} set-aside file{s} waiting{age}   —   click to open"`, rendered by
`MainWindow.xaml:105-128`'s full-width `TextWrapping="Wrap"` banner Button.
`MainWindow.xaml.cs`'s `EnterCompact` puts the Ready screen in a narrow
"compact" window: `MinWidth = 400`, default `Width = 470`.

**Measured, not just the literal instruction.** Rendering at the literal
*minimum* width (400px) alone would have reported no defect — at 400–425px
the whole `"click to open"` clause wraps cleanly onto its own line. The
actual bug only appears in the reachable range *above* the floor: at the
compact mode's own **default** width (470px, what a user sees before ever
resizing) the phrase splits `"click to"` / `"open"`; at 440px it splits
`"click"` / `"to open"`. This reproduces the original M1 finding exactly. A
width sweep (400/410/425/440/460/470, both palettes) was necessary to catch
it — the single width named in the task brief would have missed it.

**Fix:** non-breaking spaces inside `"click to open"` only
(`ShellViewModel.cs:376-377`); the em dash before it keeps regular spaces
because that break point already reads fine (proof: it's exactly where the
400–425px renders wrap, with no complaint). Re-verified across the same six
widths, both palettes, after the fix: `"click to open"` never splits again.
No test existed asserting the exact wrapped-glyph sequence (the closest,
`ShellReadyTests.cs:52`, only checks a `Contains` on the count phrase), so
none needed updating.

### (b) Settings' General-tab dead space (M2) — accepted, no change

Settings' `TabControl` (`SettingsWindow.xaml:171`) sits in a fixed
`Height="820"` window (not `SizeToContent`), so all six tabs share one
height sized for the *tallest* tab's content, and General — the first tab a
user sees — is far from tallest: General's `TabItem` body
(`SettingsWindow.xaml:212-283`) is 71 lines of XAML with 4 field rows;
Monitored folders (`:654-1092`) is 438 lines; Destinations (`:353-654`) is
301; Appearance (`:1092-1295`) is 203. A light-theme render of the General
tab at default size (`Settings-light.png` in the task-7 report) shows the
tab's real content ending around a third of the way down the 820px window,
the rest plain background — the audit's "~55% empty" estimate holds up.

**Decision: accept, per the brief's own recommendation.** A `TabControl`
that resizes the whole dialog on every tab click is a worse experience than
a dialog with some empty space on its shortest tab — the window would grow
and shrink under the user's cursor as they click through Filing → Monitored
folders → Appearance, none of which is a size the user asked for. Static
dead space is the passive failure mode; a bouncing dialog is the active,
noticeable one. No code changed.

---

## Open defect carried forward (2026-08-04, Task 7 measurement, recorded at the Task 8 gate)

### SettingsWindow's 12 remaining fixed-`130px` label columns clip at large configured text size — MEASURED, UNFIXED

This is the other half of I6 (`SettingsWindow.xaml:147`, "Fixed-pixel label
columns vs configurable text size"). Task 7 measured it for real, fixed the
self-contained half of it, and ran into a genuine structural coupling on the
rest. Recorded here so the coupling and its cost are on the record, not only
in a throwaway scratch report.

**Count correction:** a naive `Width="130"` grep over-counts at 19. The real
`ColumnDefinition Width="130"` label-column count is **17**: 12 in
`SettingsWindow.xaml`, 5 in `LabelMakerWindow.xaml`. The other 2 of the 19
are `BulkRenameWindow.xaml`'s `Button MinWidth="130"` and `DatePicker
Width="130"` — neither is a label column and neither is part of this
finding.

**Measured — real clipping confirmed**, by forcing
`Application.Resources["AppFontSize"]` (the `DynamicResource` every
window's base style reads, `App.xaml.cs:89`) and rendering Settings' four
affected tabs (General/Filing/Appearance/Data files — the only tabs whose
label column is `Width="130"`; Destinations and Monitored folders use
different column widths and aren't part of this finding) plus LabelMaker,
off-screen, both palettes:

- **At 24pt** (a realistic "user turned up text size," ~1.7× the 14pt
  default): in Settings' General tab, `"Set-aside folder:"` renders as
  `"Set-aside fo"` and `"History database:"` as `"History dat"` — clipped
  and overlapping their own textboxes. Short labels (`"Inbox:"`) still fit.
  LabelMaker's `"Keep boxes (days):"` and `"Next label number:"` clipped
  the same way before its fix (see below).
- **At 72pt** (the literal validated ceiling — `SettingsViewModel.cs`
  ~1651-1652 rejects anything `< 6 or > 72`): clipping stops being limited
  to label columns at all. `OK`/`Cancel` (`Width="96"`), tab headers, and
  LabelMaker's `Print…`/`Save PDF…` (`Width="110"`) all truncate too, and
  LabelMaker's fixed `Height="560"` window collapses outright — its `Auto`
  preview row, now enormous, starves the `*` detail row of any space, so
  the "Client id:" row is pushed out of the visible window entirely, not
  merely clipped. At the literal maximum the 130px columns are one symptom
  among several of a bigger problem (fixed window dimensions, no
  `SizeToContent`, several other fixed-width controls) that a 17-column fix
  cannot solve on its own.

**Fixed: LabelMaker's 5 sites.** Each of LabelMaker's 5 label rows has no
coupling to anything else — the row's caption text lives in the same `Grid`
as the column it labels, so widening column 0 only shifts columns 1+ right,
with nothing external to break. All 5
`<ColumnDefinition Width="130" />` became
`<ColumnDefinition Width="Auto" SharedSizeGroup="LabelCol" />`, with
`Grid.IsSharedSizeScope="True"` added to the containing `StackPanel` (the
"selected client" panel, common ancestor of all 5 rows). Re-rendered at
24pt: all 5 labels render in full, aligned to the widest
(`"Next label number:"`), textboxes uniformly starting at the same X.
Re-rendered at normal (14pt) size: unchanged — `SharedSizeGroup` computes to
essentially the same ~140px the fixed value used, confirmed against the
standard screenshot pass.

**Not fixed: SettingsWindow's 12 sites — a real coupling, not a shortcut
skipped.** `SettingsWindow.xaml:46`'s `NoteText` style hard-codes
`Margin="130,-6,0,10"` to visually indent each field's informational note
(`"relative — resolved beside the config file"`, etc.) under its textbox
column — keyed to the *exact same* `130` the `ColumnDefinition`s use, but
as a sibling `TextBlock`'s static margin, not part of the row's `Grid`. 8 of
Settings' 12 affected rows have a following `NoteText` sibling (General's 4:
Inbox, Set-aside, Names list, History database; Data files' 4: Destinations,
Monitored folders, Alerts, Box labels). Converting only the
`ColumnDefinition`s to `Auto`+`SharedSizeGroup` would let the label column
grow past 130px at large text sizes while the note's margin stays pinned at
130 — silently trading "label text clips" for "note text no longer lines up
under its field" on 8 of the 12 rows.

The remaining 4 rows (Filing's 2, Appearance's 2) have no `NoteText`
sibling and could be converted cleanly on their own — but doing that while
leaving General/Data files fixed would make label-column behavior
*inconsistent between tabs in the same window* (some tabs scale with text
size, others don't), undermining the point of `FieldLabel` being one shared
style across the whole window (`Theme/Styles.xaml:1729-1736`).

**What a correct fix would have to restructure:** all 8 `NoteText` rows
would need to become `Grid`-integrated — a new row inside each `FieldRow`
`Grid`, spanning from column 1 — instead of an externally-margined sibling
`TextBlock` keyed to a magic-number margin. That is real structural work
across 8 rows in `SettingsWindow.xaml`, not a column-definition edit, and it
was out of scope for the task that found it.

**Verdict:** measured, real, evidenced defect in both windows; fixed
cleanly where the fix was self-contained (LabelMaker, 5/17 sites); left
unfixed where a correct fix requires restructuring a coupled style
(`SettingsWindow.xaml`'s `NoteText` margin, 12/17 sites). Flagged here as
the follow-up rather than shipped as a partial, inconsistent patch.
