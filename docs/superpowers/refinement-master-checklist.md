# Refinement master checklist

**Consolidated 2026-08-22** from every audit, plan, spec, and memory-recorded deferral in
this repo. This file is the single tracker of everything still open app-wide. It is a
**tracker, not an authority** — every item cites its source, and if this file and a source
ever disagree, **the source wins**. Nothing was fixed in the pass that produced this file.

**Snapshot: 173 open items — 4 High · 81 Important · 88 Minor.**
Batch A (2026-08-22, branch `fix/app-qc-2026-08-21`) closed all nine of the first audit's
High findings; the fresh QC of 2026-08-22 (`audits/2026-08-22-fresh-qc.md`, IDs `Q2-nn`)
then added 46 findings including 4 new Highs — three of them in or beside batch A's own
fixes. Doc-source arithmetic: 242 findings recorded across eleven source documents = 89
closed + 150 open + 3 declined; the 150 collapse to 145 unique rows after cross-source
dedupe, plus 28 memory-only items = 173.

## How to read this file

- **IDs.** Items keep their source's stable ID where one exists (`QC-nn`, `FL-nn`,
  `D2–D4`, `R2–R4`). Items that had no ID were minted a `DW-nn` (deferred work) here —
  DW IDs are handles for tracking, not new authority; the citation is the authority.
- **Severity** is the house ladder: **High** — lies to the user, or loses their work ·
  **Important** — wrong behaviour the user hits and is confused by, nothing destroyed ·
  **Minor** — real but small. Sources using Medium/Low map uniformly Medium→Important,
  Low→Minor; each row keeps its source's original word implicitly via the citation.
  Where a re-grade is arguable it is noted on the row, never silently applied.
- **Marks** are preserved from the sources: **[V]** traced to code with a citation ·
  **[U]** a hypothesis needing a run/second process/real share/printer · **[live]** the
  FL audit's needs-a-live-check mark.
- **Tags:** `(user)` — blocked on a user action or decision · `(deferred)` — deliberately
  left by a recorded decision, still open · `(verify)` — recorded state may be stale;
  re-verify before acting · `(2 sources)` — the same defect recorded in two documents,
  both cited.
- A checked box means closed **with evidence** (test, commit, or re-verified source);
  see the update procedure at the end of this file.

---

## High — 4 open

The first audit's nine Highs (QC-01…QC-09) were closed by batch A on
`fix/app-qc-2026-08-21` (audit status block, 2026-08-22). The fresh QC then found four
new ones — every chain hand-verified against source.

- [ ] **Q2-01** [V] · Bulk Rename — after (or during) a batch, the Rename button re-arms over the just-executed plans before the off-thread re-plan lands; a second click — or a cancel before the first file — replaces `_lastOutcomes` with an empty list and destroys the undo record of renames already on disk. Introduced by batch A's own responsiveness fix. *(fresh-qc §High; found independently by two sweeps)*
- [ ] **Q2-02** [V] · MatchMerge / Review — Merge and Undo run `BulkRename.Execute`/`Revert` synchronously on the UI thread and assign `_outcomes` only after the loop: a kill of the frozen window leaves files renamed with no undo path. QC-04, never propagated to the sibling. *(fresh-qc §High)*
- [ ] **Q2-03** [V] · Core / filing spine + Settings — a set-aside folder (or route destination) that IS the inbox — or the same folder spelled two ways — makes Skip rename the document in place with " (2)", report "✓ Set aside", count the whole inbox as set-aside files, and re-queue the renamed file. No hop anywhere compares two configured folders; QC-08's cross-field check covers the four side-file keys only. *(fresh-qc §High)*
- [ ] **Q2-04** [V]/[U] · Core / filing spine — the QC-03 post-move guard, thrown from `UndoAction`, skips all of `Session.UndoLast`'s state restoration: document back in the inbox AND at the destination while counters and history say "filed", and a second Undo is permanently refused. Introduced by batch A; same Win32/SMB trigger as QC-03. *(fresh-qc §High)*

---

## Important — 81 open

### App-wide QC, 2026-08-21 (`audits/2026-08-21-app-qc.md`) — 16

- [ ] **QC-10** [V] · Bulk Rename — Uppercase/Lowercase is a silent no-op: `SameFile` is case-insensitive `PathIdentity.Same`, so case-only renames are classified `Changed: false` and skipped with "(no change)". Caveat: NTFS re-case via `File.Move` not yet confirmed. *(app-qc §Important; settled open — omitted from the batch-A closed list and covered by "everything else is untouched and still open")*
- [ ] **QC-15** [V]/[U] · Label Maker — Printing with Copies > 1 puts the same barcode on multiple boxes; the claim reserved only `b.Count` numbers, and Cancel/Esc's "counter untouched" claim is false because `Print()` claims before the preview opens. *(app-qc §Important)*
- [ ] **QC-17** [V] · Settings — OK never validates a route's filename suffix; a `:` in a suffix passes OK then fails every commit to that route, and Settings is unreachable from Processing. *(app-qc §Important)*
- [ ] **QC-18** [V]/[U] · Settings — OK runs every folder check and write-probe (`ValidateRoute` creates and deletes a real file) inline on the UI thread; dead shares mark the window Not Responding. *(app-qc §Important)*
- [ ] **QC-19** [V]/[U] · Shell / main window — the 10-second force-close can dispose History under a still-running commit and discard the resulting `AuditError` in silence: document filed, no history row, no warning. *(app-qc §Important)*
- [ ] **QC-20** [V] · Folder watch — `FileSystemWatcher.Error` is never handled and a dead watcher is never re-armed; at `poll_seconds = 600` that is ten minutes of blindness with no indication. *(app-qc §Important)*
- [ ] **QC-21** [V] · History / crash log — patient document paths reach `crash.log`, which sits beside the shared config on the share; one history-DB hiccup appends a patient name to a plaintext file multiple stations can read. *(app-qc §Important)*
- [ ] **QC-22** [V]/[U] · WebView2 preview — WebView2 keeps a history DB of every previewed document (`file:///` navigations; profile confirmed 70 MB on this machine) and nothing ever clears it. *(app-qc §Important)*
- [ ] **QC-23** [V] · Settings — `HotkeyParser` catches `NotSupportedException` but `KeyGesture` throws `InvalidEnumArgumentException` (`"Ctrl+300"` parses), making session start fail *and* crashing Settings on open — the one screen where it could be fixed. *(app-qc §Important)* — [U] settled empirically 2026-08-22: exception type confirmed (fresh-qc §Experiments); only the fix remains.
- [ ] **QC-24** [V] · WebView2 preview — no `ProcessFailed` handler and `_ready` is never cleared; if the Edge process dies, every subsequent filing keystroke throws outside both catch paths until restart. *(app-qc §Important)*
- [ ] **QC-25** [V] · Filename List — `Dispose()` disposes `_countGate` while in-flight counters still release it; silent today, a crash-on-close the day unobserved-task handling changes. *(app-qc §Important)*
- [ ] **QC-27** [U] · Folder watch — `FolderWatchService` shares an unguarded `List` across threads; a `SetFolders` on a pool thread racing `Dispose()` can throw out of the `Closed` handler so `Shell.Dispose()` never runs and History is never closed. *(app-qc §Important)*
- [ ] **QC-28** [V]/[U] · Label Maker — renaming onto an id a peer just created merges into their row: the carried-counter rescue only applies when `fresh is null`, so the peer's retention is overwritten and a counter is dropped. *(app-qc §Important)*
- [ ] **QC-29** [V]/[U] · Label Maker — a lowercase id on disk duplicates on first save (every disk lookup is ordinal `==` against uppercased VM ids), then `Persist` refuses every save and `Problems()` blocks Print and Save PDF; the legacy migration copies ids verbatim. *(app-qc §Important)*
- [ ] **QC-30** [V] · Settings — re-pointing a side-file path silently discards the same session's edits: `AdoptRepointedSection` replaces routes/watch-folders/alerts wholesale on the same OK that carried the user's changes. The admin-wins rule is deliberate; the silence is the defect. *(app-qc §Important)*
- [ ] **QC-31** [V] · Zip Tools — `ZipExtractViewModel.ZipAsync` ignores cancellation entirely (touches neither `_cts` nor `IsBusy` nor `RunBatchAsync`); closing the window mid-zip leaves the operation running. Fourth instance of the batch-mutated-under-a-live-list class. *(app-qc §Important, added during batch A)*

### Filename List living list, 2026-08-20 (`audits/2026-08-20-filename-list-ui-audit.md`) — 14

- [ ] **FL-07** · Filename List — nothing the tool learns survives closing it (no persisted columns/subfolders/filters). Would be closed by the deferred manifest spec §4. *(FL audit §Medium)* `(deferred — spec written, not started)`
- [ ] **FL-08** [live] · Filename List — no sign a big walk is happening: no spinner, no "Working…", no cancel on a large recursive folder. *(FL audit §Medium; named open in the fix-pass status block)*
- [ ] **FL-09** · Filename List — toggling *Include extension* re-walks the disk for a cosmetic change. Manifest spec §2 would make `stem` a column, not a rebuild trigger. *(FL audit §Medium)* `(deferred)`
- [ ] **FL-10** · Filename List — default window size is too narrow for its own columns; the 2026-08-21 width bump (640→760) absorbed only the Pages column — the all-columns-on and MinWidth-480 cases are unchanged. *(FL audit §Medium; "PARTIALLY MITIGATED … still open")* `(deferred — full remedy is manifest spec §5)`
- [ ] **FL-11** · Filename List — column headers look sortable and aren't (`CanUserSortColumns="False"` with default header chrome). Manifest spec §7 would add real sorting. *(FL audit §Medium)* `(deferred)`
- [ ] **FL-12** · Filename List — *Z to A* is a checkbox doing a two-state control's job; removed outright once header sorting exists. *(FL audit §Medium)* `(deferred)`
- [ ] **FL-13** · Filename List — *Remove from list* is right-click-and-Delete only; no toolbar button. *(FL audit §Medium; named open in the fix-pass status block)*
- [ ] **FL-14** · Filename List — buttons never disable, so Copy/Restore/Clear/Save on empty states are silent no-ops. *(FL audit §Medium)*
- [ ] **FL-15** · Filename List — *Restore removed* doesn't say how many rows it will restore. *(FL audit §Medium)*
- [ ] **FL-16** · Filename List — Clear wipes everything with no confirmation and no undo. *(FL audit §Medium)*
- [ ] **FL-17** · Filename List — stale `AddNote` and `Status` outlive what produced them (survive Clear and new folder adds). *(FL audit §Medium)*
- [ ] **FL-18** [live] · Filename List — the footer grows and shoves the grid when a save fails; the 2026-08-21 trim removed the wrap but the full text is still unrecoverable (no tooltip). *(FL audit §Medium; "PARTIALLY FIXED … stays open")*
- [ ] **FL-19** [live] · Filename List — dropping onto either text box (Find / Only these types) probably loses the drop. *(FL audit §Medium)*
- [ ] **FL-20** · Filename List — nothing indicates the window is a drop target while you drag (whole-family finding). *(FL audit §Medium)*

### Dropdowns & file connections QC, 2026-08-07 (`audits/2026-08-07-qc-dropdowns-and-file-connections.md`) — 10

- [ ] **D2** [V] · Config — `SortChoices` (Wpf) / `Config.Sorts` (Core) list drift throws on `Config.Load` and blocks startup with no in-app recovery. Still throws unconditionally at `Config.cs:268-269`; cited as live by the batch-A plan's Task 5 ruling. *(08-07 §Important)*
- [ ] **D3** [V] · Settings / Bulk Rename / MatchMerge — 6 of 11 combos have no accessible name (Process order, App font, Letter case, all three MatchMerge headers); confirmed absent in current XAML. *(08-07 §Important)*
- [ ] **D4** [V] · Settings — no validation on `UiFontFamily`, route `NamingMode`, or `Section`: a stale value renders the combo blank while the value survives; none appear in `HardErrors()`. *(08-07 §Important)*
- [ ] **R2** [V]/[U] · History / crash log — the daily history backup is a raw `File.Copy` of a live, concurrently written SQLite DB — no backup API, no integrity check; a torn backup reads as success. Source grades Important; a re-grade to High is arguable (silent corruption of the audit trail's only backup). *(08-07 §3 Important; `HistoryBackup.cs:31` unchanged)*
- [ ] **R3** [V] · Core / filing spine — `History.ExportCsv` and `BoxLabels.RenderPdf` truncate in place with no temp+rename, unlike `Config.WriteAtomic`; a crash mid-write destroys the previous file at that path. Source grades Important; a re-grade to High is arguable. *(08-07 §3 Important; `History.cs:305`, `BoxLabels.cs:275` unchanged)*
- [ ] **R4** [V]/[U] · History / crash log — `crash.log` grows unbounded with no rotation; append atomicity over SMB unverified. Distinct from QC-21 (PHI content) and DW-37 (interleaving). *(08-07 §3 Important; `App.xaml.cs:122` unchanged)*
- [ ] **DW-06** · Config — route and watch-folder paths still get the old raw resolution the C2 fix removed for Inbox/Deferred; commit `c3798f2` explicitly scoped itself to Inbox/Deferred only. *(08-07 §2 Important)*
- [ ] **DW-07** · Shell / main window — `ApplySettingsAsync` adopts a failed save into memory: `_cfg = cfg` runs before `TrySave`'s result is checked, so a failed save stays live for the session (confirmed at `ShellViewModel.cs:1874` vs `:1881`). *(08-07 §2 Important)*
- [ ] **DW-08** · Settings — only route destinations get a writability probe; Inbox, Deferred, side files, and `history_db` get existence-only checks. *(08-07 §2 Important)*
- [ ] **DW-09** · Settings — a station-local absolute path landing in the shared config is never flagged, and cross-station drive-letter/UNC coherence is unchecked. *(08-07 §2 Important)*

### Carried from the v1-era audits — 5

- [ ] **DW-01** [V] · Core / filing spine — the cross-volume move's crash branch: a kill mid-copy leaves a file holding the canonical name (08-04 §1.4). QC-03's batch-A fix covers only the copy-succeeded-delete-failed branch. *(08-04 §1.4 + 08-09 core §Important 3 + app-qc §What this method would miss)* `(2 sources)` — **PROVEN 2026-08-22** on local volumes, and worse than recorded: killed at 150ms the destination is FULL-LENGTH with incomplete data (CopyFile preallocates — undetectable by size); killed post-copy both complete copies remain (fresh-qc §Experiments). SMB untested. No longer merely deferred — the hazard is demonstrated.
- [ ] **DW-02** · Core / filing spine — `Commit.SkipFile` calls `MoveNeverOverwrite` with no `catch (FileExistsRace)`, unlike `CommitFile`/`UndoAction`; the private exception sails past `OnSkipAsync`'s catches to an unhandled UI-thread exception. No document is lost (guard fires pre-move). *(08-09 core §Important 1)*
- [ ] **DW-03** · Config — `ResolveConfined` checks containment via lexical `Path.GetFullPath` only, which does not resolve reparse points; a junction inside the config directory can redirect a confined side-file read/write outside it. *(08-09 security §Important 2)*
- [ ] **DW-04** · Repo / process — releases ship unsigned; first run trips SmartScreen. Azure Trusted Signing is wired in `release.yml` but gated on secrets that don't exist; needs a code-signing certificate the owner does not yet have. *(08-04 §3.2 + 08-09 tests-build §Important 2)* `(user, 2 sources)`
- [ ] **DW-05** · Tests / build — `LabelPreview.cs` (151 lines building the physical printed label sheet — box numbers, destroy-by dates) has zero references anywhere in `tests/`; wrong printed output is expensive to discover and impossible to recall. *(08-04 §Theme 6 + 08-09 tests-build §Important 3)* `(2 sources)`

### Recorded in the batch-A status block and memory — 5

- [ ] **DW-10** · Shell / main window — `ShellViewModel.OpenDeferredCommand` and the two `_watch.SetFolders` call sites share QC-02's blank-`Deferred` root; the Core refusal batch A added does not cover these hops. *(app-qc §Status, "deliberately left, recorded where it belongs")*
- [ ] **DW-11** · Unlock — `UnlockCommand` lacks the `!IsUnlocking` guard its siblings have; a double-fire starts overlapping runs over real files. *(app-qc §Status)*
- [ ] **DW-12** · MatchMerge / Review matches — plain digit/S keys over the WebView2 viewer pane are characters, not accelerators, and are unrecoverable without a `WH_KEYBOARD_LL` hook beside the existing mouse hook in `Services/ViewerInput.cs`. *(memory: ordosort-review-matches-fixes, "Open follow-up", 2026-08-18)*
- [ ] **DW-13** · Repo / process — `docs/sample/` still holds 412 files / 23 MB of real exports in the working tree (untracked, ignored, never committed — but a QC subagent read a row of one CSV during the 2026-08-21 pass, exactly what the `.gitignore` comment predicts). `S:\OrdoSort-samples` already exists; moving the folder closes it. *(app-qc §Working-tree note + memory: ordosort-app-qc-2026-08-21)* `(user, 2 sources)`
- [ ] **DW-14** · Repo / process — `docs/FileMover.py`, `docs/paper_mover_logger.py`, and `docs/RemoveReadOnly.ps1` default their logs to beside-the-script (inside the tree, un-ignored) and the Python two log full document paths. Whether they are still run decides whether this matters — needs the user's answer. *(app-qc §Working-tree note)* `(user)`

### Fresh QC, 2026-08-22 (`audits/2026-08-22-fresh-qc.md`) — 31

- [ ] **Q2-05** [V] · Zip Tools / Unlock / PageCounts / Bulk Rename — Clear cannot stop an in-flight add: the off-thread walk finishes and repopulates the list the user just emptied (Unlock then re-probes the rows with the fresh token Clear installed). Fifth-through-eighth instances of the DW-78 class. *(fresh-qc §Important)*
- [ ] **Q2-06** [V]/[U] · Unlock — "Manage saved…" stays live during a run and `RequeueAllFilesForProbing` re-probes the very files the batch is archiving/rewriting; the run's candidates snapshot also makes the newly added password useless mid-run. *(fresh-qc §Important)*
- [ ] **Q2-07** [V] · Zip Tools — action buttons gate only themselves; Extract and Zip can run concurrently over one list and the shared Status line keeps only the last writer's verdict. *(fresh-qc §Important)*
- [ ] **Q2-08** [V]/[U] · Shell / main window — Settings can be OK'd during Start's off-thread scan (`if (_busy) return;` — nothing sets `_busy`); `BuildRoutes` then zips the new config's routes against the old config's problem strings, and the session is seeded from a scan of the old inbox. *(fresh-qc §Important)*
- [ ] **Q2-09** [V] · Zip Tools / Unlock / PageCounts — three of four intake paths are fire-and-forget with no catch around `Intake.Expand`: a drop that fails mid-walk simply does nothing — no rows, no note, no dialog, no crash.log. *(fresh-qc §Important)*
- [ ] **Q2-10** [V] · PageCounts — Save/Copy mid-count exports a snapshot short by every still-counting row and reports "Saved to …"; pending rows emit a bare name+tab and drop out of the Total. High-arguable. *(fresh-qc §Important)*
- [ ] **Q2-11** [V] · Config / Settings — `TrySave`'s collision refusal returns empty `refusedSideFileKeys`, which takes `WarnSaveFailure`'s unconditional-modal branch: every unrelated background save (merge headers, tile toggle, Unlock-open password sweep) nags forever on a collision the user can't act on from that dialog. Introduced by the QC-08 closure. *(fresh-qc §Important)*
- [ ] **Q2-12** [V] · Bulk Rename — the add paths (buttons and drop) are the ungated third sibling of Clear/Remove: a mid-batch add dedupes against pre-rename sources, and the post-batch fixup then lists one file twice — the exact state the dedupe's own comment exists to prevent. Introduced beside batch A's gate. *(fresh-qc §Important)*
- [ ] **Q2-13** [V] · Zip Tools — `Zipper.CreateZip`'s folder walk still uses the aborting `SearchOption` overload: one denied subfolder loses the whole archive job with a raw access-denied. Third instance of QC-01's class; two of three call sites fixed. *(fresh-qc §Important)*
- [ ] **Q2-14** [V] · Tests / build — the star-column lint reads XAML only; TriageWindow's sole star column is built in C#, so its `FillerMinWidth` is unpinned — delete it and every suite stays green under a class doc claiming coverage. *(fresh-qc §Important)*
- [ ] **Q2-15** [V] · Tests / build — nothing measures LabelMakerWindow vertically: the registry delegates to a suite that probes `checkVertical: false` at no declared height; deleting that suite entirely fails nothing. *(fresh-qc §Important)*
- [ ] **Q2-16** [V] · Tests / build — the Ready dashboard is never measured in the ~350-422px band compact mode parks users at; the MainWindow registry entry renders at 400 but examines 11 chrome elements (the screens are Visibility-bound and never shown). *(fresh-qc §Important)*
- [ ] **Q2-17** [V] · Tests / build — batch A's per-window ⌈0.75×measured⌉ floor reached one of five `OverflowProbe` call sites; the other four keep flat floors at 28-75% of their measured populations — the partial blindness QC-09's own correction named. *(fresh-qc §Important)*
- [ ] **Q2-18** [V] · Tests / build — neither overflow suite ever renders largest-font-at-narrowest-width; the 2×2 corner the suites exist for is tested in neither. *(fresh-qc §Important)*
- [ ] **Q2-19** [V] · Tests / build — `TextWrapCoverageTests`' floors are 23%/56% of population, and fact 1's floor counts candidates before `HasWidthPin` can waive every one of them — a shared wrapper gaining `MaxWidth` zeroes the walk with `judged` unchanged. *(fresh-qc §Important)*
- [ ] **Q2-20** [V]/[U] · Settings — opening Settings runs 7-9 sequential file reads plus three SHA256 hashes on the UI thread before the window exists (`FreshConfigForSettings()` as a constructor argument). Distinct from QC-18's OK path. *(fresh-qc §Important)*
- [ ] **Q2-21** [V] · Label Maker — the constructor blocks up to a documented 5 seconds (`BoxLabelStore.DefaultMaxWaitMs`) on a contended store file before the window appears. *(fresh-qc §Important)*
- [ ] **Q2-22** [V]/[U] · Label Maker — PrintPreview's constructor enumerates print queues synchronously (`LocalPrintServer`, `GetPrintQueues`, `DefaultPrintQueue`); an offline network queue stalls before the window is visible. *(fresh-qc §Important)*
- [ ] **Q2-23** [V] · Bulk Rename — "Add folder" evaluates `Directory.GetFiles(dlg.FolderName)` on the UI thread as the argument to the async call; the one window in its family that still walks on the dispatcher. *(fresh-qc §Important)*
- [ ] **Q2-24** [V]/[U] · Shell / main window — the four open-folder commands (Inbox, Deferred, toast, backups) run `Directory.Exists` + `Process.Start` synchronously; a dead share stalls the main window on an everyday click. *(fresh-qc §Important)*
- [ ] **Q2-25** [V]/[U] · Settings — every Browse… button probes the current (possibly dead) path with `Directory.Exists` before the picker opens — the hang lands exactly when the user is trying to fix a bad path. *(fresh-qc §Important)*
- [ ] **Q2-26** [V] · Unlock — `UnlockAsync`'s synchronous prefix size-classifies every queued file (`FileInfo.Length` per row) on the UI thread before its first await; the same cost class the window's own comment fixed for adds. *(fresh-qc §Important)*
- [ ] **Q2-27** [V]/[U] · Shell / main window — `RefreshFoldersAsync` has no catch and all three call sites are bare `_ =`: one exception past Scanner/FolderMonitor's narrow filters and the dashboard silently never updates again. `RunGuarded` wraps its three siblings; this one was missed. *(fresh-qc §Important)*
- [ ] **Q2-28** [V]/[U] · History / crash log — History's load has no try at all and both exports catch only `IOException or UnauthorizedAccessException`; a `SqliteException` (busy/locked/corrupt multi-station DB) means an empty window forever or a silent no-op export. *(fresh-qc §Important)*
- [ ] **Q2-29** [V]/[U] · Label Maker — `SavePdf` discards its task and `SavePdfAsync`'s catch misses `FormatException` from `RebuildFromClaim`'s `int.Parse`; `Problems()` gates only the selected row, per its own comment. *(fresh-qc §Important)*
- [ ] **Q2-30** [V] · Unlock — `UnlockCommand`'s `OnError` is never subscribed, so a run that throws produces zero feedback; the finally's own comment admits "no OnError-style hook covers this path". *(fresh-qc §Important)*
- [ ] **Q2-31** [V]/[U] · MatchMerge / Review — TriageWindow holds the app's only unguarded `async void` handlers (Loaded init, `OnUseSelected`, `OnSkip`); a dead WebView2 mid-decision escapes to the global crash dialog instead of a local message. Census correction recorded: 7 sites, not 5 — a baseline undercount. *(fresh-qc §Important)*
- [ ] **Q2-32** [V]/[U] · Shell / main window — log-off/restart (`WM_QUERYENDSESSION` + `ShutdownMode="OnMainWindowClose"`) tears the process down with no grace for tool-window batches mid-write; only the main commit has `FinishClosingWhenIdle`. A half-written zip, or an unlocked PDF half-written with its original already archived. *(fresh-qc §Important)*
- [ ] **Q2-33** [V]/[U] · Shell / main window — the async `Loaded` continuation (WebView2 init → `Shell.Initialize()`) can resume after `Closed` has disposed Shell: startup work runs against disposed History/watchers, with warnings parented to a dead window. *(fresh-qc §Important)*
- [ ] **Q2-34** [V] · Shell / main window — a blank inbox flattens to the config directory (the blank-preserving wrapper was scoped to the two Deferred call sites, per its own comment): the Ready screen affirms "0 files ready" with the calm-inbox illustration, "Open inbox" opens the config folder, and the watcher watches it. QC-02's shape, on the field the product is about. *(fresh-qc §Important)*
- [ ] **Q2-35** [V]/[U] · Shell / main window — duplicate hotkeys in a hand-edited config pass `Load` unexamined; both route buttons wear the badge and WPF's last-added binding wins silently, with history faithfully recording the destination the user didn't choose. Re-grade to High arguable. *(fresh-qc §Important)*

---

## Minor — 88 open

### App-wide QC, 2026-08-21 — 17

- [ ] **DW-15** · Core / filing spine — `Commit.SkipFile` lets `ArgumentException` escape while `CommitFile` wraps it (`Commit.cs:89-91` vs `:59-61`). *(app-qc §Minor)*
- [ ] **DW-16** · Shell / main window — `AuditError.NewPath` carries the inbox path of a vanished file and the UI then says it "moved" (`Session.cs:203`; `ShellViewModel.cs:285-286`). *(app-qc §Minor)*
- [ ] **DW-17** · Core / filing spine — the collision counter is O(n) sequential `File.Exists` round-trips per commit (`Naming.cs:140-145`). *(app-qc §Minor)*
- [ ] **DW-18** · Core / filing spine — a directory occupying the target name is invisible to both collision guards (`File.Exists` is false for directories). *(app-qc §Minor)*
- [ ] **DW-19** [V] · Core / filing spine — `RejectIllegal` misses a trailing space, so `CON .pdf` is emitted (`Naming.cs:109`). *(app-qc §Minor)* — measured 2026-08-22: creates an ordinary file on Win11 NTFS, no device capture (fresh-qc §Experiments); residual risk is older Windows/other stations only.
- [ ] **DW-20** · MatchMerge / Review matches — `ClearCommand` doesn't clear `_outcomes`, so "Undo last merge" stays enabled and renames files the empty grid never showed (`MatchMergeViewModel.cs:84`). *(app-qc §Minor)*
- [ ] **DW-21** · Settings — watch-folder labels aren't duplicate-checked at OK, while route labels are. *(app-qc §Minor)*
- [ ] **DW-22** · Unlock — a hard kill leaves an unencrypted temp PDF in `%TEMP%` (every graceful path cleans up) and nothing sweeps stale `ordosort_unlock_*` at startup. *(app-qc §Minor + 08-07 §3 Minor)* `(2 sources)`
- [ ] **DW-23** · PageCounts — `PageCountsViewModel` never disposes its CTS or semaphore; `MainWindow` never disposes its WebView2 while `TriageWindow` does — the asymmetry behind the recorded WPF-test-host exit hang. *(app-qc §Minor)*
- [ ] **DW-24** · Shell / main window — `DialogRelay` silently inherits the single-select default for `AskOpenFiles` (latent, no current caller) (`IDialogService.cs:18-19`). *(app-qc §Minor)*
- [ ] **DW-25** [U] · Core / filing spine — `DebouncedProbe.Trigger`'s `_disposed` check sits outside the lock `Dispose` takes; unreachable today, free to close. *(app-qc §Minor)*
- [ ] **DW-26** · Tests / build — three "cancels the in-flight probe" tests and `DebouncedProbeTests` prove negatives with fixed sleeps; the flake direction is a silent pass. A seam is already in hand in both files. *(app-qc §Minor)*
- [ ] **DW-27** · Tests / build — `RouteTrailTests.cs:27` is the only unguarded `Assert.All` in either assembly; it passes on an empty collection. *(app-qc §Minor)*
- [ ] **DW-28** · Tests / build — two tests assert `Assert.Equal(vm.OutputText, File.ReadAllText(path))`, deriving the expected value from the code under test (`FilenameListViewModelTests.cs:146`, `PageCountsViewModelTests.cs:229`). *(app-qc §Minor)*
- [ ] **DW-29** · Tests / build — `Scanner.Scan`'s `mtime_asc`/`mtime_desc` sorts are fixed (batch A) but unpinned by any test. *(app-qc §Status)*
- [ ] **DW-30** · Tests / build — `RestoreRemovedStillClearsEveryBatchAtOnce` is an already-true trap: neither assertion reads the batch stack; near-duplicate of `RestoreRemovedBringsThemBack`. Low stakes (FL-05's real pin discriminates both ways). *(app-qc §Test-suite validity)*
- [ ] **DW-31** · Tests / build — FL-04's other half, Ctrl+C routing to `PerformCopy()` (`FilenameListWindow.xaml.cs:103-108`), has no test; delete that branch and the suite stays green. *(app-qc §Test-suite validity)*

### Filename List living list, 2026-08-20 — 10

- [ ] **FL-21** · Filename List — "ignored" conflates *you filtered these out* with *these are broken*. *(FL audit §Low)*
- [ ] **FL-22** · Filename List — *Find* searches names only, even when the *Full path* column is on. *(FL audit §Low)*
- [ ] **FL-23** · Filename List — the two hidden-row mechanisms (Find filter, removals) share one dead-end message; the 2026-08-21 rewording stopped asserting a false cause but it still offers no way out. *(FL audit §Low; "PARTIALLY FIXED … stays open")*
- [ ] **FL-24** · Filename List — intake controls and view controls are interleaved across three toolbar rows. *(FL audit §Low)*
- [ ] **FL-25** · Filename List — no accessible names on this window's inputs (`AutomationProperties` absent). *(FL audit §Low)*
- [ ] **FL-26** · Filename List — Find label spacing is off-canon (6px button gap used for a label→control pair). *(FL audit §Low)*
- [ ] **FL-27** · Filename List — no default button; Enter does nothing. *(FL audit §Low)*
- [ ] **FL-28** · Filename List — `ToText` and `ToCsv` disagree about the empty listing (0-byte .txt vs header-only .csv). *(FL audit §Low)*
- [ ] **FL-29** · Filename List — `AddNote` truncates the sentence that explains what went wrong; no tooltip. *(FL audit §Low)*
- [ ] **FL-30** · Filename List — the Size column renders raw bytes; the Pages column added beside it renders formatted, Size is unchanged and open. Manifest spec §2.1 calls the fix its headline feature. *(FL audit §Low; named open in the fix-pass status block)* `(deferred)`

### v1-era audits (2026-08-04 / 2026-08-09) — 11

- [ ] **DW-32** · Tests / build — `QcTests.MatchMergeControlIdWithColonIsSafeToo` has a dead branch: `outcomes` is always empty, so its assertion never runs. *(08-04 §Theme 6)*
- [ ] **DW-33** · Tests / build — `WatchListRowTemplateTests.OpenDashboard` can leak a shown `SettingsWindow` into the shared `Application` if it throws before returning (no try/finally). *(08-04 §Theme 6)*
- [ ] **DW-34** · Tests / build — `Config.ValidateRoute`/`ProbeWritable`'s "not a folder" and "not writable" branches are untested. *(08-04 §Theme 6)*
- [ ] **DW-35** · Tests / build — both projects target `net8.0`, which reaches end of life **2026-11-10** (under three months away); noted, not migrated. Time-sensitive despite the Minor grade. *(08-04 §7.2 + 08-09 tests-build §.NET 8 EOL)* `(2 sources)`
- [ ] **DW-36** · Unlock — the suffix-mode path writes its final .pdf via `FileMode.CreateNew` with no temp+swap; a crash mid-write leaves a corrupt .pdf that matches `Scanner.Eligible`. Dead-code-adjacent per the source's own hedge. *(08-09 core §Minor)*
- [ ] **DW-37** · History / crash log — `LogCrash` appends to the shared `crash.log` via `File.AppendAllText` with no cross-process coordination; concurrent crashes from two stations can interleave entries. Distinct from R4 (rotation) and QC-21 (PHI content). *(08-09 security §Minor 4)*
- [ ] **DW-38** · Settings — `RecomputeDataFileNote`/`AdoptRepointedSection` resolve a side-file path with the unconfined `Config.ResolveBeside` instead of the confined `ResolveBesideForRead` every other read path uses; outcomes agree today. *(08-07 §2 Minor + 08-09 security §Minor 3)* `(2 sources)`
- [ ] **DW-39** · Tests / build — the mutable test seams in production code are owed consolidation into one `TestHooks` class "before it becomes six" — and the 08-09 audit then found a seventh (`ThemeManager.IsHighContrast`) already undercounted. *(memory: ordosort-rebrand-state, 2026-08-06 + 08-09 tests-build Part A)* `(2 sources)`
- [ ] **DW-40** · Tests / build — `ThemeTests.TextPairs()` is missing three pairings that render for real: `StatusAmber`/`Surface` (its Green/Red siblings have it), `AccentBronzeText`/`AccentBronze`, and `SubtleText`/`Surface`. *(08-09 ui §Minor 4 + memory: ordosort-rebrand-state)* `(2 sources)`
- [ ] **DW-80** · Tests / build — `publish.bat`'s comment overstates .NET 8 Desktop Runtime presence on "modern Windows"; still verbatim in the current script. *(08-09 tests-build §Minor 1)*
- [ ] **DW-81** · Tests / build — local publish output is not purely single-file: ~815 KB of WebView2 XML doc-comment files land beside the exe. *(08-09 tests-build §Minor 2)*

### Dropdowns & file connections QC (2026-08-07), plus measured column-cap defects — 11

- [ ] **DW-41** · Settings — a saved custom sound (.wav) file is never existence-checked, unlike every other file field. *(08-07 §1 Minor)*
- [ ] **DW-42** · Label Maker — "No printers found" also fires for a stopped print spooler; two causes, one message. *(08-07 §1 Minor)*
- [ ] **DW-43** · Settings — `TileVisibilityIndex` / `SoundChoiceVm.Choice` int↔string mapping is duplicated across XAML item order and a C# switch, unenforced. *(08-07 §1 Minor)*
- [ ] **DW-44** · Settings — the highlighted-row legibility fix is test-pinned for only 6 of 11 combos; the rest inherit by code-path tracing, not measurement. *(08-07 §1 Minor)*
- [ ] **DW-45** [U] · Settings — "Folder does not exist" is ambiguous between "never configured" and "share currently unreachable". *(08-07 §2 Minor)*
- [ ] **DW-46** · Core / filing spine — a narrow TOCTOU in `Commit.CommitFile`'s vanished-file check yields a raw error instead of the friendly "vanished" message. *(08-07 §3 Minor)*
- [ ] **DW-47** · Config — `ProbeWritable` creates/deletes its probe file directly inside the real destination folder, not a scratch location (`Config.cs:884-897` unchanged). *(08-07 §3 Minor)*
- [ ] **DW-48** · Repo / process — no `longPathAware` manifest declaration; the failure mode is a readable error rather than a crash, but the opt-in is absent. *(08-07 §3 Minor)*
- [ ] **DW-49** · Core / filing spine — roster-driven names have no length cap short of `PathTooLongException`. *(08-07 §3 Minor)*
- [ ] **DW-50** · Filename List — `FilenameListWindow` never calls `DataGridColumnCap.Track`, unlike every sibling multi-column window; measured and deliberately left 2026-08-20. *(memory: ordosort-filename-list-manifest)* `(deferred)`
- [ ] **DW-51** · Shell / main window — a `Collapsed` `DataGridColumn`'s `ActualWidth` is its `MinWidth`, not 0, so the cap math counts a phantom entitlement — measured 84.5px cap where 189.5px is correct. *(memory: ordosort-filename-list-manifest, 2026-08-20)*

### Memory-recorded deferrals and cross-audit follow-ups — 28

- [ ] **DW-52** · MatchMerge / Review matches — `MatchMergeWindow` folder expansion runs synchronously on the UI thread. *(memory: ordosort-review-matches-fixes, "known-unfixed, below the report cap", 2026-08-18)*
- [ ] **DW-53** · MatchMerge / Review matches — `MatchResult.Status` uses raw strings where sibling VMs use enums. *(memory: ordosort-review-matches-fixes)*
- [ ] **DW-54** · MatchMerge / Review matches — `Refresh()` is O(files × roster) per mutation. *(memory: ordosort-review-matches-fixes)*
- [ ] **DW-55** · MatchMerge / Review matches — the roster file is read twice per load. *(memory: ordosort-review-matches-fixes)*
- [ ] **DW-56** · Tests / build — `SessionDeferredResolutionTests` flake seen 2026-08-19 (`IOException` in `Commit.SkipFile`, passed in isolation) is not yet recorded in `docs/known-flakes.md`; batch A's Task 3 edited that suite, so re-check before recording. *(memory: ordosort-filename-list-manifest)* `(verify)`
- [ ] **DW-57** · Tests / build — the WPF test-host process-exit hang on this machine is not yet written up in `docs/known-flakes.md` (its suspected cause is DW-23's dispose asymmetry). *(memory: ordosort-list-reformatter-upgrade, 2026-08-19)*
- [ ] **DW-58** · Tests / build — `SettingsViewModelTests.TilePreviewExplainsEmptyAndMissingFolders` and `BulkRenameViewModelTests.HandEditSurvivesAnOpChange` are flaky under full-suite load only (pure-VM `WaitFor` timing). *(memory: ordosort-spacing-wrap-pass, 2026-08-17)*
- [ ] **DW-59** · Tests / build — `TabItemShowsTheBronzeFocusRing` is environment-sensitive (failed all afternoon then passed; headed focus test the machine sometimes denies keyboard focus). *(memory: ordosort-rebrand-state 2026-08-06 + ordosort-e2e-suite 2026-08-10)* `(2 sources)`
- [ ] **DW-60** · Tests / build — `UnlockProbeWritesNothingTests` is flaky under full-suite parallelism. *(memory: ordosort-e2e-suite, 2026-08-10)*
- [ ] **DW-61** · Tests / build — Box labels and Routing loop have one `clean` e2e scenario each; the spec promised a clean+awkward pair per surface (~25 lines each to close, or amend the spec). *(memory: ordosort-e2e-suite, 2026-08-10)*
- [ ] **DW-62** · Label Maker — `BoxLabelStore` IOException `HResult` filtering follow-up from the config-split reviews. *(memory: ordosort-rebrand-state, 2026-08-01)* `(verify)`
- [ ] **DW-63** · Label Maker — `ClaimNumbers` should be offloaded from the UI thread in `SavePdf`. *(memory: ordosort-rebrand-state, 2026-08-01)* `(verify)`
- [ ] **DW-64** · Settings — `AdoptRepointedSection` path-identity edge: wrap `ResolveBeside` in `Path.GetFullPath` before comparing. *(memory: ordosort-rebrand-state, 2026-08-01)* `(verify)`
- [ ] **DW-65** · Settings · Dashboard tab — flat-order ↑/↓ can look inert across group boundaries. *(memory: ordosort-rebrand-state, 2026-08-01)*
- [ ] **DW-66** · Settings · Dashboard tab — `DropWatch`'s null branch is untested. *(memory: ordosort-rebrand-state)*
- [ ] **DW-67** · Settings · Dashboard tab — per-keystroke Section rebuild overhead. *(memory: ordosort-rebrand-state)*
- [ ] **DW-68** · Settings · Dashboard tab — two headers show when a Section literally equals `MonitorTitle` (Ready merges them). *(memory: ordosort-rebrand-state)*
- [ ] **DW-69** · Settings · Dashboard tab — triple insert-shape duplication; an `InsertWatch` helper is owed. *(memory: ordosort-rebrand-state)*
- [ ] **DW-70** · Settings · Dashboard tab — "Add section" can duplicate a renamed default heading's text (cosmetic). *(memory: ordosort-rebrand-state)*
- [ ] **DW-71** · Settings · Dashboard tab — the user's visual acceptance pass on the tab is still recorded as pending (2026-08-01; may have happened unrecorded). *(memory: ordosort-rebrand-state)* `(user, verify)`
- [ ] **DW-72** · Shell / main window — `OnEnterAsync` still carries an inline copy of the enter-target formula; consolidate into `EnterTargetIndex()` when next touched. *(memory: ordosort-rebrand-state, accepted follow-up)*
- [ ] **DW-73** · Core / filing spine — `DebouncedProbe` checks its generation before `_uiContext.Post` rather than inside it (~300ms stale repaint window, all 11 probes). *(memory: ordosort-rebrand-state, 2026-08-06)*
- [ ] **DW-74** · Bulk Rename — `Recursive`/`Color` ride the debounced path though they're discrete clicks. *(memory: ordosort-rebrand-state, 2026-08-06)*
- [ ] **DW-75** · History / crash log — `History`'s `SqliteConnection` is undisposed when its constructor throws mid-open. *(memory: ordosort-rebrand-state, 2026-08-06)* `(verify)`
- [ ] **DW-76** · Website — the Settings gallery screenshot's lower half is empty; recapture on a denser tab (e.g. Destinations). *(memory: ordosort-website-state, accepted minor, 2026-08-08)*
- [ ] **DW-77** · Tests / build — `LabelMakerOverflowTests`' hosted-window pattern is now the template four probe suites depend on; the remaining coverage-style suites have never been asked "what did this actually measure?" — only the two named in QC-09/QC-26 were. *(app-qc §What this method would miss)*
- [ ] **DW-78** · Zip Tools — the batch-mutated-under-a-live-list defect class has now appeared four times (QC-05 ×2, Task 7's caught regression, QC-31); the audit recommends one sweep asking that question of every batch surface rather than fixing instances one at a time. *(app-qc §QC-31 note)*
- [ ] **DW-79** · Repo / process — `docs/superpowers/plans/2026-08-09-v1-release-blockers.md` still shows every checkbox unchecked although its tasks landed; stale by the repo's own dated-artifact convention, but the 08-09 audit counted it as a finding. *(08-09 tests-build §Minor 3)*

### Fresh QC, 2026-08-22 — 11

- [ ] **Q2-36** [V] · Tests / build — `WindowOverflowTests`' hand-maintained registry lacks the reflection discovery guard both sibling suites carry in the same assembly; a new window ships with zero overflow coverage and nothing says so. *(fresh-qc §Minor)*
- [ ] **Q2-37** [V] · Tests / build — the FilenameList overflow builder says "every column on" and omits the Pages column, so the window's real widest set is never rendered and the floor is calibrated to the narrower one. *(fresh-qc §Minor)*
- [ ] **Q2-38** [V] · Folder watch — `SetFolders` lacks the `_disposed` guard its neighbours have; a post-dispose call resurrects real watcher handles nothing will dispose. *(fresh-qc §Minor)*
- [ ] **Q2-39** [V] · Zip Tools / Unlock — three more cancelled-but-never-disposed CTS/semaphore pairs at close, beyond QC-25/DW-23: `ZipListViewModel._cts` (both tabs) and Unlock's `_probeGate`/`_probeCts`. *(fresh-qc §Minor)*
- [ ] **Q2-40** [V]/[U] · Config — `history_db` is the one config value validated nowhere: a blank or directory-pointing value is the only startup refusal naming no key/path/remedy ("SQLite Error 14"), blank clears its Settings note where blank-inbox renders a problem, and blank passes OK where the side-file keys get defaults. *(fresh-qc §Minor)*
- [ ] **Q2-41** [V] · Settings — a monitored folder with a blank path passes OK silently and becomes a permanent, unclearable error tile whose click is a no-op, while the route equivalent warns and disables with a reason. *(fresh-qc §Minor)*
- [ ] **Q2-42** [V] · Label Maker — a close refused for a duplicate id re-runs `TryPersist` from the top each attempt: every blanked row's destructive remove-confirmation is re-asked before the duplicate message, plus a fresh up-to-5s store read per attempt. Friction, not a trap. *(fresh-qc §Minor)*
- [ ] **Q2-43** [V] · Settings — the new blank-set-aside warning turns every OK into "Save anyway?" on a station that deliberately never configures one; an unconditional nag on an optional field. Introduced by batch A. *(fresh-qc §Minor)*
- [ ] **Q2-44** [V] · Shell / main window — `AsyncRelayCommand` is silent-by-default without `OnError`, and five more VMs never wire it; currently safe only because their Core calls promise never to throw. Class-sweep row, same spirit as DW-78. *(fresh-qc §Minor)*
- [ ] **Q2-45** [V] · Tests / build — the FolderMonitor ACL test bails with a bare `return` (zero assertions) wherever the deny-ACE doesn't hold — the only pin `120770c` has, and its flake direction is a silent green. *(fresh-qc §Minor)*
- [ ] **Q2-46** [V] · Tests / build — `UnknownOldestAgeRendersAsUnknownNotAHugeNumber` never exercises the sentinel detection that is QC-13 and its `DoesNotContain("155")` cannot fail for any input; the real pin lives in `PipelineTests` (sound). *(fresh-qc §Minor)*

---

## Deferred by the user — awaiting a go

Not open defects beyond those already listed; recorded so the decision is visible.

- **File-manifest-builder spec** (`specs/2026-08-20-file-manifest-columns-design.md`) —
  complete, self-reviewed, deliberately **not started**: "presented with the honest cost,
  the decision was to fix the worst bugs and add page counts the cheap way instead"
  (commit `66be355`). Starting it would close FL-07, FL-09, FL-10, FL-11, FL-12, FL-30.
  Offer it, don't assume it.

## Declined / accepted decisions — not open work

- Plaintext `saved_passwords` in the shared `config.json`; share permissions are the
  boundary; DPAPI and a per-station passphrase both declined. *(08-09 security §scope;
  08-04 §4.3 superseding note)*
- `history_db` deliberately unconfined. *(08-09 security §scope)*
- Settings' General tab dead space (08-02 M2) — accepted; a resizing dialog is the worse
  failure mode. *(08-02 pass2 §verify-then-decide)*
- `UnlockWindow` results virtualization (08-02 pass2 M1) — intentionally left; the markup
  alone would be inert without `CanContentScroll`. *(commit `444ab6f` reasoning)*
- Daily history backup blocking first paint (08-04 §5.3) — measured (tens of ms locally),
  decision recorded in a constructor comment: do nothing. *(08-04 §5.3)*

## Obsolete — superseded or feature deleted

The reports feature was deleted from `main` at the user's request (`f7736ef`, 2026-08-16);
its deferred items die with it unless reports return (the complete hub is archived on
`feature/reports-hub-phase2`, which WAS carried to the rebuilt repo).

- Reports deferred minors (TAT export formula guard, by-category blank area,
  `ProductionWindow.RebuildColumns` boundary-by-name). *(memory: ordosort-reports-state)*
- Menu glyphs E916/E9D9 visual check — the `_Reports` menu no longer exists.
- Reports subfolder-loading fix user verification — both report windows deleted.
- `feature/report-redesign` spec branch — not listed among branches carried in the
  2026-08-20 repo rebuild; presumed gone. Re-create from
  `specs/2026-08-11-report-redesign-design.md` if reports return.
- The "final logo art → v1.0.0 tag" gate and the website's v1.0 flip — releases shipped
  through v1.2.0 and `ordosort.com/index.html:55,:234` now carry live Download links; no
  FLIP comments remain.

---

## Reconciliation

Per-source arithmetic, checked against each source's own status record. **Total = closed
+ open + declined** for every row.

| Source | Total | Closed | Open | Declined | Source's own record |
|---|---|---|---|---|---|
| `2026-08-21-app-qc.md` — numbered | 31 | 15 | 16 | 0 | Status block: closed QC-01–09, 11–14, 16, 26; "everything else … still open" |
| `2026-08-21-app-qc.md` — Minor bullets | 15 | 1 | 14 | 0 | Status block says "every Minor" open — overridden for one bullet by code evidence (discrepancy 2) |
| `2026-08-21-app-qc.md` — ancillary (status-block 3, test-validity 2, working-tree 2, method-notes 2) | 9 | 0 | 9 | 0 | Recorded as deliberately left / needing answers / recommended follow-ups |
| `2026-08-20-filename-list-ui-audit.md` | 30 | 6 | 24 | 0 | Status block: "Closed: FL-01…FL-06"; "Partially addressed, still open: FL-10, FL-18, FL-23" |
| `2026-08-02-ui-audit.md` | 22 | 21 | 0 | 1 (M2) | Remediation plans + pass2 verify-then-decide outcomes |
| `2026-08-02-ui-audit-pass2.md` | 13 | 12 | 0 | 1 (M1) | Remediation plans; I6's 12 Settings sites closed later by `6701e24` |
| `2026-08-04-full-audit.md` | 30 | 22 | 7 | 1 (5.3) | Per-finding Fixed blocks; 1.4 deliberately open (row DW-01); 2.5 closed via 08-07 C2 (discrepancy 3) |
| `2026-08-07-qc-dropdowns-and-file-connections.md` | 26 | 5 | 21 | 0 | In-document FIXED marks (D5, C1, R1) + later closures (D1, C2) |
| `2026-08-09-v1-release-audit-core.md` | 4 | 1 | 3 | 0 | Counts line "Critical 0 / Important 3 / Minor 1"; Imp 3 folded into DW-01 (discrepancy 4) |
| `2026-08-09-v1-release-audit-security.md` | 4 | 1 | 3 | 0 | Counts line "Critical 0 / Important 2 / Minor 2" |
| `2026-08-09-v1-release-audit-tests-build.md` | 8 | 2 | 6 | 0 | Counts line "Critical 0 · Important 3 · Minor 5" (two Minors live unlabeled in Part A prose — noted) |
| `2026-08-09-v1-release-audit-ui.md` | 4 | 3 | 1 | 0 | 3 Important closed by `2115826` (+ reports removal); Minor 4 confirmed open in current `ThemeTests.cs` |
| `2026-08-22-fresh-qc.md` | 46 | 0 | 46 | 0 | New audit, all open; 4 High (Q2-01…04), 31 Important, 11 Minor; also settles marks on QC-23, DW-01, DW-19 empirically |
| **Doc totals** | **242** | **89** | **150** | **3** | |
| Memory (no self-count) | — | — | 28 unique | — | further memory rows resolved on verification (below); 6 obsolete |
| **Unique open rows** | | | **173** | | 150 doc rows − 5 cross-source dedupes + 28 memory-only |

**Cross-source dedupes (each is one row above, both sources cited):** DW-01
(08-04 §1.4 = 08-09 core Imp 3), DW-04 (08-04 §3.2 = 08-09 tb Imp 2), DW-05
(08-04 Theme 6 = 08-09 tb Imp 3), DW-22 (app-qc Minor = 08-07 §3 Minor), DW-38
(08-07 §2 Minor = 08-09 sec Minor 3). Also single-counted with a memory echo: DW-13,
DW-35, DW-39, DW-40, DW-59.

### Discrepancies settled during consolidation (none silently)

1. **QC-10** — omitted from the batch-A closed list. Settled **open**: no task in
   `plans/2026-08-21-app-qc-fixes.md` covers it, and the status block's "everything else
   is untouched and still open" catches it.
2. **"Every Minor is still open"** (app-qc status block) is wrong for one bullet:
   `EveryStarColumnDeclaresItsOwnFloor` received its floor in batch A's Task 1 item 4 —
   verified in `tests/OrdoSort.Wpf.Tests/DataGridSizingCoverageTests.cs:249-256`
   ("The same sanity floor its sibling above carries…"). Counted **closed**.
3. **08-04 §2.5** (Settings' false "resolves beside the config file" claim) initially
   reads open by absence of mention — but it is the same defect as 08-07's **C2**, fixed
   by commit `c3798f2` (2026-08-09) with a live code comment naming C2
   (`SettingsViewModel.cs:1031`). Counted **closed**. The surviving remainder of that
   family — routes/watch folders still raw — is open as **DW-06**.
4. **08-09 core Important 3** looks closed under QC-03's batch-A closure, but the
   mechanisms differ: QC-03 fixed the copy-succeeded-delete-failed branch; the
   kill-mid-copy partial-destination-file branch (= 08-04 §1.4) is untouched and
   deliberately open. One row, **DW-01**.
5. `specs/2026-08-20-file-manifest-columns-design.md:418` lists FL-01/02/04/05/06 as
   still open — **stale**, written one day before the fix pass; the audit's dated
   `FIXED 2026-08-21` marks win.
6. The tests-build audit's "Minor 5" tally only reconciles if two unlabeled Part-A prose
   items count as Minors; adopted that reading and noted it here.

### Memory items resolved on verification (closed — do not re-open from stale memory)

- Vercel/DNS pending user steps → done; Vercel relinked to the rebuilt repo and verified
  deploying ("Nothing from the rebuild remains open", memory 2026-08-20).
- Missing CI run for the 2026-08-21 push → explained (Actions billing blocked
  account-wide) and resolved (repo public 2026-08-21; first CI + E2E dispatches green).
- List-reformatter PR #10 → merged; the rebuild memory lists
  `feature/list-reformatter-upgrade` among branches "deliberately dropped … (merged)",
  and the rebuilt repo has no open PRs (`gh pr list` 2026-08-22: empty).
- Zipper Save-As delete-then-recreate → fixed via `AtomicPlace.TryReplace`; re-verified
  by the 2026-08-21 QC ("genuinely fixed").
- LabelMaker whole-list-overwrite Persist → closed by the 08-04 §2.4 field-scoped
  reconciliation commits (`8c20909`, `701e7d3`, `7ca1779`).
- Rebrand memory's "next-tier still open" list (2.4, 2.6, 3.3, 3.5, 7.1, 7.3,
  THIRD-PARTY-NOTICES, LICENSE) → all closed per the 08-04 reconciliation; 5.3 is a
  recorded declined decision.
- Bulk Rename post-Apply stale-plans re-execute window → closed by the
  `_lastRenderedPlans` discipline (`BulkRenameViewModel.cs:355-360`), cited as a landed
  deliberate fix by the batch-A plan (Task 7).
- Settings' 12 fixed 130px label columns → closed by `6701e24` (2026-08-09); current
  `SettingsWindow.xaml` has zero `Width="130"` column sites.
- E2E PR #4 "17+ commits behind" → merged 2026-08-11, shipped in v1.1.0.
- Website v1.0 flip / v1.0.0 tag / final-logo gate → superseded by shipped releases
  (v1.2.0) and live Download links (see Obsolete).

---

## Update procedure

How a fix batch keeps this tracker true. The checklist is the single place status
changes; the dashboard is a render of it and never edited independently.

1. **Close an item.** When a fix lands (test watched failing first, per the batch-A
   constraints), tick the box in this file and append the closure evidence to the row:
   `— CLOSED <date>, <commit/test>`. Move nothing; rows stay in place so IDs remain
   findable. If the source document has its own status block (the QC and FL audits do),
   the fixing branch updates that source too — the source stays the authority and this
   file must agree with it.
2. **Add an item.** New findings get their native ID if their audit mints one, else the
   next free `DW-nn` (next free: **DW-82**). Every row carries: ID · marks · surface ·
   one-sentence defect · source citation · tags.
3. **Re-verify `(verify)` rows before acting** — they were recorded from memory files up
   to three weeks old and the code may have moved.
4. **Update the snapshot** (counts in the header) and the reconciliation table row for
   the affected source. The three totals must still sum: Total = closed + open +
   declined per source.
5. **Regenerate the dashboard.** Edit the `ITEMS` array in
   `docs/superpowers/refinement-dashboard.html` to mirror this file's rows 1:1 (same IDs,
   severities, surfaces, tags, status), update its snapshot constants, then republish the
   same file to the same artifact URL (in a Claude Code session: call the Artifact tool
   with this file path and the existing artifact URL, keeping the favicon). The dashboard
   must be reproducible from this markdown alone — if the two disagree, this file wins
   and the dashboard is regenerated, never the reverse.
6. **Commit** checklist + dashboard together on the docs branch, message style
   `docs(tracker): …`, so tracker history reads as a progress log.
