# Testing

How OrdoSort is tested, how to run each kind, and the rules that keep the everyday run fast and reliable. Replaces `docs/known-flakes.md`.

## Kinds of tests

| Kind | Where | Run with | Needs |
|---|---|---|---|
| Core unit tests | `tests/OrdoSort.Core.Tests` | `check.bat core` | nothing |
| WPF tests (view models, real windows off-screen) | `tests/OrdoSort.Wpf.Tests` | `check.bat wpf` | nothing |
| Integration (real Edge/WebView2, real Office) | classes tagged `[Trait("Category", "Integration")]` | `check.bat integration` | WebView2 runtime; Office for the Office tests |
| Network share (two stations saving over a real Windows share) | classes tagged `[Trait("Category", "NetworkShare")]` in `tests/OrdoSort.Core.Tests` | `check.bat share` | the test share, below |
| End-to-end (the real app, driven) | `tools/OrdoSort.Smoke` | `scripts\e2e.bat` | a desktop session |

`check.bat` runs everything except Integration and NetworkShare: format check, Release build, then the tests from a Debug build in `artifacts\check` (see the comment at the top of `check.bat` for why). `check.bat all` adds Integration. CI runs the everyday set on Release plus its own `integration` job; it never runs NetworkShare (no share there).

## The test share

The NetworkShare tests make this PC a file server with two "stations": you through `\\localhost\OrdoSortTest$`, and a local user with plain Modify rights (`OrdoSortShareTest`) through `\\127.0.0.1\OrdoSortTest$`. Windows keeps one sign-in per server name, so the two names give two users. `\\127.0.0.1\OrdoSortTestRO$` is the same folder, read-only for the test user. A test fails, naming these steps, when the share isn't ready.

| Step | Command | When |
|---|---|---|
| Create the user, the two hidden shares and the stored password | `powershell -ExecutionPolicy Bypass -File scripts\share-test-setup.ps1`, as administrator | once |
| Sign in to `\\127.0.0.1` as the test user | `powershell -ExecutionPolicy Bypass -File scripts\share-test-connect.ps1`, without admin | after each Windows sign-in, before anything else opens `\\127.0.0.1` |
| If `127.0.0.1` is already open as you (connect fails with 1219) | as administrator: `Get-SmbSession \| Where-Object ClientComputerName -match '^(127\.\|\[?::1)' \| Close-SmbSession -Force`, then connect again | when needed |
| Remove it all | `scripts\share-test-teardown.ps1`, as administrator | when done |

What it covers today: a station saving the config another station wrote last (the office "access denied", fixed in `f80c880`; the test fails with that exact message when the fix is taken out), and a save to a read-only share. A dropped, hung or slow share needs a server that can be stopped or slowed (a Samba container behind Toxiproxy is the plan); not set up yet.

## Rules

| Rule | How |
|---|---|
| No real time | No `Thread.Sleep` and no "took < N ms" asserts. Code with a debounce or timer takes an optional `TimeProvider`; tests pass `ManualTimeProvider` (`Support/`) and call `Advance`. Work goes through `IWorkScheduler`; tests pass `InlineWorkScheduler` (runs at once) or `ManualWorkScheduler` (holds work until `Release`/`ReleaseAll`) |
| One way to wait | When something real must be waited for (an OS file event), use `WaitFor(condition, because)` from `tests/Shared/Wait.cs`: 30 s ceiling, returns as soon as the condition holds |
| Deterministic windows | A view model built in a window test gets `scheduler: new InlineWorkScheduler()` (and a manual clock if it debounces). Nothing touches bound objects from a pool thread |
| Clean shared UI thread | Every class in the `HighlightContrastTests.Name` collection derives from `UiTest`, which closes leftover windows, clears focus, restores app resources and the light theme before each test. `UiTestTests` fails if a class joins the collection without it |
| Hermetic | Temp files via `TempDir` (`tests/Shared/`). `TestAssemblySetup` points the WebView2 profile, crash folder and table layouts at one per-run temp folder; `TestRunIsHermeticTests` guards it. Nothing is written under `%LOCALAPPDATA%\OrdoSort` |
| Real Edge/Office only in Integration | `MainWindow` and `TriageWindow` have an internal `initViewer` seam; window tests pass `() => Task.FromResult(true)` |
| Bounded | `HighlightContrastFixture.Invoke` fails a test body after 2 min and says whether the UI thread is in a dialog or deadlocked; the blame collector in `tests/test.runsettings` ends a test after 5 min |
| Shared helpers | `tests/Shared/` (both projects): `Wait`, `TempDir`, `Repo.Root`. `tests/OrdoSort.Wpf.Tests/Support/`: `Ui` (tree walks, bounded pumps, `Settle`), `UiTest`, `ManualTimeProvider`. Use these; don't copy helpers into test files |

## Reading a run

- [ ] Read the `Passed!` line and its **Total**. A run that stops early can exit 0 with a low total. `tests/test.runsettings` makes a zero-test run fail.
- [ ] `An Application Control policy has blocked this file` or a zero-test run: Windows blocked that exact build by hash. Rebuild with `--no-incremental`; Debug builds get a new hash each time, Release builds don't (that is why `check.bat` tests from Debug).
- [ ] After temporarily breaking product code to test a test, restore the file with `cp` or `touch` it. `mv` keeps the old timestamp, and the build keeps the broken DLL.
- [ ] Many UI tests failing in 1 ms after one failing at 2 min: the shared UI thread got stuck on the first one. Its message says what it was doing; fix that test, not the others.
- [ ] A run that hangs with stray `dotnet.exe`/`testhost.exe` processes from earlier runs: end those first.
- [ ] A test is flaky only if the identical binary passes on a re-run. Fails twice: it is a defect.

## Known flakes

| Test | Status | Notes |
|---|---|---|
| `HeaderLayoutTests` (MainWindow) | ✅ Done | The hang was a modal start-up warning ("that didn't finish") shown by the real dialog service. Every test-built MainWindow now uses `FakeDialogs`, and HeaderLayoutTests fails at once, quoting the warning and crash.log, if the dashboard warns while starting. The start-up failure itself is not yet identified |
| `FocusRingCoverageTests.TabItemShowsTheBronzeFocusRing` | ⬜ Not started | Never reproduced since 2026-08-15. If it fails, the message says why: `never accepted keyboard focus` = focus stolen; `pixels already in the band BEFORE it was focused` = the tab became selected; `NO AccentBronze pixel appears` = a real style regression; `no focus-visual adorner` = `AlwaysShowFocusVisual` broke |
| `BulkRenameBatchTests` | ✅ Done | 3 s waits raised to the shared 30 s ceiling (2026-09-27). Still uses real timers internally |
| `FolderPathResolutionTests.SettingsWarningsCheck…` | ✅ Done | Its own background write probe held the folder, not antivirus. Inline scheduler, manual clock, dispose first |
| `BulkRenameSortedNavigationTests`, `CultureInvariantDatesTests`, `DeleteKeyTests` | ✅ Done | Manual clock and inline scheduler; the culture tests now actually build the preview under the culture they set |
| `SettingsViewModelTests` path checks, `BulkRenameProbeTests`, `TilePreviewProbeTests`, `FolderWatchServiceTests`, `DebouncedProbeTests` | ✅ Done | No sleeps or wall-clock asserts; manual clock and scheduler |
| `UnlockEnterKeyTests`, UI tests seeing leftover windows or focus | ✅ Done | `UiTest` reset |
| `WebViewPdfViewerGuardBehaviourTests`, MainWindow hang on real Edge | ✅ Done | Own WebView2 profile per run; real-Edge tests are Integration |

## Environment notes

| Symptom | Meaning |
|---|---|
| `Test host process crashed … Failed to unregister class Chrome_WidgetWin_0` | WebView2 teardown in the test host (seen 2026-08-21). Re-run; everyday tests no longer start Edge, so expect it only in `check.bat integration` |
| All `WebViewPdfViewerGuardBehaviourTests` fail with `Class not registered` | The WebView2 runtime is missing on the machine |
| A burst of `XamlParseException: Cannot find resource named …` | A window built on a second thread while the shared test Application loads styles; fixed 2026-09-24. If it returns, look for a test building a window outside the fixture |
