# End-to-end demonstration suite

**Date:** 2026-08-09
**Status:** approved, ready for planning

## Problem

OrdoSort has 1,056 automated tests — 466 in `OrdoSort.Core.Tests`, 590 in
`OrdoSort.Wpf.Tests` — and every tool has both a Core test file and a view
model test file. That coverage is real, but it stops one layer short of the
thing a person actually uses. The Core tests call `Zipper.CreateZip` directly.
The WPF tests construct a view model and assert on its properties. Neither one
opens `ZipWindow`, and neither one proves that the window, the view model, the
core routine, and the filesystem agree.

`tools/OrdoSort.Smoke` does cross that line, but only for the routing loop: it
boots the real `MainWindow`, waits for WebView2, and drives commit / set-aside
/ undo. Ten Tools-menu utilities, two Reports, and History have never been
driven as real windows against real files.

So there is no single command that demonstrates the application works.

## Goal

One command that exercises every user-facing surface end to end against real
files on disk, asserts the results, and emits an evidence report a person can
read — screenshots included — plus a process exit code CI can gate on.

## Approach

A new `e2e` mode in `tools/OrdoSort.Smoke`.

The alternatives were a separate xUnit E2E project and black-box UI automation.
The xUnit route fights the repo: `OrdoSort.Wpf.Tests` deliberately does not
boot an STA `Application`, and real windows under a test runner mean one
`Application` per process, fragile shutdown, and screenshots that are awkward
to attach. UI automation (FlaUI/Appium) adds a dependency and breaks on
cosmetic XAML changes, which is a poor trade for an app whose UI is under
active redesign across seven schemes.

The Smoke tool already has what this needs:

- `SmokeUi.Boot()` — loads real `App.xaml` resources and the theme without
  running the app's startup path, so windows resolve resources as in
  production.
- `SmokeUi.RunSta(drive, passLine, failHead)` — an explicit STA thread with
  failure collection and exit code.
- `RecordingDialogs : IDialogService` — records modals instead of showing them;
  a blocked message loop would hang the harness.
- `Screenshots.Capture(...)` — forces a layout pass and rasterizes a real
  window with `RenderTargetBitmap`.

## The rule that makes this a demonstration

Every tool view model accepts a seam for the work it does:

| View model | Seam parameter |
|---|---|
| `ZipViewModel` | `Func<IReadOnlyList<string>, string?, Zipper.ZipResult>? zipper` |
| `UnzipViewModel` | `Func<string, Zipper.UnzipResult>? extractor` |
| `ZipMergeViewModel` | `Func<string, ZipMerge.MergeResult>? merger` |
| `PageCountsViewModel` | `Func<string, PageCounts.CountResult>? counter` |
| `UnlockViewModel` | `unlocker`, `fileSize`, `tryReveal`, `probe` |
| `BulkRenameViewModel` | `plan` |

**Scenarios MUST leave every one of these at its default.** The only
dependencies an E2E scenario may inject are `IDialogService` (to answer modals
without blocking) and `IWorkScheduler` (to run work inline so assertions need
no sleeping). Injecting a work seam turns the run into a mock theatre that
proves nothing — this is the single rule that separates this suite from the
view model tests that already exist.

Where a scenario needs a dialog to answer differently per case — a save-as
path, a confirm, a chosen folder — it supplies a `ScriptedDialogs`
`IDialogService` whose answers are queued per scenario. That is answering the
user's side of a prompt, not replacing the app's work.

### Waiting, not sleeping

Five surfaces (Filename list, Page counts, Bulk rename, and both Reports) load
through `DebouncedProbe<T>`. As `FilenameListViewModelTests` documents, results
are only *eventually* correct even with `InlineWorkScheduler` and
`probeDelayMs: 0`, because the underlying `System.Threading.Timer` still fires
on a threadpool thread. The existing view model tests poll with
`Thread.Sleep`, which is fine off the UI thread.

The E2E harness cannot do that. Its scenarios run on the STA dispatcher thread
that owns the windows, so sleeping there blocks the very message loop the probe
needs to marshal its result back through `uiContext`, and the wait deadlocks
until it times out. Scenarios must instead pump the dispatcher —
`Screenshots.Pump(ready, timeoutMs)` already implements exactly this with a
`DispatcherFrame` and a background-priority `DispatcherTimer`, and moves into
the shared E2E helper. View models are constructed with
`uiContext: SynchronizationContext.Current` so their results marshal back to
the dispatcher thread as they do in production.

## Architecture

```
tools/OrdoSort.Smoke/
  E2E/
    E2ERunner.cs        registry, STA boot, per-scenario isolation, exit code
    Scenario.cs         record: Name, Surface, Kind, Arrange, Act, Assert
    Fixture.cs          temp-root builder + teardown; PDF/zip/csv/xlsx makers
    ScriptedDialogs.cs  IDialogService with queued, per-scenario answers
    Evidence.cs         report.html + report.md writer, PNG capture
    Scenarios/          one file per surface (ZipScenarios.cs, ...)
```

`Program.cs` gains one line beside its existing modes:

```csharp
if (args.Length > 0 && args[0] == "e2e") return E2ERunner.Run(args);
```

Invocation: `dotnet run --project tools\OrdoSort.Smoke -- e2e [surface] [--keep]`
— no surface argument runs everything; a surface name (`zip`, `unzip`,
`zipmerge`, …) runs one; `--keep` preserves fixtures for inspection instead of
tearing them down.

### Scenario shape

Each scenario is a record with four phases:

1. **Arrange** — build an isolated fixture under
   `%TEMP%\ordo_e2e_<guid>\<scenario>\`. Never `demo-full\`, never a real
   document. Fixtures are generated in code (`MinimalPdf` already exists for
   PDFs) so the suite has no binary test assets to maintain.
2. **Act** — construct the real view model with real seams, construct the real
   `Window`, drive the same commands the buttons bind to.

   Fixture generation has everything it needs already: `MinimalPdf.Write` for
   plain PDFs, `PdfSharp` (referenced by `OrdoSort.Core`) for encrypted and
   multi-page ones via `SecuritySettings.UserPassword`, and
   `System.IO.Compression` for archives — the same approach
   `UnlockProbeTests.MakeEncrypted` already uses. No binary assets are checked
   in.
3. **Assert** — check the filesystem first (expected files exist at expected
   paths, sources are gone or preserved as the feature promises, nothing was
   overwritten), then view model state (`Status`, row counts, row status
   kinds), then recorded dialogs.
4. **Capture** — rasterize the window to
   `evidence/<timestamp>/<surface>-<scenario>.png`.

Failures are collected, not thrown: one broken scenario must not abort the run,
because a partial evidence report is the thing you most want when something
breaks.

### Isolation

Every scenario gets a fresh fixture directory and a fresh view model. Windows
are closed and fixtures deleted in a `finally`. The suite writes only under its
temp root and the `evidence/` output directory — a scenario that writes
anywhere else is a bug in the scenario.

## Coverage — 14 surfaces

Each surface gets one clean run that proves it works plus the cases that break
naive implementations.

**Zip** — files and folders mixed in one archive; default name derivation
(`Zipper.DefaultName`); save-as to an explicit path; output colliding with an
existing archive (must counter, never overwrite); unicode and spaces in entry
names; empty selection (button disabled, no archive written).

**Unzip** — clean extract with nested folders; **zip-slip path traversal**, an
entry named `..\..\evil.txt` must produce `status = "error"`, write nothing
outside the output directory, and leave no orphaned output folder behind (the
`created`-gate cleanup in `ExtractCore`); corrupt archive reports `"not a valid
zip"` and writes nothing; extraction target already exists (counters via
`Collision.FreeDirectory`); empty archive; unicode entry names.

Password-protected archives are deliberately absent: `System.IO.Compression`
can neither create nor read them, and pulling in a zip library to fabricate the
fixture would contradict the app's no-bundled-dependencies design goal. The
feature does not claim to support them.

**Zip merge** — zip of PDFs merges to one document in page order; zip with no
PDFs reports `NoPdfs` and writes nothing; zip containing an encrypted PDF; zip
with mixed content (PDFs plus other files); corrupt PDF inside an otherwise
good archive; multi-zip batch where one row fails and the others still succeed.

**Unlock PDFs** — correct password unlocks; wrong password fails and **leaves
the original byte-identical** (the never-overwrite guarantee, which
`UnlockNeverOverwritesTests` asserts at unit level and this proves through the
window); already-unlocked file; probe writes nothing.

**Bulk rename** — a planned rename applied to real files; output name
collisions counter rather than overwrite; illegal filename characters rejected
before commit; a rule producing an empty name is refused.

**Match and merge** — a roster CSV matched against real PDFs; ambiguous rows
open Review matches (`TriageWindow`); rows with no match; suggested rows.

**Box labels** — labels generated from the store; print preview renders
(`PrintPreviewWindow`).

**Filename list** — list produced from a real folder; unicode names; empty
folder.

**PDF page counts** — counts across a folder of real PDFs; encrypted PDF row;
corrupt PDF row; the good rows still report.

**List reformatter** — reformat with blank lines, duplicates, and unicode
input.

**Turn-around time report** — a folder of PECF report spreadsheets (csv and
xlsx) loaded via `AddPaths`, column mapping applied, aggregates computed; a
document dated after its own upload (negative TAT, shown as-is rather than
clamped); unparseable dates rendering `—`; empty sources resetting to the empty
table.

**Production report** — the same spreadsheet sources with group-by and sum
column picks; empty sources; a sum column holding non-numeric values.

Note: both Reports read spreadsheets through `SweptTable.Load`, **not**
`history.sqlite`. Only the History surface touches the audit database.

**History** — the window loads real rows; export to spreadsheet writes a real
file (`XlsxTable`).

**Routing loop** — the existing `Drive()` scenario from `Program.cs`, folded in
as an E2E scenario so the report covers the whole application rather than only
the tools. Its WebView2 dependency makes it the one scenario that can be
skipped by surface filter on a machine without a desktop session.

## Evidence output

`evidence/<yyyyMMdd-HHmmss>/` containing:

- **`report.html`** — self-contained, no external assets. A summary header
  (counts, duration, pass/fail), then one section per surface, then one row per
  scenario: name, verdict, the assertions that ran with their outcomes, and the
  screenshot inline as a base64 `data:` URI so the file can be mailed or
  attached and still render.
- **`report.md`** — the same content as text, for pasting into a PR or issue.
- **`*.png`** — the screenshots as loose files too, for reuse in docs.

Console output ends with `E2E PASS — <n> scenarios, <m> surfaces` or `E2E
FAIL:` followed by one line per failure, matching the existing smoke modes'
convention. Exit code 0 or 1.

`evidence/` is added to `.gitignore` — it is a build product, regenerated on
every run.

## Error handling

- A scenario that throws is recorded as a failure with its exception message
  and the run continues.
- A window that fails to construct is a failure for that scenario only.
- The existing 75-second watchdog pattern is applied per scenario rather than
  per run, so one hung window cannot consume the whole budget.
- Fixture teardown runs in `finally` and its own failures are reported but do
  not change the run's verdict.

## Testing the tests

The harness itself gets unit coverage in `OrdoSort.Core.Tests` /
`OrdoSort.Wpf.Tests` where it is testable without a window:

- `Fixture` builds and tears down under the temp root and nowhere else.
- `Evidence` produces valid self-contained HTML from a known scenario list,
  including a failing one.
- `ScriptedDialogs` returns queued answers in order and reports unconsumed
  answers (an unconsumed answer means the scenario did not exercise the path it
  claimed to).

A deliberately failing scenario, run behind a flag, confirms the runner reports
failure and exits nonzero — a suite that cannot fail is not a suite.

## Non-goals

- Not replacing the 1,056 existing tests. This sits above them.
- Not a performance or load benchmark.
- Not pixel-diff regression testing. Screenshots are evidence for a human, not
  assertions; the theme already has its own contrast tests.
- Not testing WebView2 itself beyond the one existing routing scenario.

## CI

The suite needs a desktop session for `RenderTargetBitmap` and WebView2, so it
runs as a job separate from the headless unit tests, on
`windows-latest`, with `evidence/` uploaded as a run artifact. The unit test
job is unchanged and remains the fast gate.

Note the known local constraint: Smart App Control blocks test assemblies by
hash, so builds use `-p:Deterministic=false` and tests run `--no-build`.

## Success criteria

- `dotnet run --project tools\OrdoSort.Smoke -- e2e` exits 0 on a healthy tree.
- Every one of the 14 surfaces appears in the report with at least one clean
  scenario and at least one awkward case.
- No scenario injects a work seam.
- Breaking a core routine on purpose (e.g. making `Zipper.CreateZip` overwrite)
  turns the run red and names the scenario.
- The HTML report opens standalone and shows its screenshots.
