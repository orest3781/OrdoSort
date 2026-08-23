# Full UI audit — every window, menu, control and surface

**Date:** 2026-08-22
**Scope:** all 21 XAML surfaces (7,551 lines), the theme system, the menu bar,
every dialog, and the shared control styles.

**Two passes.** A source read first, then a **live verification pass** on
2026-08-22: the app built and run against `demo-full` (Ledger scheme,
`ui_font_family: Consolas`), driven through UI Automation and real keystrokes,
plus an offscreen WPF probe that measured layout and resolved brushes off the
real rendered elements. The live pass **corrected three findings and closed the
open question** — see *Live verification pass* below.

**Method:** read every XAML file end to end; swept all of them for hardcoded
colour, window chrome, automation names, access keys, tooltips,
default/cancel handling and copy; cross-checked findings against the existing
test suite so nothing already guarded is reported as open. Contrast numbers
were computed independently (WCAG 2.1 relative luminance) and agree with
`ThemePalette.cs`'s own documented figures to two decimals.

**30 findings** — 2 High, 14 Medium, 14 Low (UI-30 was found while fixing).
Numbered `UI-nn`; severity is about what a user hits, not how hard it is to fix.

**Status 2026-08-23: 19 fixed or mitigated, 11 open.** Still open: UI-05's
keyboard half (access keys), UI-09, UI-11, UI-17, UI-18, UI-19, UI-20, UI-21,
UI-23, UI-24, and UI-30 (the Folder row's layout).

---

## Live verification pass

What running the app changed. Everything here is measured, not inferred.

| Finding | Outcome |
|---|---|
| UI-01 | **Confirmed.** Brush resolved off the real rendered `TextBlock`: `Rgb(46,125,50)` in all four dark schemes, ratios 3.33 / 3.44 / 3.43 / 3.71. Light schemes pass (4.82 / 5.27 / 5.40). |
| UI-02 | **Confirmed visually.** Screenshot: OrdoSort's own error dialog is white with a light title bar beside the app's dark Ledger window, in Segoe UI rather than the configured Consolas. |
| UI-04, UI-05 | **Confirmed via UI Automation.** Both `Edit` controls in Filename list report `Name=''`, the `DataGrid` reports `Name=''`, and **no control anywhere reports a `LabeledBy`**. |
| UI-07 | **Wrong twice — corrected, then fixed.** "Manage saved…" and Close do *not* move (0.0px); the primary **Unlock** button does. And of the four sites named, only Unlock moves at all. |
| UI-09 | **Partly wrong — corrected.** TriageWindow (720) fits a 768px laptop; only PrintPreview and Settings don't. |
| UI-12 | **Wrong in detail — corrected.** In PageCounts *every* button is always enabled, Clear and Save included. |
| UI-17 | **Confirmed visually.** Under Consolas the ↑/↓ glyphs fall back to another face and render as hairlines beside bold Consolas labels. |
| Open question (Ctrl+Z) | **Closed — not a defect.** Typed `HELLO` into the Find box, pressed a real Ctrl+Z, the box cleared. The TextBox's own undo wins; the window binding never fires. |

The live pass also produced five findings the source read missed — `UI-25`
through `UI-29` below — four of which only appear because this config uses a
**non-default font family**, an axis the test suite does not vary.

---

## What is already solid

Worth stating first, because it shapes what the findings below are worth
spending time on.

- **Zero hardcoded colours.** A sweep for `#rrggbb`, `White`, `Black`, `Gray`,
  `Red` and friends across all 7,551 lines of XAML returns nothing. Every
  brush goes through `Theme.*` + `DynamicResource`.
- **Overflow is genuinely tested.** `WindowOverflowTests` drives 14 windows at
  two font sizes (14px at MinWidth, 18px at default width) and asserts no text
  element escapes; `LabelMakerOverflowTests` covers the fifteenth. The
  `WrapPanel`-instead-of-`StackPanel` decisions throughout are the result, and
  they hold.
- **The chrome is uniform.** Every tool window: `OrdoSort — <thing>` title,
  `CenterOwner`, an explicit `MinWidth`/`MinHeight`, and Esc closes via
  `IsCancel`.
- **Theme plumbing is unusually complete.** DWM immersive-dark title bars per
  window, live OS light/dark switching, a High Contrast step-aside, and
  per-column "let selection win" triggers so a status colour never survives
  onto the accent highlight.
- **Menu mnemonics are collision-free** across File/Tools/Help and all **seven**
  Settings tabs (the first draft said six — the seventh, *Data files*, was
  missed by a truncated grep and found by opening the window). Verified live via
  UI Automation: File S/V/E/X, Tools U/B/M/X/F/C/L/Z, Help A. `Ctrl+,` is really
  registered (MainWindow.xaml.cs:99), not just advertised — though see UI-28 for
  what assistive tech is told about it.

---

## High

### UI-01 — `Theme.Success` is still used as text colour, and the contrast wall has no test for it

> **Confirmed live.** Brush resolved off the real rendered `TextBlock` in a
> shown window, per scheme — not computed from the palette file.

`src/OrdoSort.Wpf/Views/DoneView.xaml:8`, `src/OrdoSort.Wpf/Views/ReadyView.xaml:273`

`StatusGreen` exists precisely because `Success` fails 4.5:1 as foreground in
the dark schemes — `ThemeTests.cs:24` says so in a comment, and
`ThemePalette.cs` repeats it. The migration reached the Unlock file list but
missed two sites, and `ThemeTests.TextPairs()` never enumerates a `Success`
pairing, so the wall the README advertises ("every text color pairing in the
theme is enforced to WCAG AA 4.5:1 **by a unit test**") has a hole in exactly
the shape of the bug it was built for.

Measured (`Success` = 46,125,50, identical in all four dark schemes):

| scheme | Success on WindowBg | Success on Surface |
|---|---|---|
| graphite | 3.33 | 2.85 |
| ledger | 3.44 | 2.99 |
| microfilm | 3.43 | 3.03 |
| carbon | 3.71 | 3.36 |

`StatusGreen` at the same site measures 8.49 (graphite).

- **ReadyView.xaml:273** — the ✓ before "All monitored folders are quiet", at
  `FontSize="12"`. Small text, 3.33:1. Unambiguous AA failure.
- **DoneView.xaml:8** — the `CountLine` headline, `HeadlineText` (26px Bold).
  Large text under WCAG, so it clears the 3:1 large-text floor — but it fails
  the 4.5:1 rule this project set for itself.
- **DoneView.xaml:22** — the completion dot is a graphic (`Ellipse.Fill`), 3:1
  applies, 3.33 passes. Noted so a fix doesn't sweep it up unnecessarily.

**Fix:** point both foreground uses at `Theme.StatusGreen`, then close the test
hole — either add `{Success, WindowBg}` / `{Success, Surface}` to
`TextPairs()`, or (cleaner) assert that `Theme.Success` appears in no
`Foreground=` position in any XAML, which is what actually went wrong twice
now.

### UI-02 — every alert, confirmation and error is an unthemed Win32 `MessageBox` — **FIXED 2026-08-23**

> **Confirmed visually.** A real config-error dialog from the real code path,
> screenshotted beside the app's dark Ledger window: white body, light title
> bar, Segoe UI instead of the configured Consolas. See also UI-27 for what it
> says.

`Services/DialogService.cs:13,16,19`, `App.xaml.cs:29,62,82`

`Warn`, `Info`, `Confirm` and the three startup/crash paths all call
`MessageBox.Show`. A `MessageBox` is a Win32 dialog, not a WPF `Window`, so:

- `TitleBar.Hook()` never sees it — no dark title bar.
- No `Theme.*` brush reaches it — in graphite/ledger/microfilm/carbon the app
  opens a white dialog with black text on top of a dark window.
- The configured app font size (6–72, a first-class Appearance setting) is
  ignored entirely.
- `Confirm` ships generic **Yes**/**No** buttons rather than naming the action
  ("Move", "Discard"), which is the one place a destructive confirmation most
  needs a verb.

This is the largest remaining "look" break in the app, it is on the path of
every error the user will ever see, and no test touches it. The rest of the
theme work is meticulous; this undoes a visible slice of it.

**Fix:** a small themed `OrdoSortDialog` window reusing `PrimaryButton` and the
existing card chrome, behind the existing `IDialogService` seam — the interface
is already the only thing view models talk to, so this is a one-class change
plus `DialogServiceContractTests`.

---

## Medium

### UI-03 — 353 KB of dead PNGs still ship in the binary

`OrdoSort.Wpf.csproj:21` — `<Resource Include="Assets\*.png" />`

`done.png` (95 KB), `inbox-clear.png` (36 KB), `labels-empty.png` (105 KB) and
`unlock.png` (118 KB) were replaced by the vector `Illustrations.xaml`
templates. Nothing references them: the only occurrence of any filename in the
whole tree is the comment in `Illustrations.xaml:5` recording that they were
replaced. They are still compiled in as WPF resources. The csproj comment
("empty-state illustrations (generated, de-whited, 512px)") is stale and now
describes files nothing uses.

The README's first design goal is "**Small.** … the app's own code is a few
hundred KB." 353 KB of dead resources is a meaningful share of that.

**Fix:** delete the four files; narrow the `Resource` glob to `app.ico` and the
sounds, or leave the glob and let it match nothing.

### UI-04 — screen-reader names are absent in three windows and near-absent in two more — **FIXED 2026-08-23**

> **Confirmed live via UI Automation** against the running Filename list window:
> both `Edit` controls report `Name=''`, the `DataGrid` reports `Name=''` (only
> an `AutomationId` of `NamesGrid`, which is the `x:Name`, not a label).

Count of `AutomationProperties.Name` per surface:

| window | names | window | names |
|---|---|---|---|
| Settings | 38 | ZipTools | 5 |
| LabelMaker | 12 | PrintPreview | 3 |
| Unlock | 6 | Triage | 1 |
| About / BulkRename / History / ManageSaved / MatchMerge | 1–2 | **FilenameList** | **0** |
| | | **ListReformat** | **0** |
| | | **PageCounts** | **0** |

Settings shows the standard the app is capable of — even the `✕` chip carries a
bound name. The gap is concrete, not abstract:

- **ListReformat** — both large text areas (the pasted list and the result) are
  unlabelled; "Paste your list:" and "Result:" are bare `TextBlock`s with no
  programmatic relationship to them.
- **FilenameList** — the `Find:` box has no name, while History's identical box
  has `"Find in history"`.
- Every `DataGrid` in the app is unnamed: `NamesGrid`, `CountsGrid`,
  `ItemsGrid`, `ZipsGrid`, `Candidates`, `HistoryGrid`.

### UI-05 — no access keys and no `Label.Target` outside the menu bar — **PARTLY ADDRESSED 2026-08-23**

> **Confirmed live:** no control in the inspected window reports a `LabeledBy`
> at all — the bare-`TextBlock` labels create no programmatic association, as
> predicted.

A sweep for `Content="…_x…"` across all 13 tool windows returns **zero**
matches, and `<Label Target=…>` appears nowhere in the app. Only the main menu
(`_File`/`_Tools`/`_Help`), the six Settings tabs and the two ZipTools tabs
carry mnemonics.

**Half of this is now closed.** The screen-reader half — every control able to
say what it is — is done and guarded (UI-04, `AccessibleNameTests`): naming the
control directly is the mechanism assistive tech actually reads, and it needs
no `Label.Target`. What remains is the KEYBOARD half: no Alt+key reaches any
button in any dialog. That is a separate change (an access key per button, kept
unique per window) and is still open.

Consequences of the remaining half: no Alt+key reaches any button in any
dialog, and every field label is still a bare `TextBlock` rather than a `Label`
with a `Target`. Keyboard-only
operation of Settings — the most control-dense window — is Tab-walking only.

### UI-06 — Settings discards every edit without asking

`Windows/SettingsWindow.xaml:160` (`Cancel`, `IsCancel="True"`),
`SettingsWindow.xaml.cs:20` (`OnOk` is the only exit that commits)

Six tabs of editing — destinations with paths, hotkeys, suffixes and colours;
monitored folders with sections, filetypes and colours; alert terms; appearance
— and Esc, Cancel, or the window's X throws all of it away with no dirty
tracking and no confirmation. Esc in particular is muscle memory from every
other window in this app, where it is always safe.

### UI-07 — starting a batch slides Cancel under the cursor that just clicked Unlock

`UnlockWindow.xaml` — **FIXED 2026-08-22**

**Corrected twice.** The first draft claimed "Manage saved…" gets shoved; the
live pass showed it does not (0.0px) and that the primary **Unlock** button
moves instead. Writing the guard then corrected it again: the draft listed four
sites, and **only one of them moves.**

Measured across all four by `TransientFooterButtonTests`:

| Site | Panel | Moves? |
|---|---|---|
| **Unlock** — Cancel | right-aligned `StackPanel`, Cancel 2nd of 4 | **yes — Unlock −104px** |
| BulkRename — Cancel | left-aligned fill `StackPanel`, Cancel after both buttons | no |
| History — "Show all" | `DockPanel`, docked children take from the remaining rect | no |
| MainWindow — Refresh | combo beside it is gated on the same state and goes with it | no |

The rule the draft was missing: a **left**-aligned StackPanel grows rightward
and displaces only children *after* the insertion; a **right**-aligned one has
its right edge pinned and displaces the children *before* it; a `DockPanel`'s
docked children cost their neighbours nothing and the fill child absorbs the
difference. Unlock was the only footer that was both right-aligned and had its
transient button part-way along.

What that produced:

```
Cancel at rest = Collapsed, width=96, margin=0,0,8,0
  Unlock (primary)   X  250.0 ->  146.0   moved  -104.0px
  Manage saved...    X  368.0 ->  368.0   moved     0.0px
  Close              X  494.0 ->  494.0   moved     0.0px
  Unlock WAS at [250..360]; Cancel is NOW at [264..360]
  => 96px of 110px (87%) of the old Unlock footprint is now Cancel
  Unlock button IsEnabled during run: True
```

Click **Unlock**, and the button you clicked jumps 104px left, leaving 87% of
the pixels under the cursor occupied by Cancel — on a button that stays enabled
throughout. A double-click or a slow release cancelled the batch it just
started.

**Fixed** by giving Unlock the footer shape every other batch tool already had
(`DockPanel`, Close docked right, actions in the left-aligned fill child) with
Cancel last in the row, so nothing sits after it to be pushed. Unlock had been
the only outlier of the six batch tools, and the difference was never cosmetic.
Guarded by `TransientFooterButtonTests`, which asserts the property rather than
the mechanism: showing a transient footer button moves no other button.

### UI-08 — Enter in Manage saved passwords closes the window and loses the entry

`Windows/ManageSavedWindow.xaml:10` — Close carries `IsDefault="True"`, and
neither `NewPwLabel` nor `NewPwValue` has a Return binding. Type a name, type a
password, press Enter — the window closes and both are gone.

Settings' "New alert term" box solved exactly this with a local
`<KeyBinding Key="Return" Command="{Binding AddAlertCommand}" />`
(`SettingsWindow.xaml:1258`). The same three lines fix this.

### UI-09 — two windows are taller than a 768px screen, with no work-area clamp

`PrintPreviewWindow` (Height 840) · `SettingsWindow` (820)

**Corrected by the live pass.** TriageWindow at 720 was in the first draft and
should not have been: a 768px screen leaves roughly 728 usable, so it fits. Two
windows exceed it, not three.

`MainWindow` is the only window that clamps (`MaxHeight = WorkArea.Height - 24`,
MainWindow.xaml.cs:244). The live pass confirmed the placement behaviour
directly: Settings opened at **Top = 0** rather than centred, because
`CenterOwner` against the compact dashboard computes a negative Y and Windows
clamps the top edge. On a screen shorter than the window, that same clamp sends
the overflow entirely off the *bottom* — where OK and Cancel live.

This machine's work area is 5120x1392, so it cannot reproduce the end state; the
finding is arithmetic plus the confirmed placement behaviour, not a screenshot.

### UI-10 — `HistoryWindow` is the only tool window that shows in the taskbar — **FIXED 2026-08-23**

`Windows/HistoryWindow.xaml:4` — every other tool window sets
`ShowInTaskbar="False"`; this one doesn't, so it alone adds a second OrdoSort
entry to the taskbar.

### UI-11 — two different controls, two different labels, for the same "pick columns" job

- `FilenameListWindow.xaml:91` — a `Menu` whose item is `Header="Columns ▾"`
  (a literal ▾ character baked into the header string).
- `MatchMergeWindow.xaml:115` — a `ToggleButton` labelled `"Columns…"` driving
  a `Popup`.

Same concept, same window family, different affordance, different label,
different glyph strategy. The `…` on MatchMerge's is also slightly wrong by
convention — it opens a popover, not a dialog.

### UI-12 — in PageCounts every button is live even with an empty list — **FIXED 2026-08-23**

**Corrected by the live pass.** The first draft's table claimed PageCounts'
Clear and "Save as .txt..." disable correctly. They do not. Measured with zero
rows and no selection:

```
  Remove selected      IsEnabled=True  (rows=0, selection=none)
  Clear                IsEnabled=True  (rows=0, selection=none)
  Copy to clipboard    IsEnabled=True  (rows=0, selection=none)
  Save as .txt...      IsEnabled=True  (rows=0, selection=none)
  Add PDFs...          IsEnabled=True  (rows=0, selection=none)
```

The cause is in the view model: `SaveCommand = new RelayCommand(Save)` and
`ClearCommand = new RelayCommand(...)` are constructed with **no CanExecute
predicate** (`PageCountsViewModel.cs:78-79`), so they can never disable. "Save
as .txt..." on an empty list opens a save dialog and writes an empty file.

The wider pattern is real, just narrower than first stated. Where a command
*does* carry a predicate it works correctly:

| Window | Gated correctly | Ungated |
|---|---|---|
| MatchMerge | `Merge` (`MergeCount > 0`), `Undo` (`_outcomes.Count > 0`) | `LoadRoster`, `Clear`, every `Click` handler |
| ZipExtract | `Zip`/`ZipAs` (`Rows.Count > 0`), `Extract` (`PendingZips > 0`) | `Clear`, all `Click` handlers |
| MergePdfs | `Merge` (`Rows.Count > 0`) | `Clear` |
| Unlock | `Unlock` (`Files.Count > 0`), `Cancel` (`IsUnlocking`), `RemoveSaved` (selection) | `Clear`, `Add files...` |
| **PageCounts** | *(none)* | **everything, `Save` included** |
| **FilenameList** | *(none)* | `Save`, `RemoveSelected`, `UndoRemoval`, `RestoreRemoved` |

An ungated `Clear` is harmless — clearing an empty list is a no-op. The two that
matter are **`Save` on an empty list** (both windows) and FilenameList's
removal-stack commands: the live probe showed `UndoRemovalCommand.CanExecute`
returning **True** with zero rows loaded and nothing ever removed.

---

## Found only by running it

Five findings the source read could not have produced. Four of them appear
because the live config sets `ui_font_family: Consolas` — a **non-default font
family**, an axis nothing in the test suite varies.

### UI-25 — the destination folder path is truncated to a third of itself (Medium) — **MITIGATED 2026-08-23**

Settings ▸ Destinations, the `Folder:` field. The configured value for the
"Invoices" route is `S:/OrdoSort/demo-full/routes/01-invoices` (38 characters).
The field renders **`S:/OrdoSort`** — 11 characters, 29% of the value — because
`Browse…` and `Open` sit in the same row and take their width first.

It is a `TextBox`, so nothing is lost and the text scrolls when focused. But the
single most important fact on the tab — *where do files actually go* — is
unreadable at a glance, with no ellipsis and no tooltip. Every route looks
identical in this field, since they all share the same first 11 characters.

`WindowOverflowTests` cannot catch this: it asserts that no text **escapes the
window**, and a `TextBox` clipping its own content internally escapes nothing.

### UI-26 — the Hotkey hint wraps to four lines and quadruples its row (Low) — **FIXED 2026-08-23**

Same tab. `press the keys · Backspace clears` is squeezed into a column narrow
enough that it breaks as `press the / keys · / Backspace / clears`, stretching
the Hotkey row to roughly four times the height of the rows above and below it
and leaving a tall, mostly empty input box beside it.

### UI-27 — raw .NET exception text is shown to the user (Medium) — **FIXED 2026-08-23**

`App.xaml.cs:62`. Launching with a malformed config produces, verbatim:

> Config file C:\…ad-config.json is not valid JSON: Expected depth to be zero
> at the end of the JSON payload. There is an open JSON object or array that
> should be closed. Path: $.inbox | LineNumber: 1 | BytePositionInLine: 0.

`Path: $.inbox | LineNumber: 1 | BytePositionInLine: 0` is a
`System.Text.Json` diagnostic, not a sentence anyone can act on. The first
clause is good and the app's own; everything after the colon is an exception
message passed straight through. This is the failure mode that greets a user
whose config got corrupted — the moment the app most needs to say what to do
next.

### UI-28 — the one advertised shortcut is invisible to screen readers — **FIXED 2026-08-23** (Low)

`MainWindow.xaml:295` sets `InputGestureText="Ctrl+,"` on Settings…, and the
binding really is registered. But UI Automation reports `AcceleratorKey=''` for
**every** menu item in all three menus — WPF only populates that property from a
`RoutedCommand`'s gesture, not from `InputGestureText` on a `Click`-handler
item. Measured live:

```
File (4 items):
    'Settings…'   accel=''  access='S'
    'View history…'   accel=''  access='V'
    ...
```

So the app's only advertised accelerator is announced to sighted users and
withheld from everyone else. (The same enumeration confirmed the access keys are
collision-free: File S/V/E/X, Tools U/B/M/X/F/C/L/Z, Help A.)

### UI-29 — the overflow suite varies font size but never font family (Medium, test gap) — **FIXED 2026-08-23**

`WindowOverflowTests.Cases()` runs every window at 14px and 18px — both in the
**default** `Segoe UI Variable Text`. Consolas at the same nominal size is
substantially wider per character, and that is precisely what produces UI-25 and
UI-26 and what makes the UI-17 glyph fallback visible.

Font family is a first-class, user-editable setting on the Appearance tab with a
free-text font picker. It is the one appearance axis with no coverage at all.

**Fix:** add a font-family dimension to `Cases()` — a monospace face and a wide
UI face alongside the default — and, separately, an assertion that a bound
`TextBox` shows its whole value or carries an ellipsis and tooltip, which is the
class of defect UI-25 belongs to and the current probe is blind to.

---

---

## Found while fixing (2026-08-23)

### UI-30 — the destination Folder box measures ZERO at the window's own minimum width (Medium)

Discovered by the guard written for UI-25. The audit reported the field showing
about a third of its value at the default width; measured at the window's
declared `MinWidth` of 760 in Consolas, the column resolves to **0px** — the
box is not narrow, it is absent, and it holds the most important value on the
tab.

A `MinWidth="120"` floor on that column was tried and reverted in the same
pass: it pushes **Browse…**, **Open** and **Create it** clean off the window,
trading an unreadable field for unreachable buttons. The row simply cannot fit
a SharedSizeGroup label, a path and two buttons at 760px in a wide font.

That makes it a layout change rather than a column tweak — the buttons need to
wrap under the field, or the label needs to stop sharing the group, and the row
also carries the Problem/Create-it sub-row whose alignment several existing
comments were written to protect. Out of scope for the pass that found it.

The ToolTip added for UI-25 is the mitigation and `FieldClippingTests` holds it
in place, so the value is at least recoverable at any width.


## Low / polish

- **UI-13** *(fixed)* Double space in visible copy: `BulkRenameWindow.xaml:58` —
  `"Review files: rename to  <received date>-LAST-FIRST"`.
- **UI-14** *(fixed)* The same label, two capitalisations: `"Include subfolders"`
  (FilenameListWindow.xaml:73) vs `"include subfolders"`
  (SettingsWindow.xaml:1124).
- **UI-15** *(fixed)* Lowercase-initial labels against the app's sentence-case norm:
  LabelMaker's `"black bars (white text)"` / `"plain (black text)"`, Settings'
  `"always the first destination"` / `"the last-used destination (starts at the
  first)"` / `"last"`.
- **UI-16** *(fixed)* Empty states say "press Add…" (PageCounts, ZipTools ×2, Unlock,
  LabelMaker) where the gesture is a click.
- **UI-17** Icon vocabulary is split. Segoe Fluent glyphs (`&#xE7xx;`) are used
  27 times through the `Icon` style, but raw Unicode characters appear
  alongside them: `✕` (SettingsWindow.xaml:1240 — MainWindow's dismiss uses
  `&#xE711;` for the same job), `↑`/`↓` (Settings ×4), `⏎` (ProcessingView.xaml:142),
  `▾` (FilenameList), `✓` (swatch check). The app font is
  **user-configurable**, so these depend on font fallback in a way the Segoe
  glyphs do not.
- **UI-18** Chip presets announce a bare number to a screen reader —
  `AutomationProperties.Name="5"`, `"12"`, `"10"`. No unit, no context. Should
  be "Every 5 seconds", "Text size 12", "10 labels".
- **UI-19** `ReadyView.xaml:48–164` — the tile entrance cascade is 8
  near-identical `DataTrigger` blocks, 116 lines, to express
  `BeginTime = index × 30ms`. A one-line `IValueConverter` on
  `AlternationIndex` replaces the lot.
- **UI-20** `ReadyView.xaml:226` — tiles carry `Margin="0,0,6,6"` inside a
  `UniformGrid`, so the tile block sits 6px short of its container on the right
  and bottom while being flush at left and top.
- **UI-21** Numeric fields are bare `TextBox`es with no `MaxLength`, numeric
  restriction, or spinner: PrintPreview's Copies, LabelMaker's Keep-boxes-days
  / Next-label-number / Labels-to-print, Settings' poll seconds and font size.
  Validation lives in the view models, which is correct, but the input
  affordance says nothing.
- **UI-22** *(fixed)* `PrintPreviewWindow` has no `IsDefault` button — Enter from the
  Copies box does nothing.
- **UI-23** Help holds a single item. The app ships real hotkeys (Ctrl+K,
  Ctrl+Shift+Z, Esc, Ctrl+,, per-route hotkeys, 1–9/S/Enter in Triage) and
  documents them only in scattered inline hints. A "Keyboard shortcuts" item
  belongs here.
- **UI-24** About carries product name, version and a third-party-notices
  button — no copyright, licence, or website line.

---

## The open question, settled

`FilenameListWindow.xaml:11` binds **Ctrl+Z** at window level to
`UndoRemovalCommand`, and the window contains editable text boxes. The first
draft flagged this rather than asserting it.

Answered on the running app with real keystrokes: focus placed in the Find box
via UI Automation, `HELLO` typed, then a real Ctrl+Z.

```
focused: type=ControlType.Edit  inSameWindow=True
after typing 'HELLO' : 'HELLO'
after Ctrl+Z         : ''
```

The TextBox's own undo handled the key and marked it handled; the window-level
binding never fired. **Not a defect** — WPF routing does what it should, and
MainWindow's Ctrl+Shift+Z was belt-and-braces rather than a necessary dodge.

## Suggested order

1. **UI-01** and **UI-02** — both are violations of contracts the project states
   about itself, and UI-02 sits on every error path. **UI-27** rides along with
   UI-02: the same dialog, better words.
2. **UI-03** — a delete, and it restores a README claim.
3. **UI-07**, **UI-06**, **UI-08** — the three that lose the user work or
   actively invite the wrong click. UI-07 is a one-word change
   (`Collapsed` → `Hidden`).
4. **UI-29** — a test-axis gap that is currently letting UI-25, UI-26 and UI-17
   ship unseen. Fixing it before the cosmetics means the cosmetics stay fixed.
5. **UI-25**, **UI-26** — what UI-29 was missing.
6. **UI-04**, **UI-05**, **UI-28** — one accessibility pass across the five weak
   windows and the menu bar.
7. **UI-12** — add the two missing `CanExecute` predicates.
8. The rest as a single copy and consistency sweep.
