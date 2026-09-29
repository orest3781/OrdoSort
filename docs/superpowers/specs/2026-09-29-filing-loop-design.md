# The filing loop — design

Owner request, 2026-09-29: "the perfect filing loop that allows the user to quickly file pdfs". Follows the 2026-09-29 finding that 1.8.0's per-document page-size read pulled every file over the network before the preview opened (fixed on `fix/preview-speed`: the size is read once per session).

## Today

One key press (a route hotkey, Enter, or Skip) runs these steps one after another, all before the next page appears. Every one of them waits on a disk or the network.

| # | Step | Code | Waits on | Typical cost on a share |
|---|---|---|---|---|
| 1 | Release the preview: navigate Edge to `about:blank`, wait for its completion event | `WebViewPdfViewer.ReleaseAsync` | Edge; capped at 2 s | 30-200 ms, up to 2 s |
| 2 | Move the document | `Session.CommitCurrent` via `RunFiling` | SMB: a rename, or a copy + delete across shares | 20 ms to seconds |
| 3 | Re-scan the deferred folder | `RefreshDeferredAsync` | a directory listing, often on the share | 10-300 ms |
| 4 | Rebuild the name suggestions | `RefreshCompleterAsync` | SQLite query + seed file read | 5-50 ms |
| 5 | Open the next document in Edge, straight off the share | `LoadCurrentAsync` → `ShowAsync` | the whole file over the network, then Edge's render | 100 ms to seconds |
| 6 | Focus the name box | `RequestNameFocus` | — | — |

Step 1 exists because Edge holds the shown file open, and a move against an open file fails. Steps 3 and 4 are bookkeeping that the user never waits for on purpose; they are simply in the way. Step 5 is the big one: the file crosses the network at the moment the user is waiting for it, and the network is idle the whole time the user reads the previous page.

The user's own rhythm is: read the page (seconds), type a name (a second or two), press a key. The loop should spend that reading time getting the next page ready, so the key press costs nothing.

## Goal

| Measure | Today (share) | Target |
|---|---|---|
| Key press → next page on screen, name box focused | 0.3-3 s, varies with the network | Under 100 ms, and the same whether the inbox is local or on a share |
| What the user waits on | Edge's release, the move, two refreshes, the network read | Nothing but drawing the page |
| Filing safety | One document at a time, every error shown before the next page | Unchanged: every move is logged, every failure is reported, Undo still works |

## Principle

**The only thing on the critical path is putting the next page on screen.** Moving, logging, suggestions and the deferred scan happen after it or beside it. The network is used while the user reads, never while the user waits.

## Design

### 1. Stage the documents locally (read-ahead)

The preview never shows the inbox file. A staging folder on the local disk holds a copy of the current document and the next few, copied in queue order in the background. Edge is pointed at the local copy.

| Question | Design |
|---|---|
| Where | `%LOCALAPPDATA%\OrdoSort\stage\<session id>\` — per session, so two OrdoSorts on one PC never share it |
| How far ahead | The current document plus the next 2. Enough to cover a fast user; small enough to bound disk use |
| When the copy starts | The moment a document enters the "next 2" window: at session start (documents 1-3 at once), then one more each time the user advances |
| Copy key | Inbox path + size + last-write time. A file that changed under us is copied again; a staged copy that no longer matches is never shown |
| The first document | It is copied first and shown as soon as its copy lands; if the copy has not landed within 300 ms, Edge is pointed at the share file instead (today's path) so the first page never waits for staging |
| A document whose copy is not ready when it becomes current | Same fallback: show it off the share, as today, and let staging carry on for the ones behind it |
| A copy that fails | Fallback to the share, silently; staging never produces an error the user sees. The reason goes to `crash.log` once per session |
| Cleanup | A staged copy is deleted as soon as its document is filed or skipped and no longer undoable (the last filed document's copy is kept so Undo can re-show it without another copy). The session folder is deleted at session end; orphaned session folders from a crash are swept at start-up |
| Size cap | Files over 200 MB are not staged (shown off the share); the stage folder is capped at 1 GB, oldest first |
| Privacy | Copies of documents sit on the local disk for the length of a session. This is the same exposure as Edge's own cache before InPrivate (QC-22) but bounded and cleaned. The folder is under the user's own profile. Owner decision below |

What this buys, on its own:

- Edge reads from the local SSD, not the share. The read happens while the user is looking at the previous page.
- Edge holds the *copy* open, not the inbox file, so **step 1 (release) goes away**: the move never has to wait for Edge.
- The move (step 2) still runs, but now nothing before it waits on Edge and nothing after it waits on the network.

### 2. Show the next page before the move finishes

On a route key the shell captures what it needs (the document's path, the typed name, the route), advances the screen to the next staged document, focuses the name box, and *then* files the previous one in the background. The user is reading page N+1 while page N moves.

| Question | Design |
|---|---|
| Ordering | One background filing queue, first in first out; moves never run in parallel (two moves on one share are slower than one after another, and the history log stays in order) |
| How many can be in flight | Up to 3 documents may be "shown and not yet moved". At the 4th, the key press waits for the oldest move — a fast user on a dead share is slowed down, never allowed to run away |
| The ✓ chip | Shows "Filing…" the moment the key is pressed, becomes "✓ Filed to X" when the move lands; the last three outcomes are visible, newest on top, so a failure two documents back is still seen |
| A failed move (`CommitError`) | The document comes back: the queue re-inserts it *in front of the document now on screen*, the shell shows it again with the same dialog as today, and the typed name is restored. The document the user had moved on to is simply next again |
| Filed but not logged (`AuditError`) | Same as today (reported, not re-shown), just delivered when the move lands rather than before the next page |
| Undo | Undo waits for that document's move to finish, if it is still in flight, then reverses it as today. The staged copy is still there, so re-showing it costs nothing |
| Stop / Esc / X mid-session | Waits for in-flight moves (each is at most one document), as the QC-19 fix already does for the single move today |
| Vanished file | Reported when the move finds it gone, as today; nothing to re-show |
| Name suggestions | The just-used name is added to the in-memory list at once, so it is suggestable on the very next document; the full rebuild (SQLite + seed file) runs in the background afterwards |
| Session state | `Session` gains a per-document state (`Pending`, `Shown`, `Filing`, `Filed`, `Skipped`, `Failed`) and a `Peek(n)` for the read-ahead; `Current` stays what is on screen |

### 3. Everything else off the path

| Work | Today | After |
|---|---|---|
| Deferred-folder scan | Before the next page, every document | After the page is shown, coalesced: one scan in flight at a time; a request while one runs marks "again" and runs once more when it finishes |
| Name-suggestion rebuild | Before the next page, every document | In-memory add now (see above); the full rebuild after the page, coalesced the same way |
| Page fit | Once per session (`fix/preview-speed`) | Unchanged |
| Focus | After `ShowAsync` returns | Immediately on advance; the page draws under an already-focused box |

### 4. Measure it

A `timing.log` beside `config.json`, on when `"timing": true` is in the config, one line per document: `key→shown ms`, `stage wait ms` (0 when the copy was ready), `move ms`, `stage hit/miss`. This is how the targets above are checked on the real share, and how a future "it feels slow again" report is answered in minutes rather than a day. Off by default; no document names in it (QC-21).

## What the user sees

| Moment | Today | After |
|---|---|---|
| Session start | First page after the window fits and Edge reads the file | Same; staging starts for documents 2 and 3 in the background |
| Press a route key | Page goes blank; a pause; next page draws; focus lands | Next page draws at once, focus is already there; "Filing…" under the routes turns to "✓ Filed" a moment later |
| A move fails | Dialog before anything else | The page the user just left comes back with the dialog; their typed name is back in the box |
| Undo | Reverses the last move | Same, at most one move's wait if that move is still running |
| Session end | Instant | Instant; the stage folder is deleted |

## Phases

Each phase ships on its own, is measured against the previous one, and is useful without the next.

| Phase | Contains | Expected gain on a share | Risk |
|---|---|---|---|
| 1 | Timing log (§4); deferred scan and completer rebuild off the path (§3) | Steps 3-4 gone from the key press: 15-350 ms | Low: nothing about filing changes |
| 2 | Staging (§1): read-ahead copies, Edge shows the local copy, no release wait | Steps 1 and 5 gone: the network leaves the key press entirely | Medium: a cache that must never show a stale file, and cleanup that must never leave copies behind |
| 3 | Show-then-move (§2) | Step 2 gone: the key press costs only the draw | Higher: error and undo paths change; needs the per-document session state |

- [ ] Phase 1: `feature/filing-loop-1` off `fix/preview-speed`
- [ ] Measure on the real share; record in STATUS
- [ ] Phase 2, only if the timing log says the load is where the time goes (it will be)
- [ ] Phase 3, only if the timing log says the move is what is left — a fast local rename may make it not worth it

## Owner decisions

| Question | Options | Owner's answer |
|---|---|---|
| Local copies of documents during a session (§1 privacy) | Accept (bounded, per session, cleaned) / never stage, only take steps 1-4 off the path | |
| Read-ahead depth | 2 (design) / 1 / 5 | |
| Show-then-move at all (phase 3) | Yes / no, keep one document at a time | |
| In-flight limit if yes | 3 (design) / 1 | |
| Where a failed move re-appears | In front of the current document (design) / at the end of the queue | |
| Timing log | Config switch (design) / always on / never | |

## Tests

| Phase | Test | Proves |
|---|---|---|
| 1 | Routing a document does not await the deferred scan or the completer rebuild before `ShowAsync` | steps 3-4 are off the path |
| 1 | Two quick routes coalesce into one deferred scan | no scan storm |
| 1 | The just-used name is suggestable on the next document before the rebuild completes | in-memory add |
| 2 | With a slow copy stub, the first page shows off the share within 300 ms | first-page fallback |
| 2 | A staged copy whose inbox file changed (size or mtime) is not shown; the file is re-copied | never a stale page |
| 2 | Filing a document deletes its staged copy; Undo re-shows the kept copy without a new copy | cleanup and undo |
| 2 | Session end deletes the session folder; start-up sweeps an orphaned one | nothing left behind |
| 2 | The move never waits on `ReleaseAsync` when the shown file is a staged copy | release gone |
| 3 | A route key returns before the move; `ShowAsync` for the next document precedes `CommitCurrent` in the call order | show-then-move |
| 3 | A `CommitError` re-inserts the document in front of the current one and restores the typed name | failure path |
| 3 | Undo during an in-flight move waits for it, then reverses it | undo |
| 3 | The 4th route key with 3 in flight waits for the oldest | bounded |
| 3 | Stop with a move in flight waits for it and logs it (QC-19) | no lost history row |
| all | `timing.log` has one line per document, no document names | measurement, QC-21 |