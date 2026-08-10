# v1.0 release audit - security, privacy, configuration, concurrency (shared SMB deployment)

Repo: `S:\ordosort-session`, branch `session/header-pickers`, HEAD `6c11ead`.
Read-only; nothing in the worktree was changed except this file.

Scope: `Config.ResolveBeside*`/`ResolveConfined` path handling; concurrent-station
atomic writes (`Config.WriteAtomic`, `WriteAtomicNew`, `BoxLabelStore.Mutate`,
`History` SQLite settings); the newest utility tools (`Zipper`, `ZipMerge`,
`Intake`, `Collision`) and their WPF windows/view models; the WebView2 PDF
viewer's navigation guard (`WebViewPdfViewer`); crash.log and dialog content
for password/PHI leakage. Owner decisions treated as settled and not
re-litigated: plaintext saved_passwords in the shared config.json (share
permissions are the boundary, DPAPI rejected), no LICENSE file, history_db
deliberately unconfined.

## Counts

- Critical: 0
- Important: 2
- Minor: 2
## Important

1. `src/OrdoSort.Core/Zipper.cs:98` - CreateZipCore's Save-As branch does
   File.Delete(outputPath) unconditionally whenever a file currently sits at
   that path, then creates the zip fresh, with no created-gate protecting
   the delete - unlike every other destructive write in this same file and its
   siblings (Unlock.PlaceAndSwap, ZipMerge.MergeZipCore, Zipper.Extract),
   which all gate cleanup on "did this call create the thing." Trigger: two
   coworkers on the shared drive both use Zip - Save As to the same filename
   in the same folder (a plausible default-name collision, e.g. both zipping a
   folder called "Intake"); station B writes its file in the gap between
   station A's dialog closing and this line running; station A's delete
   destroys B's just-written archive with no recovery. Exposed: any coworker
   on the share, no elevated access needed - a plain concurrent-usage race,
   not an attacker. Independently corroborated by the parallel
   2026-08-09-v1-release-audit-core.md (same file/line, correctness lens);
   flagged here because it is exactly the "shared destination clobbered by a
   peer" class of bug this dimension exists to catch, and the doc comment's
   claim ("the exclusive create right after is what actually protects the
   created-gate") is true for a pre-existing file the user already saw, not
   for one written by someone else after the dialog closed.
2. `src/OrdoSort.Core/Config.cs:338-353` (ResolveConfined) - containment
   is checked entirely on the lexical result of Path.GetFullPath, which
   does not resolve reparse points. A directory symlink or NTFS junction
   placed inside the config directory (e.g. destinations_file = link/x.json
   where link is a junction pointing elsewhere) would pass this check -
   full lexically starts with configDir\ - while the filesystem actually
   lands the read/write somewhere else once Windows follows the reparse
   point. This is the same class of escape the 2026-08 audit closed for ..
   and rooted-without-drive paths, left open for filesystem-level indirection.
   Mitigating factors, which is why this is Important and not Critical:
   (a) Windows disables remote-to-local and remote-to-remote symlink
   resolution by default (fsutil behavior query SymlinkEvaluation), and this
   app's whole deployment model is a remote share, so a symlink object
   stored on the share pointing at a local path or another UNC path is not
   followed by default on the victim station; (b) an NTFS junction is
   volume-local, so one planted on the share can only redirect within the
   same server-side volume, not onto a victim workstation's own disk - a
   narrower escalation than the original "share-write becomes local-arbitrary-
   write" finding ResolveBesideForWrite's doc comment describes fixing.
   Net effect: today's safety here comes from an OS default the code does not
   control and a comment does not mention, not from anything in Config.cs
   itself - worth a one-line note in the doc comment so a future reader does
   not assume the string check alone is sufficient. Exposed: requires someone
   who can already create a reparse point in the config's own directory on the
   share (a junction needs only write access to that folder - no special
   privilege; a symlink needs SeCreateSymbolicLinkPrivilege or Developer
   Mode) - a materially higher bar than merely hand-editing config.json
   text, but still short of domain admin.
## Minor

3. src/OrdoSort.Wpf/ViewModels/SettingsViewModel.cs:1235 and :2310-2318
   (RecomputeDataFileNote, AdoptRepointedSection) resolve a side-file path
   with the unconfined Config.ResolveBeside and then probe File.Exists or
   attempt a read, instead of ResolveBesideForRead - the confined reader
   every other read path in this file (PickSideFile, Config.ReadDoc) uses.
   Inconsistent with the doctrine Config.cs's own doc comments spend several
   paragraphs establishing (resolve once, confined, before any filesystem
   probe). Not independently exploitable as a cross-station oracle: the
   result (FieldNote.Problem/Info text) is shown only in this station's own
   local Settings window, never written back to the shared config or
   crash.log, and the actual save path for these fields still goes through
   ResolveBesideForWrite correctly, so nothing is ever written outside the
   config directory because of this gap. Worth tightening for consistency,
   not urgent.

4. src/OrdoSort.Wpf/App.xaml.cs:114-127 (LogCrash) appends to a shared
   crash.log beside config.json on the SMB share via File.AppendAllText
   with no cross-process coordination. Concurrent crashes from two stations
   writing at nearly the same instant could interleave partial writes on some
   SMB/filesystem combinations (this was not empirically reproduced here - a
   repro would need real multi-process contention against a real share).
   Worst case is a garbled log entry (an availability/legibility problem for
   whoever reads the log later), not an information-disclosure or data-loss
   issue - crash.log is diagnostics-only and nothing else in the app reads it
   back. Rated Minor because the blast radius is "harder to debug a crash,"
   not privacy or data loss.
## Verified sound (reasoning included, not just asserted)

- Saved-password plaintext + DPAPI migration (PasswordVault.cs,
  UnlockViewModel.MigrateProtectedToPlaintext, UnlockViewModel.cs:568-591) -
  correctly implements the owner's decision. The migration can never lose a
  password: an unexpected exception rolls back every entry the call touched
  to its original ciphertext (UnlockViewModel.cs:585-590), and an entry that
  cannot be decrypted (the normal case for one protected on a different
  machine/account) is left completely untouched rather than collapsed to ""
  (PasswordVault.TryReveal, PasswordVault.cs:58-73, distinct from the lossy
  Reveal). No stale "encrypted"/"protected" claim remains anywhere in the UI
  - ManageSavedWindow.xaml:22 states plainly: "Stored as plain text in the
  shared config.json - anyone who can open that file can read them." The
  comment and the code agree.

- The four confined side-file keys (destinations_file, monitored_folders_file,
  alerts_file, box_labels_file) - .. traversal and Windows rooted-without-
  drive paths (\evil.json) are correctly refused on both read and write via
  canonical (Path.GetFullPath) comparison with a trailing-separator-guarded
  prefix check (Config.cs:338-353), verified against
  tests/OrdoSort.Core.Tests/SideFilePathConfinementTests.cs. The previously-
  fixed existence-oracle for box_labels_file (Config.cs:518-528, 733-742) is
  correctly closed: the File.Exists probe runs against the same already-
  confined path the write uses, so an escaping value is refused
  unconditionally before any filesystem probe, on both Save and the 4-arg
  TrySave.
- Concurrency on the shared config - WriteAtomic/WriteAtomicNew (GUID-suffixed
  sibling temp file, retrying File.Replace, create-only semantics for
  box-labels.json's bootstrap) and BoxLabelStore.Mutate (exclusive
  FileShare.None open with contention-only retry, "0 bytes on an existing
  file is a crash, not first-run" guard, write-then-truncate ordering) both
  correctly implement the read-modify-write discipline needed on SMB.
  ShellViewModel.SaveSavedPasswordsNow (re-reads a fresh Config with
  createIfMissing:false and overlays only SavedPasswords,
  ShellViewModel.cs:1567-1604) and ApplySettingsAsync's peer-clobber guards
  verified correctly closed: a momentarily-missing shared config.json can no
  longer trigger Config.Load's first-run save-all-defaults path and wipe
  every peer's Theme/TileVisibility/Sounds (the "wiped every peer's theme"
  bug named in the brief) - createIfMissing:false throws
  ConfigMissingException instead. The box-label "id-edit sweeps a live
  sibling" bug (commit a961c59) is correctly fixed and tested
  (LabelMakerViewModel.Persist's stillOwned guard,
  LabelMakerViewModelTests.cs:584).

- Newest tools (Zipper, ZipMerge, Intake, Collision) - ZipSlip is correctly
  handled two different ways: Zipper.Extract relies on .NET's own
  ZipFile.ExtractToDirectory traversal guard (load-bearing, and pinned by a
  regression test per its own doc comment), while ZipMerge never touches a
  zip entry's name as a filesystem path at all - entries are read purely as a
  content source into memory, so traversal is structurally impossible there,
  not just guarded. Zipper.Extract's and ZipMerge's created-gate discipline
  (only delete/clean up what this call provably created) is correct in both
  files - Zipper.CreateZipCore's Save-As branch (Important #1 above) is the
  one exception. All drag-drop/Browse intake in the WPF windows
  (ZipWindow/UnzipWindow/ZipMergeWindow) is local-file-picker-driven, no
  attacker-reachable path construction.
- WebView2 PDF viewer - NavigationStarting/DownloadStarting/
  NewWindowRequested guards, plus IsScriptEnabled = false and
  AreHostObjectsAllowed = false, are all present and verified by
  ViewerNavigationPolicyTests.cs's allow-list-of-one policy
  (IsPermittedNavigation) and by WebViewPdfViewerGuardBehaviourTests, which
  drives the real Chromium engine end-to-end (not a reflection stub) and
  confirms the guard fails closed (blanks the pane) rather than open,
  including for an awkward realistic filename with spaces/en-dash/non-ASCII
  characters that could plausibly desync .NET's and Chromium's URL
  canonicalizers. A hostile filename cannot forge _expected - only
  ShowAsync/Blank/ReleaseAsync set it, immediately before Navigate.

- Logging/dialogs - grepped every Unlock.cs result path; the typed or saved
  password is never interpolated into any Message/exception text that could
  reach crash.log or a dialog. crash.log is written beside config.json (the
  same share, the same trust boundary already accepted for saved passwords)
  - it can end up carrying document filenames (which by this app's design
  already carry patient names/control numbers), but that is not a new
  exposure channel: anyone with read access to the share already sees
  identical filenames throughout the actual inbox/deferred/route folders.
  CSV exports (History, TurnaroundTime, ProductionReport) all route through
  Csv.EscapeField, which correctly guards Excel formula injection (=+-@, tab,
  CR get a leading apostrophe) - verified in Csv.cs:69-81. XlsxTable.cs's
  XDocument.Load calls are not XXE-vulnerable: this project targets
  net8.0(-windows), whose default XmlReaderSettings prohibit DTD processing
  and use no XmlResolver.

## Could not check

- Empirical repro of the crash.log concurrent-append interleaving (Minor #4)
  or the Zipper Save-As race (Important #1) against a real SMB share under
  real multi-station contention - both are reasoned from code and platform
  semantics, not reproduced with two live processes.
- Whether the shared config.json deployment actually runs with Windows'
  default SymlinkEvaluation policy at any real customer site (Important #2
  assumes default policy; an admin who has enabled remote symlink evaluation
  removes that mitigation).
- Did not re-audit Commit.cs/Naming.cs/FolderMonitor.cs/HistoryBackup.cs in
  depth - pre-existing, heavily-audited files (per their own doc comments)
  outside this pass's "newest code" weighting; spot-checked only the rename-
  path note in commit a961c59's message, which reports them independently
  investigated and found sound for the same "sibling swept off disk" shape.
- No pixel-level verification of any dialog/tooltip text; content was read
  from source, not rendered.
