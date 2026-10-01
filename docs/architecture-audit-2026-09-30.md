# Architecture audit (2026-09-30)

A read of the whole codebase on `feature/media-loop` (`bb7c5b0`) for structure, duplicate logic, speed, scale and upkeep. It is not another bug hunt: rows already in `STATUS.md` or `docs/superpowers/refinement-master-checklist.md` are not repeated, and the declined rows (DW-23, DW-39, DW-53, Q2-21, Q2-39) are not re-opened.

## How it was done

| Area | Read by | Size |
|---|---|---|
| Filing loop: `ShellViewModel*`, `Session`, `Commit`, `Scanner`, `Intake`, `History`, `DocumentStage`, `FolderMonitor`, `FolderWatchService` | Main reviewer, in full | about 6,000 lines |
| Tool windows: the 12 tool view models and their windows | Helper 1, read-only | about 9,000 lines |
| The rest of Core, plus `OfficeConverter`, `ConverterChain`, `ImageToPdf` | Helper 2, read-only | about 9,000 lines |
| Settings, `Config`, the shared UI library, Box Labels, the Wpf services | Helper 3, read-only | about 12,000 lines |

Each helper finding below says how far it was checked. "Read" means the main reviewer opened the cited lines and they say what the row says. "Measured" means it was timed. "Helper only" means it rests on the helper's reading and should be confirmed before work starts on it.

## Architecture

### Projects

| Project | Targets | Job | Size (.cs lines) |
|---|---|---|---|
| `src/OrdoSort.Core` | net8.0 | Everything with no window: config, naming, moving, history, and each tool's engine | 9,901 |
| `src/OrdoSort.Ui` | net8.0-windows | Shared look and parts: theme, styles, MVVM base, message window, the label maker | 3,254 |
| `src/OrdoSort.Wpf` | net8.0-windows | OrdoSort.exe: dashboard, filing loop, Settings, tool windows, PDF and video panes | 21,331 |
| `src/BoxLabels.App` | net8.0-windows | BoxLabels.exe: the label maker by itself | 1,023 |
| `tools/OrdoSort.Smoke` | net8.0-windows | End-to-end scenarios, screenshots, brand art | 33 files |
| `tests/*` | | xUnit: Core (73 files), Wpf (182 files) | 61,121 |

Core never references WPF. The two apps share `OrdoSort.Ui` and `OrdoSort.Core`. Packages: PdfSharp, Microsoft.Data.Sqlite, SharpZipLib, MetadataExtractor (Core); WebView2, LibVLCSharp, ProtectedData (Wpf).

### The filing loop, start to finish

| Step | What happens | Where | Thread |
|---|---|---|---|
| 1. Start-up | `config.json` is loaded; the dashboard window builds the shell with its watcher, sounds, dialogs and document staging; the shell backs up and opens `history.sqlite` | `App.xaml.cs:72-76`, `MainWindow.xaml.cs:52-70`, `ShellViewModel.cs:171-198` | UI |
| 2. Watching | The inbox and set-aside folders are watched (file events with a 1.5 s pause, plus a poll every `poll_seconds`) | `FolderWatchService.cs:40-52`, `ShellViewModel.cs:611-620` | pool, then UI |
| 3. Snapshot | Inbox scan, set-aside summary and the monitored-folder sweep are gathered together, then shown: counts, tiles, alerts, notices | `ShellViewModel.cs:649-733`, `Scanner.cs`, `FolderMonitor.cs:49-108` | pool, then UI |
| 4. Start | Scan again, check every destination, build the route buttons, open the session window, read the first page's size | `ShellViewModel.cs:1512-1567` | pool, then UI |
| 5. Show | The document on screen and the next two are copied to this PC; Edge (PDF, photo) or VLC (video) shows the copy; a photo's date is read ahead | `ShellViewModel.Filing.cs:327-437`, `DocumentStage.cs`, `ShellViewModel.Media.cs` | pool, then UI |
| 6. Press | A route key takes the typed name, shows the next document at once and queues the move (at most 3 waiting) | `ShellViewModel.Filing.cs:152-252` | UI |
| 7. Move | One at a time: the viewer lets go, the copy is checked against the inbox file, the name is built, the file is moved (never overwritten), the history row is written | `ShellViewModel.Filing.cs:458-587`, `Session.cs:138-166`, `Commit.cs:120-169`, `History.cs:165-193` | pool |
| 8. After | The filed bar shows; the set-aside count and name suggestions refresh | `ShellViewModel.Filing.cs:671-703` | pool, then UI |
| 9. Undo, Stop, Done | Undo waits for moves to land, then moves the last file back and marks its row reverted; Stop lets presses land first | `ShellViewModel.Filing.cs:716-795`, `Session.cs:198-239` | pool, then UI |

### Tools

| Tool | View model | Core engine |
|---|---|---|
| Unlock PDFs | `UnlockViewModel` | `Unlock`, `PdfPasswords`, `Passwords` |
| Bulk rename | `BulkRenameViewModel` | `BulkRename` |
| Standardise names | `StandardiseNamesViewModel` | `Standardise`, `BulkRename.Execute` |
| Match and merge, Review matches | `MatchMergeViewModel`, `TriageWindow` (no view model) | `MatchMerge` |
| Merge PDFs | `MergePdfsViewModel` on `ZipListViewModel` | `PdfMerge`, the converters |
| Zip, Extract | `ZipListViewModel`, `ZipExtractViewModel` | `Zipper` |
| File list | `FilenameListViewModel` | `FilenameList`, `PageCounts` |
| Page counts | `PageCountsViewModel` | `PageCounts` |
| List reformatter | `ListReformatViewModel` | `ListReformat` |
| History | `HistoryViewModel` | `History` |
| Box labels | `LabelMakerViewModel` (in `OrdoSort.Ui`) | `BoxLabels`, `BoxLabelStore` |

### What is sound and should stay

| Decision | Where | Why it is worth keeping |
|---|---|---|
| One answer to "are these two paths the same file" | `PathIdentity` | Three callers once had three answers |
| Files are moved, never deleted or overwritten | `Commit` | Every failure leaves the document in one of two known places |
| Temp file beside the target, GUID name, then one rename | `AtomicPlace` | Safe for several PCs on one share |
| Disk and network work goes through `IWorkScheduler`; timers take a `TimeProvider` | everywhere | The UI thread stays free and tests need no real time |
| Core result types never throw across the boundary; they carry a status and a message | every engine | One error contract |

## Findings

`Checked` uses: Reproduced (a test failed on it), Measured, Read, Helper only. `Status` uses the usual four values.

### Can lose data or a record

| ID | Area | What is wrong | Evidence | Checked | Status |
|---|---|---|---|---|---|
| AR-01 | Standardise names | Two ways to lose the undo of files still under their new names. Undo forgot every file it could not put back (old name taken again, file in use), so that file could never be retried. And a Rename that moved nothing (its files in use) replaced the record of the rename before it with an empty one (Q2-01: fixed in Bulk rename, missed here). The first write-up also blamed a kill mid-batch; that was wrong as a defect: the record lives in memory only, in every tool, so a kill loses it however it is recorded | `StandardiseNamesViewModel.cs:567`, `:604` (before the fix) | Reproduced (3 tests failed, then passed) | ✅ Done |
| AR-02 | Merge PDFs | A merge saved under the default name was written straight to the final name, so a kill mid-save left half a PDF that looked finished. Zip and Unlock write to a private name first (Q2-32); Merge was missed. Now saved as `<name>.<guid>.partial` and renamed once whole; a name taken meanwhile is still left alone and reported, as before | `PdfMerge.SaveNew` | Reproduced (a test saw the file under its final name mid-save) | ✅ Done |
| AR-03 | Box Labels settings | A settings file that could not be read for a moment was treated as blank, and the next save wrote that blank over it; the write was in place, so a crash mid-write damaged it too. The remembered labels file was lost and the first-run picker came back. Now a save that can't read the file first fails and changes nothing, and the file is swapped in whole through `AtomicPlace.TryReplace` (made public for this). Still open: a file that can't be read at the moment the app starts still shows the first-run picker | `LabelsFileSettings.cs` | Reproduced (3 tests failed, then passed) | ✅ Done |
| AR-04 | History backup | A killed backup leaves a full copy of the database named `…sqlite.<guid>.partial` on the share for good; nothing sweeps it | `HistoryBackup.cs:80-123` | Helper only | ⬜ Not started |

### Structure

| ID | Area | What is wrong | Evidence | Checked | Status |
|---|---|---|---|---|---|
| AR-05 | Shell | `ShellViewModel` is 3,524 lines across four files and does about twelve jobs: dashboard counts, tiles, alerts, toast, notices, status line, route buttons, the name box and suggestions, the filing loop, Settings apply with the two-station conflict rules, config saving, history export. A change to any one of them reads past the other eleven | `ShellViewModel.cs`, `.Filing.cs`, `.Media.cs`, `.Video.cs` | Read | ⬜ Not started |
| AR-06 | Filing loop | What the loop is doing is spread over at least 13 separate flags and counters (`_busy`, `_pressing`, `_recovering`, `_stopWhenLanded`, `_workerRunning`, `_leavingPresses`, `_refreshBusy`, `_refreshPending`, `_sideRefreshRunning`, `_sideRefreshAgain`, `_tilesDue`, `_stageUnavailable`, `HistorySwapping`). Each guard reads a different mix of them, so "can this happen now" has no single answer | `ShellViewModel.Filing.cs:74-119`, `:141-148`, `ShellViewModel.cs:644-645`, `:1400-1405` | Read | ⬜ Not started |
| AR-07 | Session | `Session` is read on the UI thread and changed on a pool thread with no lock. It is safe today only because the shell's flags keep the two apart; `Session` itself promises nothing | `Session.cs:60-85`, `:101-109` | Read | ⬜ Not started |
| AR-08 | Settings | `SettingsViewModel` is 2,755 lines, its constructor takes 17 values, and a plain on/off setting needs 6 edits in 4 files (up to 9 with a check and a live note) | `SettingsViewModel.cs:671-685`, `:2646-2669` | Helper only | ⬜ Not started |
| AR-09 | Review matches | `TriageWindow` has no view model: the decide, skip and close rules live in 787 lines of window code, so every test needs a real window. Its rename also runs on the UI thread | `TriageWindow.xaml.cs:663-669`, `:705-743` | Read (`:669`) | ⬜ Not started |
| AR-10 | Tool windows | Three ways to tell "Clear stopped this" from "Cancel was pressed", two rules for whether Clear works mid-run, five rules for closing mid-run (Match and merge has none: STATUS "Match and merge" minor) | `UnlockViewModel.cs:232`, `ZipListViewModel.cs:299-323`, `PageCountsViewModel.cs:95-102` | Helper only (overlaps UX-75) | ⬜ Not started |

### Duplicate logic that has drifted

| ID | Rule | Copies | How they differ | Checked | Status |
|---|---|---|---|---|---|
| AR-11 | Rename a batch and keep an undo record | Bulk rename, Match and merge, Standardise names, Review matches | Only Bulk rename can be cancelled and records as it goes. Match and merge works out what came back with two `File.Exists` per file on the UI thread. Standardise runs its batch as one call with no progress or cancel (its undo record was AR-01) | Read (`StandardiseNamesViewModel.cs:528-617`, `MatchMergeViewModel.cs:606-636`) | ⬜ Not started |
| AR-12 | Add dropped paths to a list | 7 (Unlock, Zip, Bulk rename, Page counts, Match and merge twice, Standardise) | Standardise has no "the list was cleared meanwhile" check, so Clear during an add brings the rows back (the Q2-05 fix, missed here). Match and merge's file picker checks each file on the UI thread. Three of them drop an error silently | Read (`StandardiseNamesViewModel.cs:312-374`); rest Helper only | ⬜ Not started |
| AR-13 | The " (2)" counter for a taken name | `Collision.FreeFile`, `Collision.TakenIn`, `BulkRename.Plan` and `Execute`, `Naming.BuildTarget` | Only the filing loop treats a folder on the name as taken (DW-18) and lists the folder once (DW-17). Zip, Merge and Unlock still call `FreeFile`, which asks about files only | Read (`Collision.cs:18-29`, `:55-79`) | ⬜ Not started |
| AR-14 | A date at the front of a file name | `BulkRename`, `Standardise`, `Naming`, `MatchMerge` | Match and merge accepts only `-` after the date and never checks it is a real date. Standardise can write `20260930_SMITH_JOHN`, which Match and merge then can't read exactly | Read (the two patterns); effect not run | ⬜ Not started |
| AR-15 | A typed list of file types | `FolderMonitor.ParseFiletypes`, `MediaFiles.ParseExtensions` | The first accepts `*.pdf` (FL-02). The second turns `*.heic` into `.*.heic`, which matches nothing and says nothing | Read (`MediaFiles.cs:89-108`) | ⬜ Not started |
| AR-16 | Is this PDF encrypted | `PdfPasswords.Open`, `Unlock.ProbeReadiness`, `Unlock.UnlockBuffered`, `Unlock.UnlockStreaming` | Unlock skips the "does it even contain `/Encrypt`" gate, so a damaged PDF may read as "needs a password" | Helper only | ⬜ Not started |
| AR-17 | Checks on config values | `Config.Load`, `SettingsViewModel.HardErrors` | Different, overlapping sets. Route naming mode, suffix, hotkey and font are checked only in Settings, so a hand-edited bad value loads and fails later. (The helper's other example, a `history_db` that is a folder, is caught when Settings tries to open it: `ShellViewModel.cs:1996-2008`) | Read (`Config.cs:370-378`, no `history` check in `HardErrors`) | ⬜ Not started |
| AR-18 | Small JSON files | `TableLayoutStore`, `FilenameListOptionsStore`, `LabelsFileSettings` | Three read rules. Two write through a fixed `.tmp` name, the bug `AtomicPlace` was written to end; `LabelsFileSettings` now saves through `AtomicPlace.TryReplace` (AR-03); the other two still use their own `.tmp` | Read (`LabelsFileSettings`); rest Helper only | 🔄 In progress |
| AR-19 | Crash log | `OrdoSort.Wpf/App.xaml.cs`, `BoxLabels.App/App.xaml.cs` | Box Labels' copy has no rotation, no retry and writes beside the exe | Helper only | ⬜ Not started |
| AR-20 | Small helpers | `RemoveQuietly` (3 in Core), `IsInUse` (2), the font resolver (2), drag-and-drop handlers (7 windows), `new OpenFileDialog` (7 windows), clipboard copy (4) | Only File list's drop handlers have the FL-19 fix. Merge says "another program" where Unlock names it | Read (`RemoveQuietly`, `IsInUse`); rest Helper only | ⬜ Not started |
| AR-21 | Lists copied by hand | Naming modes, media folder choices, theme names, reserved hotkeys | All agree today; only Sort has a test that would catch drift. The hotkey capture box may drop the Windows key | Helper only | ⬜ Not started |

### Speed and scale

| ID | Area | What is slow | Evidence | Checked | Status |
|---|---|---|---|---|---|
| AR-22 | Inbox scan, set-aside summary | Every scan listed the folder and then asked the disk about each file again for its size or date: one round trip per file on a share, after every filed document and every poll. 2,000 files: 137 ms on the local disk, 1,170 ms through a loopback share. Read from the listing: 1.2 ms and 7.9 ms | `Scanner.cs` | Measured | ✅ Done |
| AR-23 | Merge PDFs, Zip, Extract: adding files | The list was refreshed once per added row, and a refresh walks every row. A 3,000-file drop: 2,002 ms before, 456 ms after (view model only, no window) | `ZipListViewModel.cs` | Measured | ✅ Done |
| AR-24 | Merge PDFs, Zip: readiness checks | Each check result still refreshes the whole list, about 0.9 s in total for 3,000 PDFs, spread over the checks rather than one freeze | `ZipListViewModel.cs:568-596`, `MergePdfsViewModel.cs:402-409` | Measured | ⬜ Not started |
| AR-25 | Page counts, File list | `PageCounts.Count` and `PageShape.SizeOf` open the PDF in PdfSharp's import mode, which reads the whole file. Counting 500 scans on a share pulls every byte across. A fix needs a reader that only looks at the end of the file: new code, not a tidy-up | `PageCounts.cs:19-20`, `PageShape.cs:30-34` | Helper only (same pattern as the 2026-09-29 preview fix) | ⬜ Not started |
| AR-26 | Unlock | The readiness check reads the whole file with no size limit, though the unlock itself switches to streaming at 32 MB. A 300 MB locked PDF crosses the network three times | `Unlock.cs:201`, `:62`, `:345-368` | Helper only | ⬜ Not started |
| AR-27 | Bulk rename, Standardise names previews | One `File.Exists` per file on every preview, so 3,000 files on a share is 3,000 round trips per typing pause. `Collision.TakenIn` already answers this from one folder listing | `BulkRename.cs:384-386`, `:421-426` | Read | ⬜ Not started |
| AR-28 | File list | Each page count that lands scans the whole list for its row; each Find keystroke clears and refills the list with no pause | `FilenameListViewModel.cs:613-614`, `:726-733` | Read | ⬜ Not started |
| AR-29 | Match and merge | Every unmatched file re-splits every roster name (three times each). Not timed | `MatchMerge.cs:297-301` | Helper only (overlaps DW-54) | ⬜ Not started |
| AR-30 | Filing loop, after each document | `names.txt` is read again and the whole ranked-names query runs again after every filed document. Small today (17 ms at 100,000 history rows, measured 2026-08-04) but it is on the share | `ShellViewModel.cs:2188-2210`, `Completer.cs:9-20` | Read | ⬜ Not started |
| AR-31 | History export | The whole table is read into memory as one dictionary per row before the CSV is written. The table is never pruned | `History.cs:306-345` | Read | ⬜ Not started |
| AR-32 | Merge PDFs with images | Every image is re-saved as PNG before going into the PDF, so a zip of phone photos grows several times over. Not measured | `ImageToPdf.cs:65-92` | Helper only | ⬜ Not started |

### Upkeep

| ID | Area | What is wrong | Evidence | Checked | Status |
|---|---|---|---|---|---|
| AR-33 | Comments | 12,060 of 35,509 source lines (34%) are comment lines, and most tell the story of a past review ("fix round 2, Gap B") rather than why the code is the way it is. `SaveSavedPasswordsNow` has 73 lines of comment over 45 of code (`ShellViewModel.cs:1747-1864`). The repo's own rule is "explain the why"; history belongs in the commit message | counted with `grep` over `src` | Measured | ⬜ Not started |
| AR-34 | Comments | 16 comments point at a `file:line` that has since moved. Three in `Commit.cs` point at the wrong lines today (`:202`, `:67`, `:123`) | `Commit.cs:68`, `:213-214`; list from `grep` | Read | ⬜ Not started |
| AR-35 | Comments that are now wrong | "four section files" (there is one); "eight per-field note probes" (five); `OfficeConverter` says its encryption check is not applied to `.xls`, and the code applies it; File list says Page counts colours failures differently (it doesn't) | `SettingsViewModel.cs:627`, `:1600`; `OfficeConverter.cs:403`, `:549-555`; `FilenameListViewModel.cs:463-467` | Read (`OfficeConverter`); rest Helper only | ⬜ Not started |
| AR-36 | Dead code kept for a test | `BulkRename.TidyStem` and `DeleteSegmentsFromStem` are no longer used by the app; `TidyStem` is kept as a test's reference | `BulkRename.cs:136`, `:281` | Helper only | ⬜ Not started |
| AR-37 | Styles | The ToggleButton style is a full copy of Button, and PasswordBox of TextBox | `Styles.xaml:52-142`, `:163-242` | Helper only | ⬜ Not started |
| AR-38 | Settings | Four buttons still touch the disk on the UI thread (Open folder, Open backups, Create folder, the side-file picker's start folder) | `SettingsViewModel.cs:1402`, `:1416`, `:1433`; `DialogService.cs:113` | Helper only | ⬜ Not started |

## Refactoring plan

Smallest and safest first. Every step is its own change with a failing test before the fix, and nothing here needs a new package.

- [x] 0. Speed fixes with numbers behind them: AR-22, AR-23 (this change)
- [x] 1. Close the gaps that can lose something (done 2026-09-30): AR-01 (Undo keeps what it could not put back; a rename that moves nothing keeps the last record), AR-02 (the merge is written to a private name, then renamed), AR-03 (a settings file that could not be read is never saved over; the save swaps in a whole file). AR-04, the stale history-backup partials, is still open and still unconfirmed
- [ ] 2. One helper per copied rule, then point every copy at it. In order of payoff: a `RenameBatch` helper lifted from Bulk rename (AR-11, also closes the "Match and merge can close mid-merge" minor); a shared add-paths helper (AR-12); `Collision.Free(target, taken)` (AR-13, also fixes AR-27); a small public Core helper for little JSON files built on `AtomicPlace` (AR-18, also AR-03); one `CrashLog` in `OrdoSort.Ui` (AR-19); one `StemDate` reader (AR-14)
- [ ] 3. Settings: move `HardErrors` and the folder checks into a plain `SettingsChecks` class, then give `Config` one `Problems()` list that both `Load` and Settings call (AR-08, AR-17)
- [ ] 4. Split the shell, one piece per change, each a move with no behaviour change, guarded by the existing shell tests: (a) config saving and the two-station rules, (b) the name box and suggestions, (c) the dashboard (tiles, alerts, toast, notices), (d) the filing loop with one named state in place of the flags (AR-05, AR-06, AR-07)
- [ ] 5. Give Review matches a view model (AR-09) and one shared busy/clear/close rule for the tool windows (AR-10)
- [ ] 6. Remaining speed rows, each measured first: AR-24, AR-26, AR-27, AR-28, AR-29
- [ ] 7. Comments: when a file is next touched, cut history down to the reason; replace `file:line` pointers with names (AR-33, AR-34, AR-35)

Why this order. Steps 1 and 2 remove ways to lose something and stop a fix landing in one copy only; that is where the past bug rows came from (QC-17, D4, Q2-05, Q2-32, FL-02 and FL-19 were each fixed in one copy and missed in another). Step 4 is the largest and the riskiest: the filing loop is the product and it was rebuilt on 2026-09-29. Splitting it buys easier changes later, not a visible gain now, so it goes after the cheap wins and one piece at a time.

What is not proposed. Merging atomic placement with the created-by-me gate (CONTEXT.md says why not). A stream copy of our own for cross-drive moves (STATUS "Dead ends": 19 times slower). Turning status strings into enums (DW-53, declined). A dependency-injection container: every view model already takes its collaborators through the constructor, with a default; a container would add a package and change nothing a user or a test can see.

## Changed in this pass

| Change | Files | Measured | Tests |
|---|---|---|---|
| The inbox scan and set-aside summary read each file's size and time from the folder listing. Sorting became a function with no disk access | `src/OrdoSort.Core/Scanner.cs` | 2,000 files: 137 → 1.2 ms local, 1,170 → 7.9 ms loopback share | `ScannerListingTests` (11 new); the QC-13 sentinel test moved there from `PipelineTests` |
| A drop into Merge PDFs, Zip or Extract refreshes the list once, not once per file | `src/OrdoSort.Wpf/ViewModels/ZipListViewModel.cs` | 3,000 files: 2,002 → 456 ms | `MergePdfsViewModelTests.ADropRefreshesTheListOnceNotOncePerFile` (fails on the old code: 3 refreshes for 3 files) |

| Standardise names: Undo keeps the files it could not put back, and a Rename that moves nothing no longer wipes the last undo record (AR-01) | `src/OrdoSort.Wpf/ViewModels/StandardiseNamesViewModel.cs` | Not a speed change | `StandardiseNamesViewModelTests`: 3 new, each failed before the fix |
| Merge PDFs: a default-name merge is saved under a private `.partial` name and renamed once whole (AR-02) | `src/OrdoSort.Core/PdfMerge.cs` | Not a speed change | `PdfMergeTests`: 4 new; the first failed before the fix |
| Box Labels: a settings file that can't be read is never saved over, and the save swaps in a whole file (AR-03) | `src/BoxLabels.App/Services/LabelsFileSettings.cs`, `src/OrdoSort.Core/AtomicPlace.cs` (`TryReplace` made public) | Not a speed change | `BoxLabelsAppSettingsTests`: 3 new, each failed before the fix |

One thing the scan change can alter: a folder listing can lag behind a file that is still being written, so such a file may sort by an older size or time until its writer closes it. A finished file sorts exactly as before. The paths handed to the session are spelled as before (pinned by a test).

Two things the AR-02 and AR-03 fixes can alter. A merge killed mid-save now leaves a `.partial` file beside the sources that nothing cleans up, the same as a killed default-name zip (STATUS "Core: zip"); it no longer looks like a finished PDF. And a Box Labels save into a folder it can't write to now takes up to about 3 seconds to say so, because `AtomicPlace` retries before giving up; it used to fail at once.

## Not covered

| What | Why |
|---|---|
| A timing on a real network share | Only a loopback share on this PC was measured; a real link adds delay to each round trip, so the gain there should be larger, but it was not measured |
| `WebViewPdfViewer`, `VlcVideoPlayer`, `ThemeManager`, the body of `LabelMakerViewModel` | Searched, not read in full |
| Rows marked "Helper only" | One reader, not confirmed |
| The XAML, beyond `Styles.xaml` | The 2026-09-30 UX audit covers the screens |
