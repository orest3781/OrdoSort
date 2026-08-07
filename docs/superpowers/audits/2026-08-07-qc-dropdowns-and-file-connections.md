# QC — drop-downs and file connections, 2026-08-07

Three read-only sweeps at `701e7d3`, run concurrently with an active fix round, so **none of them built or launched the app**. Everything below is traced from source unless marked otherwise.

Marks: **[V]** traced to code with a citation · **[U]** needs a run, two processes, or a real share to settle — a hypothesis, not a defect.

## 1. Drop-downs

**There are 11 real `ComboBox` controls**, not the 23 a naive grep suggests — the larger number counts `<ComboBoxItem>` lines.

| Window | Combos | Source | Binding | Persisted |
|---|---|---|---|---|
| MainWindow | Tile visibility | inline ×3 | `SelectedIndex` → int↔string map | yes (string enum) |
| SettingsWindow | Process order, Naming mode, Section, App font, Sound ×4 | static KVPs / computed / inline | `SelectedValue` / `Text` / `SelectedIndex` | mixed — sort strictly validated, rest soft |
| BulkRenameWindow | Letter case | inline ×3 | `SelectedIndex` | no, resets each Apply |
| MatchMergeWindow | First / Last / Control header | `Headers` collection | `SelectedItem` | yes, name-matched |
| PrintPreviewWindow | Printer | code-behind `Items.Add` | `SelectedItem` | no, re-enumerated |

### Critical

**D1 [V] — MatchMerge silently mismerges identity data when roster headers repeat or are blank.** `MatchMerge.cs:58-65` and `MatchMergeViewModel.cs:183-192` both resolve a chosen header to a column by **first match**. A roster with two identically-named (or blank) columns can collapse First, Last and Control onto column 0, while the UI reports "Roster loaded" and raises no error. This decides which person a document is filed against. Live behaviour **[U]**.

### Important

- **D2 [V] — a list drift refuses to start the app.** `SettingsViewModel.SortChoices` (Wpf) and `Config.Sorts` (Core) independently maintain the same list. On drift, `Config.Load` throws (`Config.cs:233-235`) and `App.xaml.cs:56-66` shows a dialog and exits, with no in-app recovery.
- **D3 [V] — 6 of 11 combos have no accessible name.** Process order, App font, Letter case and all three MatchMerge headers have neither `AutomationProperties.Name` nor a `ToolTip`; the adjacent label is sighted-only.
- **D4 [V] — no validation on `UiFontFamily`, route `NamingMode`, or `Section`.** A stale value renders the combo blank while the underlying value survives.

### Minor

The Sound "Custom .wav…" label never checks `File.Exists` (playback falls back gracefully, but Settings shows no warning, unlike every other file field); PrintPreview's "No printers found" also fires for a stopped spooler; `TileVisibilityIndex` and `SoundChoiceVm.Choice` map int↔string across two unenforced sources of truth (XAML item order and a C# switch); the highlighted-row legibility fix is test-pinned for only 6 of 11 combos — the rest inherit it by code-path tracing rather than measurement.

**Verified fine, do not re-investigate:** `PART_EditableTextBox` present and working; `IsTextSearchEnabled` coverage correct; `KvpValueTemplate`/`FontChoiceTemplate` resolve as intended; headerless-roster handling; the font list cannot be empty; keyboard reachability.

## 2. Configured paths

| Key | Resolves relative to | Confined on write |
|---|---|---|
| `inbox`, `deferred`, route `path`, watch `path` | **raw string → current directory** | no |
| `names_file`, `history_db` | beside `config.json` | `history_db` deliberately not |
| the four `*_file` side files | beside `config.json` | yes; reads still allow an absolute passthrough |

### Critical

**C1 [V] — two of three save paths were never migrated, and one browsed path bricks every save.** `SaveConfigNow` (`ShellViewModel.cs:1164-1169`) and `ApplySettingsAsync` (`:1471`) still call the full `Config.TrySave`, which rewrites all four side files on every call. The Data files tab's picker returns an **absolute** path and nothing blocks it, so once a user browses one of those four keys, `ResolveBesideForWrite` refuses it on every later save — tile toggle, merge headers, box labels, any Settings OK — producing "settings not saved" **forever**, until the config is hand-edited.

`ShellViewModel.cs:1203-1219` documents this exact hazard and states it was fixed. It was fixed for `SaveSavedPasswordsNow` only. **This is an incomplete migration introduced by the 2026-08-06 security program.**

**C2 [V] — the Settings UI states something false about two of its four path fields.** `SettingsViewModel.RecomputeFolderNote` (`:967-976`, text at `:972`) tells users a relative Inbox/Deferred path is "resolved beside the config file". It is not: `Inbox` and `Deferred` are consumed raw by `Scanner.Scan`, `FolderWatchService.SetFolders`, `Commit.SkipFile` and `OpenFolder`, and written back with only `.Trim()`. They resolve against `Environment.CurrentDirectory`, which merely *often* coincides with the config directory.

`NamesFile`/`HistoryDb` carry near-identical wording in the same tab and genuinely *are* resolved beside the config — so the tab is truthful for two fields and false for two. This pins audit finding 2.5 exactly.

### Important

Route and watch-folder paths share the same raw resolution as Inbox/Deferred, without the false claim (`Commit.cs:49`, `FolderMonitor.cs:41-70`); `ApplySettingsAsync` sets `_cfg = cfg` at `:1470` **before** checking `TrySave`'s result, so a failed save is still adopted in memory; only route destinations get a writability probe — Inbox, Deferred, side files and `history_db` get existence-only checks; nothing flags a station-specific absolute path landing in a shared config, and drive-letter/UNC coherence across stations is entirely unchecked.

### Minor

"Folder does not exist" reads identically for "never configured" and "share currently unreachable" **[U]**; `RecomputeDataFileNote`'s preview uses the unconfined resolver while the real read uses the confined one (outcomes agree today).

Previously-fixed items re-confirmed still holding: side-file write confinement, the box-labels existence oracle, atomic config writes, the box-labels truncation guard.

## 3. Runtime files

Inbox/routes/deferred are moved only, unbounded by design. `locked_archive_YYYYMMDD/` is never pruned (by design). `.unlocking.tmp` is cleaned on every normal run. `%TEMP%\ordosort_unlock_*.pdf` is cleaned in a `finally` — but **no startup sweep exists**, so a hard kill leaks one. `history.sqlite` is unbounded (it is the audit trail); `backups/history-*.sqlite` is count-bounded to 14. `crash.log` is appended forever. CSV export and box-label PDFs go to a user-chosen path. Roster and `names.txt` are read-only — confirmed never written. The WebView2 profile folder is created once and owned by Edge thereafter.

### Critical

None. Every document-moving path uses a non-overwriting `File.Move`; the "only ever moves files" promise holds structurally, not merely by convention.

### Important

- **R1 [V] — the one genuine silent no-op in the whole surface.** `HistoryBackup.BackupDaily` returns `null` on failure and **both** call sites discard it (`ShellViewModel.cs:135-137`, `:1397-1403`). A daily backup that fails every day fails silently forever — and the audit log is the only link between a filed document and its original identity.
- **R2 [V]/[U] — the daily backup is a raw `File.Copy` of a live database.** `HistoryBackup.cs:31`, against a DB the class's own doc says is routinely open on a share with concurrent writers. No SQLite backup API, no integrity check. A torn snapshot is plausible; proving it needs two processes **[U]**.
- **R3 [V] — two write paths still truncate in place.** `History.ExportCsv` (`:252`) and `BoxLabels.RenderPdf` (`BoxLabels.cs:275`) write directly with no temp+rename, unlike `Config.WriteAtomic`, which exists precisely because in-place truncation destroys a file on a crash. Both are consent-gated by a save dialog, but a route folder full of real documents is a plausible target.
- **R4 [V] — `crash.log` grows unbounded** with no rotation, and is shared across stations when the config is shared; append atomicity over SMB is **[U]**.

### Minor

No temp sweep after a hard kill; a narrow TOCTOU in `Commit.CommitFile`'s vanished-file check yields a raw error rather than the friendly "vanished" message; `Config.ProbeWritable` writes its probe file into real destination folders; no `longPathAware` manifest declaration (failure is a readable error, not a crash); roster-driven names have no length cap short of `PathTooLongException`.

Already-known items re-verified and holding: `Naming.RejectIllegal` coverage including roster-driven renames, `.unlocking.tmp` naming, `Unlock`'s `CreateNew` write with gated cleanup, and the documented cross-volume partial-move gap at `Commit.cs:19-27`.
