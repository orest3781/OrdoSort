# OrdoSort — Full Audit, 2026-08-04

Nine parallel read-only audits at HEAD `b78ce53` (33 commits, `main` synced with origin, 879 tests green). Dimensions: data integrity, concurrency/lifetime, security, resilience, config/migration, performance/scale, build/distribution, test quality, dependencies/docs/hygiene.

This is deliberately **not** a third UI pass. Two UI audits (`2026-08-02-ui-audit.md`, `-pass2.md`) already swept that surface and their findings are remediated; re-running them would mostly re-find fixed work. These nine cover the angles those passes did not.

## Method and its limits

Every audit was **static**. Nothing was built, run, or profiled — nine concurrent rebuilds would have corrupted each other's view of the tree. That bounds what this document can claim, and the bound is marked per finding:

- **[V]** — verified by me directly against the code during synthesis, independently of the agent that reported it.
- **[A]** — agent-verified statically with a code citation; I did not re-check it.
- **[U]** — **unverified**; needs a dynamic test, a second process, or a real network share to settle. Treat as a hypothesis, not a defect.

This codebase has a documented history of confident false readings surviving until someone re-measured, so nothing here should be actioned on the strength of its severity label alone — check the mark first.

**Severity is normalized across all nine audits, not taken from them.** Individual audits disagreed on scale (one dimension labelled four findings Critical that another dimension's rubric would have called Important). The scale used here:

| | Meaning |
|---|---|
| **Critical** | Data loss, corruption, or an unusable app, in ordinary use, with no user error |
| **Important** | Real user harm under plausible conditions, or a broken promise the app makes in its own words |
| **Minor** | Robustness, hygiene, or quality — no user-visible harm on a normal day |

## Headline

**No finding shows OrdoSort destroying a user's document in ordinary filing.** The core commit / skip / undo / bulk-rename / match-merge paths were traced end to end and genuinely never overwrite or delete: every move funnels through a non-overwriting `File.Move` behind a collision guard, and every user-supplied name funnels through `Naming.RejectIllegal`. The data-integrity audit went looking for a document-destroying path in normal use and could not construct one. That is the single most important result in this document, and it is the foundation everything below sits on.

What the audit did find is that **the app's two central promises have leaks at the edges** — and those promises are load-bearing, because the app puts them in front of users verbatim:

> *"Nothing was deleted — OrdoSort only ever moves files, so the document is either where it started or where it was going."*

Four independent findings each break one half of that sentence, and no single audit could see the pattern because each owned only one of them.

---

## Theme 1 — The audit-log and move guarantees leak at the edges

**1.1 — Exit or OS shutdown mid-commit silently loses the audit row. [V] Critical — FIXED `e15bdb4` (bound `3373560`)**

> Fixed: `MainWindow`'s `Closing` handler now checks `ShellViewModel.IsBusy` before letting the `_reallyExit` path land, and re-issues the close once the in-flight commit finishes (`e15bdb4`, capped at a 10s backstop). A code-review round found the backstop itself could leave the window permanently unclosable if a commit never finished; `3373560` adds a one-shot `_forceClosing` bypass so the timeout is genuinely terminal. Verified end to end by Task 6's gate: `File > Exit` and the second-launch first-run path both leave no surviving process.

`MainWindow.xaml.cs:113-115` — the `Closing` handler opens with `if (_reallyExit) return;`, taking no busy check at all. `_reallyExit` is set by File > Exit and by `Application.SessionEnding` (OS shutdown/restart). `Closed` then runs `Shell.Dispose()` → `_history.Dispose()`, while `CommitCurrent` is still moving the file and writing its row on a thread-pool thread (`ShellViewModel.cs:876` — `await _scheduler.Run(() => _session.CommitCurrent(typed, route))`).

The file moves. The row does not get written. The app's stated invariant — every move is in the log — is broken, and the disposal races an active SQLite command besides.

The sharpest detail: the same handler's *other* branch carries the comment `// respects the mid-commit busy guard`. The guard exists and is trusted three lines below. The exit path is the one place that skips it. A user pressing File > Exit while the last document files, or Windows restarting for updates, is not an exotic scenario.

**1.2 — `Unlock` holds the product's only overwrite and only foreign-file delete. [V] Important — FIXED `e364988`**

Across all of `OrdoSort.Core` there are exactly three places that can delete or overwrite: a self-created write-probe (`Config.cs:447-448`, benign), backup pruning (`HistoryBackup.cs:38`), and `Unlock`.

- `Unlock.cs:146` — `File.WriteAllBytes(target, unlockedBytes)` truncates whatever is at `target`, under a check-then-act gap after `CollisionFree`.
- `Unlock.cs:250` — the failure path calls `RemoveQuietly(target)`, deleting a file this process may not have created. On the shared folder the app is explicitly designed for, that can be a file another station just placed.

The race window itself is **[U]** — proving it needs two processes. The *capability* is **[V]**: this is the one place the "only ever moves files" sentence is not literally true.

> Fixed (`e364988`): the buffered write path now opens `target` with `FileMode.CreateNew` instead of `File.WriteAllBytes` — a name taken by a peer in the gap after `CollisionFree` now fails the call atomically instead of being silently truncated. The streamed path was checked and was already create-only (`File.Move`'s two-argument overload). `PlaceAndSwap`'s `place` parameter gained a `markCreated` callback that fires only once the file genuinely exists on disk; all four `RemoveQuietly(target)` sites (the immediate failure path plus three inside the archive/swap block) are now gated on `createdTarget`, so a failed create can never delete a file this call didn't make. Verified live, not just unit-tested: Task 5's gate unlocked a real encrypted demo PDF (`northgate_001.pdf` from `demo-full`) end to end and confirmed the archived original and the new unlocked file were exactly where the app's own reassurance text says, nothing else touched.

**1.3 — Unlock's in-place swap has a crash window that strands the document in a third place. [A] Important — FIXED `93b3528`**

`Unlock.cs:261-301`. A kill or power loss between the two moves leaves `X.unlocking.pdf` plus the original in `locked_archive_YYYYMMDD` — a location the reassurance copy never names. The leftover can also re-enter the inbox queue.

> Fixed (`93b3528`): the in-place swap's intermediate name changed from `<stem>.unlocking.pdf` to `<stem>.unlocking.tmp` — closing only the half of this finding that's fixable (two moves still can't be made atomic, so a crash between them still leaves the unlocked copy under the intermediate name plus the archived original in `locked_archive_YYYYMMDD`; it just can no longer masquerade as a document). Confirmed before touching anything that the old name needed fixing: `Scanner.Eligible` is a bare `EndsWith(".pdf")` in three of four naming modes (and insert mode's own regex also matched whenever the stem already contained `--`, the normal case for a re-unlocked document), and `FolderMonitor`'s watch-folder filters match on `Path.GetExtension` — both would have picked up the leftover as a real document. `.tmp` fails both checks and matches the codebase's existing in-progress-write convention (`Config.cs`'s own atomic-write temp files). Verified live at Task 5's gate: unlocking a real demo PDF left no `.unlocking.tmp` or any other intermediate behind.

**1.4 — An interrupted cross-volume move can give garbage the canonical name. [A]/[U] Important**

`Commit.cs:24,44-47`. `File.Move` across volumes is copy-then-delete. Interrupted, it can leave a partial file at the destination; on retry the collision counter hands the *real* document the `" (2)"` suffix while the partial keeps the canonical name — and nothing detects it afterwards. The crash case is verified by construction **[A]**; whether Win32 `MoveFileEx` cleans up its own partial on a non-crash failure is **[U]**.

> Left deliberately, not fixed. Recorded as a code comment directly above `MoveNeverOverwrite` in `Commit.cs:19-27` (bracketing the same `File.Move` call and the `Build()` collision counter this finding names), spelling out the unproven window and that closing it needs a disk-full or kill test across two real volumes — dynamic proof no static pass or single-machine live launch can produce. Task 4 of the 2026-08-06 program confirmed the pickup was real (not cosmetic) before adding the comment, and left `Commit.cs`'s move logic itself untouched. Confirmed still present and unaltered as of Task 5's gate.

**1.5 — Undo's three failure branches are entirely untested. [V] Important — FIXED `902adbb`**

`Commit.cs:92` (filed file gone), `:94` (original name reused), `:97` (inbox folder vanished). Zero `.cs` test files reference `UndoAction` — the only matches are compiled DLLs. Undo is the safety net for a mis-filed document, and its failure handling is the least-exercised code in the product.

> Fixed: Seven new tests now pin the three failure branches individually and the state-preservation property that matters: after a failed undo, the undo stack entry survives, `Filed`/`Skipped` are unchanged, `Pos` is unchanged, and the history row is **not** marked reverted. All three guards already behaved as documented — no correctness issue there.
>
> **Real defect found and fixed:** `MoveNeverOverwrite` throws a private `FileExistsRace` on a collision; `CommitFile` retries it but `UndoAction` let it escape unhandled. A last-instant race in guard 2's own window produced a generic "didn't finish" dialog instead of the actionable "already exists again" message guard 1 gives two lines earlier. Fixed: `UndoAction` now catches `FileExistsRace` and rethrows the byte-identical `CommitError` message its `:94` guard produces. The race is forced deterministically via an internal test-only seam (`Commit.RaceHookForTests`) rather than a timing-dependent test.
>
> **Guard 2 survives:** After removing guard 2 (`Commit.cs:94`), all seven tests still pass. The `FileExistsRace` path via `MoveNeverOverwrite`'s own `File.Exists` check provides a byte-identical fallback. Guard 2 was kept anyway — it is the fast, non-exception path for what is actually the routine failure, whereas the `FileExistsRace` mechanism elsewhere in this file is reserved for the rare last-instant race. The safety property remains covered even when the guard is deleted.

---

## Theme 2 — Shared-config, multi-station mode is under-defended

The app documents a `--config <shared-path>` mode with several workstations against one share. That mode is where most of the remaining risk lives.

**2.1 — Settings > OK silently clobbers another station's concurrent edit. [V] Critical — FIXED `a7d8407` (refined `86be636`)**

`RefreshSharedSectionsFromDisk()` (`ShellViewModel.cs:1053`) has exactly **one** caller: `SaveConfigNow()` at `:1036`, whose own doc comment scopes it to "tool windows saving their own state". The primary Settings-OK path (`SettingsViewModel.ApplySettingsAsync`) never calls it.

Two people editing settings on two stations, no crash and no hand-editing required: the second to press OK overwrites the first's destinations/monitored-folders/alerts with a stale in-memory copy, with no warning.

> **This audit's own recommended fix below ("call `RefreshSharedSectionsFromDisk` from Settings-OK") was wrong, and it is worth recording why.** That helper's entire purpose is to *overwrite* the in-memory shared sections with whatever is currently on disk — it is correct for a tool window that has no edits of its own to lose. Settings is the opposite case: its whole reason for existing is that the user is *changing* those sections. Wiring it in would have silently discarded the user's own edits on every save, trading one silent-clobber bug for another. The actual fix is conflict *detection*, not refresh: `FreshConfigForSettings()` fingerprints the three shared section files when the Settings session opens (a SHA-256 of each file's bytes, per `86be636` — an initial `(path, mtime, length)` fingerprint in `a7d8407` produced false-positive prompts whenever a peer rewrote a section with byte-identical content). `ApplySettingsAsync` re-fingerprints before saving and, on a mismatch, asks the user via `IDialogService.Confirm`, naming the changed section(s); declining leaves both disk and the in-memory `_cfg` untouched. `SaveConfigNow`'s own `RefreshSharedSectionsFromDisk` call is unchanged — this fix added a second, different mechanism to the path that needed one, rather than reusing the wrong tool.

**2.2 — A 0-byte `box-labels.json` silently wipes every counter. [V] Critical — FIXED `fe8b110`**

`BoxLabelStore.Mutate:126-128` treats empty content as `new BoxLabelsDoc()`. `BoxLabelStore.Read:44-51` hands the same empty string to `Deserialize` and throws `ConfigException("not valid JSON")`. **The two functions disagree about what a 0-byte file means**, and `Mutate` is the one that then truncates and rewrites (`:140-143`).

So: a crash mid-write leaves 0 bytes → the next station's box claim reads "no clients yet", wipes all counters, and can reissue box numbers already printed on physical boxes. Physical-world consequences, no crash needed on the second station.

> Fixed: `fe8b110` closes the gap two ways rather than reusing Task 1's temp-file pattern (which cannot apply here — `Mutate`'s exclusive `FileStream` *is* the cross-station mutex, and swapping the file would release the lock other stations wait on). `existedBefore`, captured via `File.Exists` before the stream opens, lets `Mutate` distinguish genuine first-run-empty from crash-mid-write-empty and refuse the latter exactly as `Read` does; and the write order was changed from truncate-then-write to write-then-truncate so a fresh crash cannot produce a 0-byte state either.

**2.3 — No file is written atomically, anywhere. [V] Critical — FIXED `ceada04` (review round `d52208c`)**

`Config.SaveMain`/`WriteDoc` use `File.WriteAllText`; `BoxLabelStore.Mutate` uses `Seek(0)` + `SetLength(0)` + write, in place. No temp-file-then-`File.Replace` anywhere. Disk-full or a kill mid-write destroys the previously valid file. For `config.json` that bricks every station sharing it until someone fixes it by hand, because `Config.Load` throws and `App.xaml.cs` responds with `Shutdown(1)`.

This one finding is the root cause of 2.2 and compounds 3.1. It is the highest-leverage fix in the document.

> Fixed: `Config` writes now go through a temp file + `File.Replace`, with a 500ms retry budget (50 × 10ms) for a concurrent reader holding the destination open (`ceada04`). `d52208c` is a review-round follow-up: it restored `File.Replace` (preserving ACLs) in place of an initial delete-then-move approach, documented the retry budget, and added a retry-exhaustion test. `BoxLabelStore` deliberately does **not** use this pattern — see 2.2 above for why.

**2.4 — LabelMaker's dirty tracking is per-row, not per-field. [A] Important — FIXED `8c20909` (fix round `701e7d3`, fix round `7ca1779`)**

Editing an unrelated field on a client and closing the window writes back that client's *stale* `NextNumber`, rolling back a counter another station already advanced — duplicate box numbers, contradicting the code's own stated intent.

> Fixed (`8c20909`): field-scoped reconciliation, not a full per-field dirty-tracking rewrite. `_dirty` keeps its existing row-granularity meaning (duplicate-id refusal, rename remove/add, the zero-edit fast path); one narrow addition, `_numberEdited`, records only "the user typed into `NextNumberText` on this row." Inside `Persist()`'s `Mutate` callback — which already holds the exclusive lock and already has the fresh on-disk client in hand — `fresh.NextNumber` is now overwritten from the edited VM only when `_numberEdited` contains that row; otherwise disk wins. A deliberate edit to the number box still always lands, even over a peer's concurrent advance. Fix round `701e7d3` closed a **Critical** the field-edit fix didn't cover: a *rename* routes through `_removedIds` the same as a removal, so the merge's "same-id fresh row" lookup never found one for the new id and the guard never fired — `_originId` (the row's true on-disk identity at load time, never updated on rename) now lets `Persist()` snapshot the *origin* row's fresh `NextNumber` before the `_removedIds` sweep runs, carrying a peer's advance forward across the rename. Fix round `7ca1779` closed a further gap in that same mechanism: a round-trip rename (X→Y→X) still rolled the counter back, because the skip condition compared the *current* id back to origin instead of asking `_removedIds` — the actual source of truth for what's about to be deleted — directly; fixed by asking `_removedIds.Contains(origin)`, which also resolves any longer hop chain (three-hop-return verified by a dedicated test) without counting hops at all. Removal and add-after-remove were both traced and found already safe by design (removal is explicitly, unconditionally destructive, and the confirm dialog says so before the user clicks through). All three rounds proven with a revert-rebuild-reconfirm teeth proof, not just a passing suite. Wpf suite grew 561 → 570 across the three rounds (2, then 5, then 2 more new tests).

**2.5 — Settings tells users something false about relative paths. [A] Important**

The Settings UI says a relative Inbox/Deferred path is "resolved beside the config file". Verified false by grep across the runtime: `Scanner.Scan`, `FolderMonitor`, `Session` and `OpenFolder` all use the raw string. Only `names_file`, `history_db` and the four `*_file` overrides actually resolve beside the config. This breaks the documented shared-config mode and misleads precisely the user trying to set it up.

**2.6 — Unsynchronized SQLite connection and unsynchronized session queue. [V]/[A] Important — FIXED `ba34dc2` (fix round `62f1a99`)**

`History.cs:48` holds one `SqliteConnection` with no synchronization; File > Export/View history is reachable during Processing and runs concurrently with `LogCommit` **[A]**. Separately, `Session.Current` (`Session.cs:45`) reads `Pos` twice in one expression — `Pos < Queue.Count ? Queue[Pos] : null` — so a `Pos++` on the commit thread between the two reads throws `IndexOutOfRangeException` on the UI thread at the last document **[V]**.

> Fixed (`ba34dc2`): a private `readonly object _gate` on `History`, `lock`-wrapped around every method touching `_conn` (`Exec`, `LogCommit`, `MarkReverted`, `Count`, `Rows`, `RankedNames`, `Dispose`, and the test-only introspection methods) — the smallest correct change, chosen over a reader-gets-its-own-connection approach because this database can live on an SMB share, where extra file handles are exactly the cost to avoid. `ExportCsv` is the one method where the lock does **not** span the whole body: it covers only the `SELECT` and its materialization into memory, released before the `StreamWriter` file write that follows, so a slow export (potentially to a share of its own) can never freeze a concurrent `LogCommit` behind it. `Session.Current` now reads `Pos` once into a local before both the bounds check and the indexer see it. Proven with a genuine, unamplified data race, not a mocked one: `ConcurrentLogCommitAndReadsDoNotThrowOrCorrupt` drives 5,000 real `LogCommit` calls against a concurrent reader thread hammering `Rows`/`RankedNames`/`ExportCsv` — reliably reproduced `ArgumentOutOfRangeException` and a corrupted-connection `NullReferenceException` out of `SqliteConnection.Dispose()` on the unfixed code (3/3 runs), zero failures fixed, ~45–53 seconds per run at production `synchronous=FULL` settings (not weakened for CI speed). `Session`'s race was reproduced deterministically via reflection on its own private `Pos` setter rather than trying to win a race against real commit I/O. Fix round `62f1a99` (a review finding on `ba34dc2`) closed the identical hazard one expression away: `Current`'s ternary also read `Queue` twice, the same class of bug in the same getter — no live crash path today (every UI entry point that could race `Start()` is gated by `ShellViewModel._busy`), but an unenforced convention, not a guarantee `Session` itself makes; both `Pos` and `Queue` are now read once into locals, reproduced the same deterministic reflection way and confirmed to fail 3/3 on the pre-fix getter. The same round also closed a related gap the class doc overclaimed: `Migrate()`'s inline `PRAGMA table_info` read bypassed the lock (harmless — constructor-time, pre-publication — but routed through it anyway for the doc comment to stay honest). Core suite grew 422 → 425 across both commits (`ba34dc2` +2, `62f1a99` +1); Wpf unaffected at 575.

---

## Theme 3 — First run and deployment

**3.1 — First run in a non-writable folder leaves an invisible, un-closeable process. [V] Critical — FIXED `ea49754`**

Full chain, every link verified:

1. `Config.Load:172-176` calls `Save(fresh, path)` on first run with no guard — unlike the read path beside it.
2. `Config.Save` → `SaveMain` → `File.WriteAllText`. Every `ConfigException` wrapper in the file (`:182-260`) is on the **read** path; the write path is unwrapped, so this surfaces as `UnauthorizedAccessException`.
3. `App.xaml.cs:51` catches only `ConfigException`. The exception escapes `OnStartup`.
4. `DispatcherUnhandledException` (`:28-37`) shows a dialog and sets `ex.Handled = true` — but never calls `Shutdown()`.
5. `App.xaml:5` sets `ShutdownMode="OnMainWindowClose"`, and no window was ever created.

Result: the process survives with no window and no way to close it but Task Manager. Install to `Program Files`, run as a normal user, and this is the first thing a new user experiences.

It compounds: the dialog tells the user "The technical details were written to crash.log, beside your config file" — but `LogCrash` (`:100-110`) writes to that same unwritable directory inside a swallowing `try`. The message is false in exactly the case that produces it.

> Fixed (`ea49754`): `Config.Load`'s first-run bootstrap write now wraps `IOException`/`UnauthorizedAccessException`/`NotSupportedException` as a `ConfigException` with an actionable message — the same treatment every other `Load` failure already got. `App`'s `DispatcherUnhandledException` handler now calls `Shutdown(1)` whenever `MainWindow` is still null, closing the whole class of windowless-startup-failure, not just this one cause. `LogCrash` now returns whether it actually wrote `crash.log`, so the crash dialog no longer promises a file a locked-down folder just failed to produce. Task 6's gate ran this against a real unwritable location (a config path whose parent segment is a file, not a directory — the same construction `FirstRunFailureTests.AnUnwritableLocationIsReportedAsAConfigurationProblem` uses) rather than relying on the unit test alone: the app showed the honest message above and `tasklist` confirmed no process survived dismissing it.

**3.2 — Releases ship unsigned. [A] Important**

A `v*` tag produces two unsigned single-file `win-x64` zips (framework-dependent and self-contained). The Trusted-Signing step is wired but skipped, confirmed by there being zero signing secrets configured. SmartScreen will block non-developer users on first run — the exact audience.

**3.3 — No version anywhere, and no About box. [A] Important**

Every non-tag build defaults to `1.0.0.0`, and the app has no About dialog at all. A user cannot tell you what version they are running, which makes every future bug report harder.

**3.4 — No LICENSE, on a public repo shipping binaries. [V] Important**

No `LICENSE`/`COPYING` in the repo root, on a public repository whose README invites downloads, while the single-file exe embeds WebView2's BSD-licensed native DLL with no third-party notices.

**3.5 — WebView2 prerequisite undocumented; its failure is a raw dump. [A] Minor**

The Evergreen runtime is present on current Windows 11 but not guaranteed. There is no bootstrapper, check, or documented prerequisite, and init failure surfaces `ex.ToString()`.

---

## Theme 4 — Security

Threat model: a malicious PDF or spreadsheet, a hostile share, another user on the same machine. Not a remote attacker.

**4.1 — The PDF viewer is completely unhardened. [V] Important — FIXED `f821dfe`**

`WebViewPdfViewer.cs:31-47` creates the environment and calls `EnsureCoreWebView2Async`. That is all. Zero matches across all source for `NavigationStarting`, `NewWindowRequested`, or any `CoreWebView2Settings` property; the only browser argument set is `--disable-smooth-scrolling`. `ShowAsync` then navigates to a `file://` URL.

So the app's defining untrusted input — a PDF arriving from a scanner or a share — is rendered by a browser with every default capability on. A link annotation can navigate the pane to any http(s) or `file:` URL, run script there, spawn popups, and start downloads. The natural attack is a convincing fake "enter the PDF password" prompt inside the app's own viewer pane. Mitigating: there is no host-object or web-message bridge, so there is no path back into the process.

> Fixed (`f821dfe`): `WebViewPdfViewer.InitAsync` now gates every navigation through a pure `IsPermittedNavigation` predicate enforced by a `NavigationStarting` handler — only the document the viewer itself asked to show is allowed (`about:blank` is always permitted too, because `ReleaseAsync` depends on it); anything else, including a link annotation to `http(s)://`, `file://`, or `javascript:`, is silently refused. `NewWindowRequested` is blocked and `DownloadStarting` is cancelled. Settings now turn off host objects, web messages, script dialogs, devtools, password autosave, general autofill, and the status bar. The going-in hypothesis was that `IsScriptEnabled = false` might not be viable — Edge's built-in PDF viewer is itself a web application, so disabling script could break rendering — and that hypothesis turned out **false**: a real launch against `demo-full`, screenshotted at the pixel level (not just navigation-checked), showed the PDF toolbar and page text rendering correctly with script off, across more than one document. So the pane ships with **both** script disabled and the navigation allowlist, rather than leaning on the allowlist as the sole defense. `AreDefaultContextMenusEnabled` and `AreBrowserAcceleratorKeysEnabled` were deliberately left on — a stated user decision, security-only with no UX change — and the gate confirmed a right-click inside the pane still opens the standard Edge context menu (Back/Forward/Refresh/Print/Rotate/Send tab to your devices).

**4.2 — Config-controlled absolute paths give an arbitrary-file write. [A] Important — FIXED `6337a74` (fix round `37e887d`)**

`Config.cs:240-243` accepts *absolute* `destinations_file` / `alerts_file` / `monitored_folders_file`, and `Save`/`TrySave` writes JSON there unconditionally. On the shared deployment the code explicitly anticipates, anyone who can write `config.json` gets a file of their choosing overwritten on the victim's machine at the next settings save. `Route.Path` likewise can silently redirect filed documents to a hostile UNC share.

> Fixed: the fix splits read from write rather than rejecting absolute paths outright, because the Settings "Data files" tab's Browse… buttons (`OpenFileDialog` for all four keys) always hand back an absolute path with no relativizing — a real, already-shipped capability, not a hand-edit-only corner case. **Write** (`Config.Save`/`TrySave`) now refuses any path — absolute, `..` traversal, or a Windows rooted-without-drive path — that resolves (via `Path.GetFullPath` on both sides of the comparison, with a trailing separator appended to the config-directory side so a same-prefixed sibling directory can't fool it) outside the config's own directory. **Read** keeps loading an already-configured, fully-qualified absolute path unchanged, so a station with a working Browse…-picked path doesn't lose its data at the next startup; anything else that would escape on read is refused exactly like a write. Fix round `37e887d` closed a residual existence oracle in the `box_labels_file` bootstrap (`Save`/`TrySave` used to probe `File.Exists` via the unconfined path before confining the write, letting an attacker-controlled path distinguish "exists on the victim's disk" from "doesn't"; now resolves once, confined, and reuses that path for both the probe and the write). `history_db` is deliberately left unconfined — documented at both `ResolvePath(cfg.HistoryDb, ...)` call sites in `ShellViewModel.cs` (the constructor and the Settings-apply swap) — because unlike the four side files, `History.cs`'s own class doc says this database is designed to live on its own SMB share independent of where `config.json` lives; confining it would break that documented, supported deployment, and the residual exposure was verified narrower besides (pointing it at an absent path only plants an empty, app-schema'd file, and pointing it at an existing non-database file does not overwrite it — SQLite validates the header before touching page content).

**4.3 — Legacy plaintext passwords never expire at rest. [A] Important — FIXED `64e556c` (fix rounds `0ad4766`, `57efd95`)**

`ReprotectLegacyPlaintext` has three callers, all on save paths; nothing sweeps at load. A hand-edited plaintext entry stays plaintext in `config.json` forever if the saved list is never touched. (DPAPI scope `CurrentUser` with null entropy is the correct choice here — do not "fix" it to `LocalMachine`.)

> Fixed: `UnlockViewModel`'s constructor — the class's own "load" moment, run every time the Unlock window opens — now also runs `ReprotectLegacyPlaintext`, previously wired only to the three save paths. Gated on the sweep's own return value, so a config with nothing to convert is never re-saved (no needless write to a shared `config.json` on every open); when something *is* converted, a one-time `IDialogService.Info` notice fires, warning that a colleague on another station will need to re-enter and re-save their copy, since DPAPI `CurrentUser` scope makes the newly-protected value unreadable anywhere else. Fix round `0ad4766` hardened the failure mode: a DPAPI (`CryptographicException`) failure on any entry now rolls back every entry converted so far in that pass and warns instead of letting the exception escape the constructor and permanently brick the Unlock window; the passive save was also narrowed from the whole-config `SaveConfigNow` to a new `ShellViewModel.SaveSavedPasswordsNow`, which re-reads the fresh on-disk config and overlays only `SavedPasswords`, so a peer station's concurrent edit to the main config section survives. Fix round `57efd95` widened the catch from `CryptographicException` to `Exception` (Finding 1's promise is that *no* failure here may cost the user the tool, not just the documented one) and guarded `SaveSavedPasswordsNow` against an absent `config.json`, which `Config.Load`'s first-run path would otherwise have silently recreated with factory defaults, clobbering every peer's settings through a different door. **Verified live, not just unit-tested, at Task 5's gate**: `demo-full`'s own `config.json` held two genuinely plaintext saved passwords going in; opening Unlock showed the exact "OrdoSort — saved passwords protected" notice, and both entries were confirmed rewritten to `dpapi:`-prefixed values on disk immediately after. Closing and reopening Unlock a second time — config now clean — showed no dialog at all, confirming the sweep is correctly idempotent and silent when there is nothing to convert.

**4.4 — Verified non-findings, recorded so nobody re-litigates them. [A]**

Path traversal is genuinely blocked: `Naming.RejectIllegal` (`Naming.cs:104-113`) rejects separators, colons, wildcards, control characters and device names, and *every* write funnels through it — including MatchMerge's roster-driven renames via `BulkRename.Plan:141`. SQL is parameterized. CSV export already guards formula injection. All three `Process.Start` calls pass a single existing path with no argument string. `XlsxTable` is safe from XXE (`XDocument.Load` prohibits DTDs) and zip-slip (nothing is extracted) — though a crafted cell reference like `r="AAAAAA1"` can drive a ~12M-slot allocation (`XlsxTable.cs:49,116-125`), a local DoS only.

---

## Theme 5 — Performance and scale

Envelope: inbox/session of tens to ~2,000 files; ~10 routes; history is an intentionally unbounded, never-pruned SQLite audit log, shared over SMB by design — plausibly tens of thousands of rows within a year.

**5.1 — The history table has no indexes at all. [V] Important — FIXED `79b9289`**

Zero `CREATE INDEX` statements anywhere in the codebase. `RankedNames()` (`History.cs:152-164`) runs `GROUP BY name_entered ... ORDER BY MAX(ts_utc) DESC` — a full-table scan, unindexed on every column it touches — and it runs after **every** commit, skip and undo via `Completer.Names` → `RefreshCompleterAsync`. Off-thread, but awaited before the next document loads. The cost grows for the life of a table that is never pruned.

> Fixed: `79b9289` adds a single partial covering index — `ix_history_ranked_names ON history(name_entered, ts_utc, id) WHERE reverted = 0 AND name_entered != ''` — created via `CREATE INDEX IF NOT EXISTS` in `History.Migrate()`, matching `RankedNames()`'s `WHERE` clause exactly. Measured on a 100k-row fixture (2,000 distinct names, ~5% reverted, ~3% blank `name_entered`): `EXPLAIN QUERY PLAN` went from `SCAN history` plus two TEMP B-TREEs to a single index scan, and `RankedNames()` dropped from ~73ms to ~17ms best-of-5 (4.3×). Write cost is unchanged in practice — `LogCommit` touches the index on every insert, measured 8.494ms/call before vs 8.536ms/call after over 1,000 real calls, inside the noise floor already imposed by `synchronous=FULL`'s per-call fsync. `Count()` and `Rows()` are untouched by design. One-time migration cost on an existing 100k-row database, measured end-to-end as `new History(path)` (the call `ShellViewModel`'s synchronous constructor makes before first paint): 157.5–163.7ms — not perceptible as a hang. Confirmed live against a genuinely pre-existing, pre-migration `demo-full` database (Task 2's gate): the app opened it, the migration ran, and `File > View history` read back real rows, all within a startup that showed no visible delay.
>
> **A durable lesson worth keeping:** a *plain* (non-partial) index on the same three columns was tried first and rejected. It produced an almost identical-looking `EXPLAIN QUERY PLAN` to the partial index — but was *6× worse than no index at all* (~437ms vs ~73ms unindexed), because it doesn't cover the `reverted` column: SQLite still had to pay a table lookup per index row. Only a partial index whose `WHERE` provably matches the query's `WHERE` lets SQLite scan the index alone as a full covering index. Plan text alone does not prove an index is safe to ship — measure the query, not just the plan.

**5.2 — Bulk Rename does synchronous file I/O per keystroke on the UI thread. [A] Important — FIXED `79c3997` (fix round `5fe0113`)**

Every Find/Replace/Prefix/Suffix setter calls `Refresh()` with no debounce, and `BulkRename.Free()` (`:159-161`) does a `File.Exists()` per loaded file. Every other live-validation field in the app already uses `DebouncedProbe` for exactly this; Bulk Rename is the one that was never wired to it — in the tool built for batches, over SMB.

> Fixed (`79c3997`, fix round `5fe0113`): `Find`/`Replace`/`Prefix`/`Suffix` now route through a `DebouncedProbe<List<PlannedRename>>` (300ms default); `Refresh()` was split into an off-thread compute (`Plan`) and a UI-thread apply (`ApplyPlans`, which mutates the `DataGrid`-bound `Preview` collection — mutating it off-thread is a crash, not a test failure). Discrete, non-keystroke input — `ReviewMode`, `ReceivedDate`, `CaseIndex`, `DeleteSeg1`–`DeleteSegLast`, `AddFiles`/`RemoveFiles`/`SetOverride`/`Clear`, and `Apply()`/`UndoBatch()`'s own trailing refresh — resolves immediately, matching the classification already established for `RouteEditVm`/`WatchEditVm`. `BulkRenameViewModel` is now `IDisposable`; its one production call site (`MainWindow.OnBulkRename`) disposes it when the dialog closes. A fix round caught two false-greens before they could be trusted: the promptness tests injected latency at the scheduler rather than the compute seam, so a regression to the literal pre-fix synchronous call would have passed them anyway; and a `WaitFor` polling pattern that could observe leftover state from a *prior* compute rather than the one it claimed to pin (three instances found and fixed, one self-found by the implementer on re-audit). Verified hands-on (Task 3 gate, real UI Automation driving a live launch, not just the unit suite): typed characters into `Find` landed as fast property-set calls (2–36ms each, no UI-thread stall); a `scan001...` row's `New name` stayed unchanged 30ms after the last keystroke and correctly showed `scan` stripped once the debounce window passed (~500ms); toggling a delete-segment checkbox updated a hyphenated test row's preview within 60ms — confirming the discrete/debounced classification is not just correct in tests but felt right in the app.

**5.3 — First paint blocks on a full DB copy, once a day. [V] Important — MEASURED, DELIBERATELY UNCHANGED (`3da2a22`)**

`ShellViewModel`'s constructor (`:63-66`) runs `HistoryBackup.BackupDaily` — a whole-file `File.Copy` of the history DB — then opens SQLite synchronously, before `MainWindow`'s constructor returns and before `Show()`. The history *swap* path at `:1118-1124` does the identical work correctly inside `_scheduler.Run(...)`. The codebase already knows the right pattern; the constructor predates it.

> Measured, not guessed: a 100k-row `history.sqlite` (15.27 MB, seeded via a raw-SQL bulk insert in one transaction so the measurement timed the copy, not `LogCommit`'s own fsync cost) gave, over 5 runs on a local NVMe SSD — `HistoryBackup.BackupDaily`'s `File.Copy`: **6.5–8.1ms**; `new History(dbPath)` open+migrate with an already-current schema: **0.9–1.0ms**; the whole `ShellViewModel` constructor: **7.6–9.8ms**. The three numbers corroborate each other (copy + open ≈ measured whole-constructor range) and match the independent estimate already recorded in `History.cs`'s own 5.1 comment ("6.0ms to 8.1ms locally"). **Decision: do nothing but record the measurement** — tens of milliseconds locally is not a perceptible hang, and both alternatives cost real complexity for a locally-unmeasurable gain: moving backup-then-open into `_scheduler.Run` trades ~8ms of real work for a visible "starting" state and a new state machine between constructing and usable; deferring the backup to after the connection opens would copy a **live** database, turning finding 1.3's unproven tearing risk into a proven one. The gap this does *not* close, stated rather than assumed: the app's documented deployment is an SMB share, and a 15+MB whole-file copy over SMB is not the same proposition as a local SSD — no share was available to measure against, so the constructor now carries a comment with these numbers, the reasoning, and the SMB caveat, so the next person measuring on a share doesn't have to rediscover this task's own scope.
>
> The measurement did surface a **real, independent bug**, fixed regardless of the "leave it alone" decision above: `HistoryBackup.BackupDaily` returns `null` for two indistinguishable reasons — nothing to back up yet (first run) vs. a genuine failure (bad permissions, full disk, disconnected share, caught and swallowed internally) — and both `ShellViewModel` call sites (the constructor and the settings history-db swap) discarded the return value entirely, so a backup that failed every day failed *silently* forever. `RunHistoryBackup` now disambiguates the same way `BackupDaily` does internally (`File.Exists` first), and a genuine failure sets `HasHistoryBackupWarning`, surfaced as a full-width, click-to-open-the-backups-folder banner reusing the existing `DeferredAlert` pattern — deliberately a passive banner, not a modal, both because a modal at startup would either block the first paint this finding is about or require new plumbing to defer past `Show()`, and because a persistent problem would then reappear as a modal on *every* launch, training the user to reflexively dismiss it. Proven via three tests, including a revert-and-reconfirm: the fresh-install (no db yet) case correctly shows no warning, while a genuine blocked-backups-directory failure at both call sites surfaces the warning and shows no dialog.

**5.4 — Settings tile preview probes the filesystem per keystroke. [A] Important — FIXED `5b8cbca`**

`SettingsViewModel.cs:742-751,1283` runs `FolderMonitor.Status` (a `Directory.Exists` plus a possibly-recursive enumeration) synchronously on the UI thread for each keystroke in the selected watch row — the un-fixed sibling of the `DebouncedProbe` work already done elsewhere.

> Fixed (`5b8cbca`): the status-derived tile-preview properties (count, note, hint, alerting colours) now go through a `DebouncedProbe<FolderStatus>` (300ms); the cheap ones (`Visible`, `Label`) stay instant, matching `RouteEditVm`/`WatchEditVm`'s existing split. **Second fix, folded into the same commit but not named by this finding:** `HookWatch` previously fired `RecomputeTilePreview` on *every* `PropertyChanged` of *every* `WatchEditVm` row, regardless of whether that row was selected — `RecomputeTilePreview` only ever renders `SelectedWatch`, so a keystroke on an unselected row ran a full (possibly recursive) `Directory.EnumerateFiles` and threw the result away. `HookWatch` now early-returns for rows that are not the selected one; the `Section`-choices branch stays outside that guard so it still fires for every row (section membership is shared UI state, not per-row status). Verified hands-on (Task 3 gate, real UI Automation driving a live launch): typing into a selected folder's `Label` updated the tile's preview label on every keystroke (2–16ms per call, no stall); typing into its `Folder` path left the count/note blank immediately after the last keystroke (no stale old value shown) and landed the new folder's real count correctly ~500ms later; selecting a different watched folder switched the whole preview (label + count) within 80ms — well inside "promptly."

---

## Theme 6 — Test quality

**The suite is genuinely strong, and this matters for reading everything above.** The auditor went hunting specifically for tests that would still pass with their production code reverted and **found none** in either project. Core does real filesystem round trips, real concurrency (parallel writes, SQLite busy-timeout), and picked `th-TH` over `ja-JP` for culture tests because the authors understood why the latter would not catch the bug. The historical `ThemeTests` false-assurance defect is visibly fixed: it now tests only pure palette math, with live assertions moved to files that read *resolved* control properties.

Findings are consequently thin, and that is a real result rather than an absence of effort:

- **[A] Minor** — `QcTests.MatchMergeControlIdWithColonIsSafeToo` has a dead `if (outcomes.Count > 0)` branch; tracing `MergeOne`→`Plan`→`Execute` proves `outcomes` is always empty, so that assertion never runs.
- **[A] Minor** — `WatchListRowTemplateTests.OpenDashboard` can leak a shown `SettingsWindow` into the shared process-wide `Application` if it throws before returning (no `try/finally`), poisoning every later test in that collection.
- **Untested surface, ranked by user risk:** (1) `Commit.UndoAction`'s three failure branches **[V]**; (2) `Config.ValidateRoute`/`ProbeWritable`'s "not a folder" and "not writable" branches; (3) `LabelPrinting.BuildDocument` — the code that builds the physical printed sheet has no tests, unlike the Core math it wraps.

---

## Theme 7 — Dependencies and hygiene

| Package | Pinned | Latest | Licence | Verdict |
|---|---|---|---|---|
| Microsoft.Data.Sqlite | 8.0.11 | 10.0.10 | MIT | **CVE-2025-6965** via transitive `SQLitePCLRaw` — see below |
| Microsoft.Web.WebView2 | 1.0.2903.40 | 1.0.4129.50 | Proprietary MS (redistribution OK) | Aging; runtime patches independently |
| PdfSharp | 6.1.1 | 6.2.4 | MIT | Clean; API used is stable |
| System.Security.Cryptography.ProtectedData | 8.0.0 | 10.0.10 | MIT | Clean |
| xunit / runner / Test.Sdk / coverlet | 2.5.3 / 2.5.3 / 17.8.0 / 6.0.0 | — | Apache-2.0 / MIT | Test-only, no advisories, aging |

**7.1 — SQLite CVE, with an unexpected reachability path. [V] Important**

`SQLitePCLRaw` resolves to exactly **2.1.6** — the unpinned floor — confirmed from `project.assets.json`. That carries the vulnerable bundled native SQLite.

Two audits each held half of this and neither could see it whole: the history DB is *designed* to live on an SMB share, and the config audit independently established that share is writable by other stations. A SQLite-level memory-corruption bug plus "another user can write your database file" is a narrow but real path from hostile-share to code execution. Neither dimension is alarming alone.

**7.2 — .NET 8 reaches end of life 2026-11-10 [A]** — about three months out, for a v1.0 shipping now on `net8.0`.

**7.3 — `.claude/worktrees/` is 798 MB of stale scratch, untracked but not ignored. [V] Minor**

`.gitignore` has no `.claude` entry. All six worktree HEADs are ancestors of `main` and clean — nothing unique is in them, safe to delete. One `git add -A` from ignoring them would be unpleasant.

**7.4 — Documentation is accurate. [A]** No false or stale claims were found in `README.md` or elsewhere; every spot-checked claim verified exact against code. Zero `TODO`/`HACK`/`FIXME` in `src`, `tests` or `tools`; no commented-out code; no committed `bin`/`obj`; no version drift across the five csproj files; `demo-full/` is correctly ignored and contains only synthetic names.

---

## What to fix, in order

Ranked by (harm × likelihood) ÷ effort, not by severity label.

**Update, 2026-08-04 (Task 6 gate):** items 1–4 and 6 below are done — all five v1.0.0-blocking findings (1.1, 2.1, 2.2, 2.3, 3.1) are fixed, reviewed, and gated (Release build, full suites at Core 375 / Wpf 520, `demo-full` smoke, and — for 3.1 specifically — a real launch against a real unwritable folder, not just the unit test). See each finding above for its commit SHA(s). Item 4's actual fix diverged from this list's own recommendation below — see the note under 2.1 for why the recommendation was wrong.

**Update, 2026-08-06 (Task 5 gate):** four more findings are now fixed, reviewed, and gated — **4.2** and **4.3** (Theme 4, security) and **1.2** and **1.3** (Theme 1, the audit-log/move guarantees) — added below as items 10–13. This list did not originally include any of the four; that omission is what this update corrects. Every Important-or-above finding this document raises is now fixed except **1.4**, which stays deliberately unfixed (recorded as a code comment, not a defect — see its note) and the handful of Theme 2/3/5/6/7 items this top-priority list never carried in the first place (2.4–2.6, 3.2–3.5 beyond item 14, 5.3, Theme 6, Theme 7). Gate: Release build, full suites at Core 414 / Wpf 558, `demo-full` smoke, and — for 1.2, 1.3 and 4.3 specifically — a real live launch against `demo-full`, driving the actual Unlock window end to end against a real encrypted PDF and `demo-full`'s own real plaintext saved passwords, not just the unit suites. See each finding above for its commit SHA(s) and live-verification detail.

**Update, 2026-08-07 (Task 4 gate):** two more findings are now fixed, reviewed, and gated — **2.4** (LabelMaker's per-row dirty tracking, three rounds: `8c20909`, `701e7d3`, `7ca1779`) and **2.6** (the unsynchronized History connection and `Session.Current`'s double-read, two rounds: `ba34dc2`, `62f1a99`) — added below as items 15–16. **5.3** was measured, not fixed: a 100k-row database showed a 6.5–8.1ms backup copy and a 7.6–9.8ms whole constructor on a local SSD — not a perceptible hang — so the deliberate decision (`3da2a22`) was to leave the timing alone and record the measurement and its SMB caveat in a code comment; the same commit did fix a genuinely separate bug the measurement surfaced, the backup's silent failure, now a persistent session banner (see 5.3's note). Every Important-or-above finding this document raises is now fixed or explicitly, evidence-backed decided, except **1.4** (deliberately left, a code comment not a defect) and Theme 3's release-hygiene items (3.2–3.5, item 14) and the Theme 6/7 notes this list never carried as numbered items. Gate: Release build, full suites at Core 425 / Wpf 578, `demo-full` smoke, and a real live launch against `demo-full` — UI Automation driving a live Label maker (a client's next-number displayed correctly, and switching clients showed each one's own number, not a stale cross-client value) and View history (real rows read back) end to end. One live sub-check fell back to its automated test instead of a click-through: the Data files Browse… confinement warning, because this session's console had neither screen-capture nor input-injection access to drive the native OS file picker (`CopyFromScreen` returned solid black; `SendKeys.SendWait` threw Access Denied — both probed directly, not assumed) — covered instead by `SettingsViewModelTests.BrowsingToAnAbsolutePathOutsideTheConfigDirectoryIsRejectedAndKeepsTheCurrentValue`, part of the passing 578.

1. ~~**Atomic writes everywhere** (2.3) — temp file + `File.Replace` in `Config.SaveMain`/`WriteDoc` and `BoxLabelStore.Mutate`. One pattern, three call sites, and it is the root cause of 2.2 and a compounding factor in 3.1.~~ **DONE — `ceada04`, review round `d52208c`.** (`BoxLabelStore.Mutate` ended up using a different mechanism than "the same pattern" implied here; see 2.2's note.)
2. ~~**Guard the first-run config save** (3.1) — and make the crash handler `Shutdown()` when no window exists. Small, and it is the first thing a new user can hit.~~ **DONE — `ea49754`.**
3. ~~**Hold close until the in-flight commit completes** (1.1) — checking the busy guard on the `_reallyExit` path closes the disposed-connection race for free.~~ **DONE — `e15bdb4`, bound by `3373560`.**
4. ~~**Call `RefreshSharedSectionsFromDisk` from Settings-OK** (2.1) — the fix already exists; wire it to the second path.~~ **DONE, but not as recommended here — `a7d8407`, refined `86be636`.** This recommendation was wrong: `RefreshSharedSectionsFromDisk` overwrites the shared sections with disk content, which is correct for the tool-window caller it already had and would have discarded the user's own Settings edits on every save. The shipped fix is conflict detection by content hash instead — see the note under 2.1.
5. ~~**Harden the WebView2 viewer** (4.1) — ~20 lines in one `InitAsync`, no workflow change.~~ **DONE — `f821dfe`.** Landed close to the original scope, plus one empirical correction: the plan's own worry that script might have to stay on for rendering to survive turned out false — `IsScriptEnabled = false` was measured, not assumed, and kept. See 4.1's note.
6. ~~**Make `Mutate` and `Read` agree about a 0-byte file** (2.2) — falls out of 1, but assert it directly too.~~ **DONE — `fe8b110`.** Landed as its own fix, not as a byproduct of item 1: item 1's temp-file pattern doesn't apply to `Mutate` (see 2.2's note).
7. ~~**Index the history table** (5.1) — `name_entered`, `reverted`, `ts_utc`.~~ **DONE — `79b9289`.** Shipped as a single partial covering index rather than a plain one on the same columns — see 5.1's note for why the plain version measured 6× worse than no index at all.
8. ~~**Test `UndoAction`'s three failure branches** (1.5) — the safety net deserves a test.~~ **DONE — `902adbb`.** Added as a real defect fix: `FileExistsRace` was escaping the assembly unhandled. The three guards already worked correctly; the gap was a race in one guard's own window that produced the wrong error message. See 1.5's note.
9. ~~**Debounce Bulk Rename and the Settings tile preview** (5.2, 5.4) — the pattern is already proven in this codebase.~~ **DONE — `5b8cbca`** (5.4, plus the unselected-row probe folded in) **and `79c3997`, fix round `5fe0113`** (5.2).
10. ~~**Confine the four config side files to the config's own directory on write** (4.2) — `destinations_file`/`monitored_folders_file`/`alerts_file`/`box_labels_file` must refuse to resolve outside where `config.json` lives.~~ **DONE — `6337a74`, fix round `37e887d`.** Split read from write: write refuses any path resolving outside the config directory; read still loads an already-configured absolute path (the Settings "Data files" Browse… buttons produce exactly that, a real shipped capability) but refuses anything else that would escape. `history_db` deliberately left unconfined and documented at both `ResolvePath` call sites — it's designed to live on its own SMB share, unlike the four side files. See 4.2's note.
11. ~~**Never let `Unlock` overwrite or delete a file it didn't create** (1.2) — `PlaceAndSwap`'s buffered write and its `RemoveQuietly` cleanup.~~ **DONE — `e364988`.** The buffered path's write is now `FileMode.CreateNew` (fails atomically on a taken name instead of truncating); the streamed path was already create-only. All four `RemoveQuietly(target)` sites are gated on a new `markCreated` callback. See 1.2's note.
12. ~~**Close Unlock's in-place-swap crash window from resurfacing as a document** (1.3) — the intermediate name must not be scanner-eligible.~~ **DONE — `93b3528`.** Renamed the intermediate from `.unlocking.pdf` to `.unlocking.tmp`, closing the half of the crash window that's fixable — the old name was confirmed pickable by `Scanner.Eligible` in three of four naming modes and by extension-based watch-folder filters. See 1.3's note.
13. ~~**Protect legacy plaintext saved passwords on load, not just on save** (4.3) — `ReprotectLegacyPlaintext` must run every time Unlock opens.~~ **DONE — `64e556c`, fix rounds `0ad4766`, `57efd95`.** `UnlockViewModel`'s constructor now sweeps on load; a DPAPI (or any) failure rolls back and warns instead of bricking the tool; the passive save was narrowed to overlay only `SavedPasswords` onto a fresh on-disk read so a peer's concurrent edit survives. Verified live at Task 5's gate against `demo-full`'s own real plaintext saved passwords — see 4.3's note.
14. **Release hygiene before `v1.0.0`** (3.2–3.5) — sign, stamp a version, add an About box, add LICENSE + third-party notices, document the WebView2 prerequisite.
15. ~~**Stop LabelMaker's per-row dirty tracking from rolling back a peer's counter** (2.4) — merge on `NextNumber` only when the number field itself was edited, not the whole row.~~ **DONE — `8c20909`, fix round `701e7d3`, fix round `7ca1779`.** Shipped narrower than the header implied: not a full per-field dirty-tracking rewrite, one added set (`_numberEdited`) scoped to the one field with a peer-concurrency story. Two follow-up rounds closed the same hazard reached through a rename and, further, a round-trip rename — see 2.4's note.
16. ~~**Synchronize the History connection and fix `Session.Current`'s double read** (2.6) — one lock around every `_conn` access; read `Pos` once into a local.~~ **DONE — `ba34dc2`, fix round `62f1a99`.** `ExportCsv` deliberately keeps its file write outside the lock so a slow export can't stall a concurrent commit. The follow-up round found and fixed the identical hazard one property over: `Current` also read `Queue` twice — see 2.6's note.

Items 1–4 were what I would not ship `v1.0.0` without — done as of the 2026-08-04 update. Items 10–13 close out every other Important-or-above finding this document raised, except 1.4 (deliberately left — see its note). Items 15–16 close out 2.4 and 2.6, the two Theme 2 findings this list originally didn't carry. **5.3** is deliberately not a numbered item here: the 2026-08-07 gate measured it and chose to leave the timing unchanged (see 5.3's note and the update above) rather than ship a fix, so there is no diff to point at the way every other item in this list has one. Item 14 (release hygiene) is the only item now standing between this codebase and a `v1.0.0` tag.

## Open questions needing dynamic proof

Not defects — hypotheses this static pass could not settle:

- Whether an interrupted cross-volume `File.Move` leaves an orphaned partial at the destination (1.4). Needs a disk-full or kill test across volumes. **Still open** — deliberately left, recorded as a code comment at `Commit.cs:19-27`.
- ~~Whether `Unlock`'s `CollisionFree` → `WriteAllBytes` gap is actually winnable (1.2). Needs two processes against one shared folder.~~ **Moot as of `e364988`**: the gap itself (a check-then-act race between `CollisionFree` and the write) still exists — two processes can still race for the same name — but `WriteAllBytes` is gone. The write is now `FileMode.CreateNew`, so winning the race now means the *loser* fails loudly with nothing overwritten or deleted, not that a peer's file gets silently truncated. Whether the race is winnable is no longer a question worth a two-process test, because the answer stopped mattering to data safety either way.
- Whether `HistoryBackup`'s raw `File.Copy` of a live SQLite file produces a torn backup in practice, and whether the 14-day prune can rotate away every good one.
- Whether `BoxLabelStore`'s HResult retry-or-fail-fast list matches real SMB error codes, and how it behaves against a stale lock from a crashed station.
