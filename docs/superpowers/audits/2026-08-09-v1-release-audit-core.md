# v1.0 release audit — OrdoSort.Core — correctness & data safety

Repo: `S:\ordosort-session`, branch `session/header-pickers`, HEAD `6c11ead`.
Scope: `src/OrdoSort.Core` — Commit, Unlock, Session, History, BulkRename,
MatchMerge, Naming, Config, Scanner, BoxLabelStore, Collision, Intake,
NaturalSort, Zipper, ZipMerge. Read-only; nothing in the worktree was changed.

Central promise under test: "Nothing was deleted — OrdoSort only ever moves
files, so the document is either where it started or where it was going."

## Counts

- Critical: 0
- Important: 3
- Minor: 1

## Important findings

1. **Commit.cs:92** — `SkipFile` calls `MoveNeverOverwrite` with no
   `catch (FileExistsRace)`, unlike `CommitFile` (line 67, retries once then
   converts to `CommitError`) and `UndoAction` (line 123, converts directly).
   `FileExistsRace` is a `private sealed class` nested in `Commit`
   (line 133) — callers outside `Commit` cannot even name it in a catch
   clause. Trigger: two stations on the shared inbox/deferred SMB folder
   press Skip on files that resolve to the identical deferred-folder target
   name at nearly the same instant (the exact class of race Commit.cs's own
   doc comment says this file exists to guard against). The loser gets an
   unhandled Exception instead of a CommitError. Verified end-to-end:
   ShellViewModel.OnSkipAsync (ShellViewModel.cs:1293-1302) only catches
   AuditError and CommitError, so this type sails past both and becomes an
   unhandled exception on the UI thread. No document is lost — the guard
   fires before any move, so the file stays put in the inbox — but this
   directly contradicts the file's own class-doc promise, "Every failure
   raises CommitError with a message fit for a dialog box" (Commit.cs:5-7):
   a comment that contradicts its own code, in the file most central to the
   never-lose-track-of-a-document guarantee.

2. **Zipper.cs:98** — `CreateZipCore`'s Save-As path (`outputPath is not
   null`) does `File.Delete(outputPath)` unconditionally whenever a file
   currently sits at that path, then creates the zip fresh. The comment's
   justification ("the dialog already confirmed overwrite intent") is true
   of what the user saw when the dialog closed, not of whatever is actually
   at that path when this line executes. On the concurrent-station SMB
   deployment this app targets, another station can write a new file to
   that exact path in the gap between dialog-close and this call — that
   file is deleted outright, with no markCreated-style gate and no
   move-based recovery, unlike every other destructive write in this
   codebase (Unlock.PlaceAndSwap, ZipMerge.MergeZipCore, Zipper's own
   Extract), which all gate cleanup behind "did this call actually create
   the thing." This is the one write in the newer zip/unzip code that does
   not follow the file's own stated discipline. The window is narrow
   (milliseconds, and only when a user explicitly Save-As's to a path that
   collides with a file someone else is concurrently writing), so this
   sits below Critical, but it's the one place in the newer code that
   doesn't hold itself to the bar the rest of the file sets.

3. **Commit.cs:19-27** (MoveNeverOverwrite doc comment) — an
   already-flagged, not-fixed gap: across volumes, File.Move is effectively
   copy-then-delete; a kill/power-loss/disk-full mid-copy can leave a
   partial file at the destination. On retry, Build()'s collision counter
   sees that name as taken and routes the real document to a " (2)"
   suffix, leaving the partial file holding the canonical name with
   nothing downstream aware it's incomplete. The code's own authors rate
   this [U] — unproven, not reproduced, needs a real disk-full/kill test
   across two volumes to confirm whether Win32 MoveFileEx cleans up its own
   partial. I did not attempt to reproduce it either. Worth carrying into
   release notes as a known, acknowledged gap rather than a silent one;
   likelihood is reduced in practice because inbox/deferred/destination
   folders are typically on the same SMB volume for a given station.

## Minor

- **Unlock.cs** suffix-mode path (`suffix` non-empty, i.e. "write a copy,
  never touch the original") writes its final .pdf output directly via
  FileMode.CreateNew with no temp+swap. A crash mid-fs.Write there would
  leave a corrupt .pdf sitting under its real, final name, which (unlike
  the swap-in-place .tmp path) would match Scanner.Eligible and could
  surface as a spurious queue entry. The doc comment states this path is
  "No longer reachable from the app" (Unlock.cs:13-14) — I did not find a
  call site outside tests, so this is dead-code-adjacent and I am not
  counting it toward the severity totals, but it's worth deleting or fixing
  before v1 rather than leaving unreachable-but-wrong code in an assembly
  whose whole job is data safety.

## Verified sound

- **Commit.CommitFile / Naming.BuildTarget / the core commit path**: every
  destination write goes through File.Move's default non-overwrite
  behavior plus an explicit pre-check, and the one race window it can't
  close (check, then act) is retried once and then fails soft to
  CommitError with the source untouched. This holds.
- **Unlock.PlaceAndSwap, Zipper.CreateZip's default (non-Save-As) path,
  Zipper.Extract, ZipMerge.MergeZipCore**: all four follow the same
  markCreated/created-flag discipline correctly — cleanup on failure is
  gated on "did this call actually create the thing," verified by reading
  each gate's set/check pairing, not just trusting the comment. Extract's
  directory case is properly narrowed (existence checked immediately
  before CreateDirectory, not assumed) so it won't Directory.Delete
  (recursive: true) a folder it didn't create.
- **ZipMerge's ZipSlip immunity**: verified structurally, not just by
  comment — entry names are only ever used as a ZipArchiveEntry.Open()
  source or as error-message text, never passed to a File/Path API, so
  there is no code path for a crafted entry name to escape the read side.
  Zipper.Extract relies on ZipFile.ExtractToDirectory's own traversal
  guard (a framework guarantee, not this code's own), which is a
  reasonable but real external dependency — flagged honestly, not as a
  defect.
- **History.cs / SQLite**: busy_timeout=30000, journal_mode=TRUNCATE,
  synchronous=FULL, Pooling=false are all set exactly as specified, and a
  repo-wide search confirms History.cs is the only file in Core that opens
  a SqliteConnection — nothing in the newer utility code touches SQLite or
  could reintroduce WAL. All writes and reads take the same `_gate` lock;
  ExportCsv correctly narrows the lock to the SELECT only.
- **Config.cs**: side-file confinement (ResolveBesideForWrite/ForRead), the
  WriteAtomic vs WriteAtomicNew split for box-labels.json's create-only
  bootstrap, and BoxLabelStore.Mutate's exclusive-handle read-modify-write
  with a 0-byte-vs-crash distinction are all correctly implemented per
  their own (extensive, already-audited) doc comments — I re-derived each
  invariant from the code rather than trusting the prose, and it holds.
- **BulkRename / MatchMerge**: both check-then-act (File.Exists then
  File.Move), but because the 2-arg File.Move refuses to overwrite an
  existing destination, a lost race produces a caught IOException /
  per-file error, never silent data loss.
- **Scanner.cs, Intake.cs, NaturalSort.cs, Naming.cs**: no writes at all;
  pure/read-only. Nothing to find.

## Could not check

- Whether the [U] cross-volume MoveNeverOverwrite gap (Important #3) is
  actually reachable — needs a real kill/power-loss test across two
  volumes, which I did not attempt (read-only audit, and the repo's own
  notes say this needs exactly that kind of test, not code reading).
- Real SMB-share race timing for findings #1 and #2 — reasoned from the
  code and the app's documented deployment (several stations, shared
  folders), not reproduced under load.
