# Zip, unzip, and merge — and a password instead of a failure

**Date:** 2026-08-28
**Status:** approved in design review, ready for planning
**Supersedes:** `2026-08-25-zip-window-split-design.md`. That spec's window split
is carried into this one unchanged; its "the Core engines do not change"
scope is what stops holding.

## Problem

Three things the zip tools cannot do, and one they do badly:

- **A password-protected zip fails.** `Zipper.Extract` and `ZipMerge.MergeZip`
  read through `System.IO.Compression`, which has no encryption support at
  all — not ZipCrypto, not AES. An encrypted archive comes back as "couldn't
  extract: …" with a message about a corrupt stream, which is not what
  happened.
- **A password-protected PDF inside a zip fails the whole zip.** By design —
  fail-whole is the right discipline, see below — but with no way to supply
  the password, the design's loud failure is also a dead end.
- **Loose PDFs cannot be merged.** Merge takes archives only. Merging the
  seven PDFs a batch produced means zipping them first.
- **The window.** The 2026-08-25 spec found a zip dropped on the tools window
  lands in whichever tab happens to be selected, silently. Its fix — two
  windows — is approved, planned, and not yet built.

The Unlock tool already has the mature half of the answer for PDFs: a
candidate list (typed plus every saved password), a read-only readiness probe
on add, and a settled PdfSharp exception discipline in `Unlock.cs`. None of
that reaches the zip tools.

### Decisions taken in review

Asked one at a time; each answer shapes what follows.

| Question | Decision |
|---|---|
| Several PDFs and zips dropped on Merge — what comes out? | **One PDF per source.** Each zip → its own merged PDF; the loose PDFs → one merged PDF. Not "everything → one document". |
| Zip passwords: read only, or create too? | **Read only.** Extract and merge from encrypted zips. Zip creation stays unencrypted. |
| When to ask for a password? | **Both.** Probe on add so the count is visible before the run, and pause to ask during the run for anything the known passwords do not open. |
| Try the Unlock tool's saved passwords here? | **Yes, for PDFs and zips.** One password list for the whole app. |
| Where the password loop lives | **In Core, via a callback** — not "return needs_password and let the view model retry the whole operation", which re-reads a large zip per prompt and cannot say which entry wanted which password. |
| Window structure | **Two windows**, as the 08-25 spec chose. One list, three actions was considered a third time and rejected for the reasons both prior specs record. |

## Goal

Two single-purpose windows — *Zip and unzip*, *Merge PDFs* — where anything
password-protected asks for its password instead of failing, where the
passwords the app already knows are tried before anyone is asked, and where a
skipped password leaves a row that can simply be run again.

## Behaviour

### Zip and unzip (`ZipToolsWindow`)

The 08-25 spec's window, promoted from the tab exactly as that spec lays out,
with one addition: Extract copes with a locked zip.

```
[ Add files… ] [ Add folder… ] [ Add zips… ] [ Remove selected ] [ Clear ]
  Item                          Kind     Result
  Batch 12.zip                  zip      needs a password
  Scans 0827.zip                zip      a saved password opens this
  Q3 report.pdf                 pdf
  ─────────────────────────────────────────────────────────────────
  3 items · 2 zips · 1 needs a password
[ Zip 3 items ]  [ Zip to… ]  [ Extract 2 zips ]                 [ Close ]
```

| Action | Enabled when | Acts on |
|---|---|---|
| Zip, Zip to… | the list is not empty | every row |
| Extract | at least one zip row is runnable | runnable zip rows, one at a time |

Permissive intake, Zip folding the whole list, Extract mapping each archive to
its own sibling folder, sequential and cancellable between items — all
unchanged. Zip output is never encrypted (decision above), so the Zip side of
this window does not change at all.

### Merge PDFs (`MergePdfsWindow`)

New. Replaces the "Merge PDFs from zips" window the 08-25 spec described.
Accepts PDFs and zips; anything else is refused by intake with its usual note.

```
[ Add PDFs or zips… ] [ Remove selected ] [ Clear ]
  Item                          Kind     Result
  Batch 12.zip                  zip      → Batch 12.pdf (14 PDFs)
  cover.pdf                     pdf      not merged — appendix.pdf needs a password
  Q3 report.pdf                 pdf      not merged — appendix.pdf needs a password
  appendix.pdf                  pdf      needs a password
  ─────────────────────────────────────────────────────────────────
  1 merged · 1 needs a password
[ Merge 3 items ]  [ Merge to… ]                                  [ Close ]
```

(After one run: the zip merged; the loose group did not, because one of its
three was skipped at the prompt. All three loose rows are still runnable, so
the button counts them.)

One output per source:

- **Each zip → `<zipname>.pdf` beside it**, every PDF inside natural-sorted by
  entry path. This is today's `ZipMerge` behaviour, unchanged.
- **All loose PDFs in the list → one PDF**, natural-sorted by file name
  (`2.pdf` before `10.pdf`, the way every list in this app sorts; two files
  with the same name in different folders fall back to full-path order; no
  drag-reorder). It is saved beside the first PDF in that order and named
  after that PDF's folder — `C:\Jobs\Job 4471\cover.pdf` → `Job 4471.pdf` —
  the same default-name rule `Zipper.DefaultName` applies to a zip, so the
  two windows guess alike. `Merged.pdf` when that folder has no name (a drive
  root), mirroring `Archive.zip`. Collision-suffixed via `Collision.FreeFile`,
  never overwritten.
- **Merge to…** is a Save-As for the loose-PDF output only — zips already have
  a natural name and place. Enabled only while the list holds a runnable loose
  PDF. A path chosen there is placed the way `Zip to…` places an archive:
  built to a GUID-named temp sibling and moved onto the chosen name through
  `AtomicPlace.TryReplace`, so a merge that fails part-way leaves whatever was
  at that name untouched.
- **Output is always a plain, unencrypted PDF.** PdfSharp's Import mode copies
  pages into a fresh document, exactly as Unlock does. A locked source does
  not produce a locked merge.
- **Fail-whole, never partial.** If one loose PDF cannot be opened — skipped
  at the prompt, or unreadable — the group does not merge: that row says why,
  the others stay pending with *not merged — appendix.pdf needs a password*
  (or *… couldn't be read*, for the unreadable case).
  A zip with an unopenable PDF inside fails as a whole, as today. `ZipMerge`'s
  class comment already makes the argument: a merged document that quietly
  dropped a file looks complete, and in a filing app that is the dangerous
  outcome. A loud non-result is the safe one.

Units run in list order, zips first, the loose group last; progress reads
"Merging 2 of 3…" over units, not rows.

### The password prompt

Appears only when the run reaches something none of the known passwords
opens. The operation pauses — nothing else in the batch is touched — until
the prompt is answered.

```
  [lock]  Batch 12.zip is password-protected.
          (or: report.pdf inside Batch 12.zip is password-protected.)
          That password didn't open it.              <- only after a failed try

          Password  [••••••••••••]  [ show ]

                                  [ Open ]  [ Skip this one ]
```

- **Enter** = Open; **Esc** = Skip. Skip is the default, for the reason
  `MessageWindow.Confirm` gives: the safe answer is the one a reflexive Enter
  should land on. Here "safe" means "nothing happens to this item".
- A wrong password re-asks with the second line shown; the loop ends when the
  item opens or the answer is Skip.
- **Skip leaves the row runnable** as *needs a password* (see statuses below).
  Press Extract or Merge again and it asks again — no remove-and-re-add.
- The *show* toggle mirrors `UnlockWindow`'s: a `PasswordBox` and a visible
  `TextBox` bound to the same value, one of which is collapsed.

### Where passwords come from

Tried in this order, silently, before anyone is asked:

1. **Typed in this window**, most recent first. Kept for the window's
   lifetime, not the run's: a second Extract in the same window never re-asks
   for a password the first one learned. Not persisted anywhere.
2. **The Unlock tool's saved passwords** from `config.json`, in list order,
   read once when the window opens through the same `PasswordVault.Reveal`
   path Unlock uses. A legacy DPAPI entry this machine cannot decrypt is
   skipped, not reported — Unlock already owns that conversation.

Nothing is ever *saved* from these windows. Unlock stays the one place a saved
password is added or removed; the "save this password?" banner is a follow-up
if it turns out to be wanted, and not worth spreading the config-write path
across three windows now.

### Row statuses and notes

`ZipItemRowStatus` gains one value: **`NeedsPassword`**, alongside `Pending`,
`Ok`, `NoPdfs`, `Error`. A row is **runnable** when it is `Pending` or
`NeedsPassword`; every batch selects runnable rows, every button count counts
them. `NeedsPassword` renders amber in the Result column, like `NoPdfs` — not
done, not broken.

The probe on add writes a readiness verdict into the Result column while the
row is still pending:

| Probe verdict | Row | Result column |
|---|---|---|
| not encrypted | `Pending` | *(blank)* |
| ready — a known password opens it | `Pending` | *a saved password opens this* |
| needs a password | `NeedsPassword` | *needs a password* |
| in use (PDF, Unlock's verdict) | `Pending` | *open in another program* |
| unreadable / not a valid zip | `Error` | the probe's own message |

The run overwrites the note with its result, exactly as today. The run's tally
line gains a bucket: *1 needs a password* / *2 need a password*.

Which rows get probed: the Zip window probes zip rows (files and folders need
nothing); the Merge window probes zips **at archive level** and loose PDFs
with `Unlock.ProbeReadiness`. PDFs *inside* zips are not probed on add — that
would read every zip fully twice over a share — and are asked for during the
run, which is what the prompt is for.

### Tools menu

`_Zip and unzip…` stays. `Merge _PDFs…` goes directly below it, in the same
group after the last separator. `P` is the accelerator: `M` would collide with
`_Match and merge…`, the app's other merge tool and a genuinely confusable
neighbour. Icon `&#xE8A5;` (document), pairing with `&#xE8B7;` on the zip
entry.

Window titles: "OrdoSort — Zip and unzip" (unchanged), "OrdoSort — Merge PDFs".
Both 700×520, min 580×420; both keep window-level `AllowDrop`; both keep the
empty-state copy *Drag … anywhere on this window*, now true of exactly one
list each.

## Structure

### One dependency

`SharpZipLib 1.4.2` (`ICSharpCode.SharpZipLib`, MIT, managed-only, targets
net6.0) in `OrdoSort.Core`. Every zip **read** — extract, merge, probe — goes
through it, so a zip is read one way. Zip **creation** stays on
`System.IO.Compression`: it is unencrypted by decision, and `ZipFile.Open`'s
atomic `FileMode.CreateNew` created-gate in `Zipper.CreateZip` is proven and
tested. One library per direction; never two per operation.

Two things `System.IO.Compression` did for free become ours, and are the
correctness rules of this change:

1. **The ZipSlip guard.** `ZipFile.ExtractToDirectory` refused any entry
   resolving outside the destination; `Zipper.Extract` now resolves each
   entry's full path itself and refuses one that does not sit under the
   output folder — `..` segments, rooted names, drive-qualified names — before
   a byte is written. `ZipperTests.ZipSlipEntryIsRejectedAndLeavesNoTraceOutsideOrInside`
   keeps guarding it, joined by rooted and drive-qualified cases.
2. **A password counts only if an entry decrypts *and* verifies.** ZipCrypto's
   header check is one byte: 1 wrong password in 256 passes it and yields
   garbage. The CRC is what catches that, so every ZipCrypto entry read is
   CRC-checked (`Crc32` over the decrypted bytes against `ZipEntry.Crc`); AES
   entries (`ZipEntry.AESKeySize > 0`) carry an authentication code
   SharpZipLib checks at end of stream. The probe verifies against the
   **smallest** encrypted entry (by uncompressed `ZipEntry.Size`), which
   bounds its cost; the run verifies every entry it writes.

One password per archive: the password that opens the first encrypted entry
is set on the `ZipFile` for the whole archive. A later entry it does not open
fails the zip with a note naming that entry. Mixed encrypted and plain entries
in one archive are handled per entry (`ZipEntry.IsCrypted`). A `ZipException`
on open is "not a valid zip", the same voice as today's `InvalidDataException`.

### Core

**The password contract** — pure, no UI:

```csharp
public sealed record PasswordRequest(string Item, string? Inside, bool PreviousAttemptFailed);
// "Batch 12.zip"            -> Item = "Batch 12.zip", Inside = null
// "report.pdf" in that zip  -> Item = "report.pdf",   Inside = "Batch 12.zip"
```

Every locked operation takes the same pair: `IReadOnlyList<string> candidates`
(tried first, in order, silently) and `Func<PasswordRequest, string?>? ask`
(called only when no candidate opens the item; `null` = skip). Core tries the
answer, re-asks with `PreviousAttemptFailed = true` on failure, and stops on
`null` with status `needs_password`. Core remembers nothing — the caller owns
the candidate list.

| Call | Statuses |
|---|---|
| `Zipper.Extract(zip, candidates, ask)` | `ok` · `needs_password` · `error` |
| `Zipper.Probe(zip, candidates)` — read-only | `not_encrypted` · `ready` (+ `MatchedIndex`) · `needs_password` · `unreadable` |
| `PdfMerge.MergeZip(zip, candidates, ask)` — today's `ZipMerge.MergeZip`, renamed | `ok` · `no_pdfs` · `needs_password` · `error` |
| `PdfMerge.MergeFiles(pdfs, outputPath?, candidates, ask)` — new | `ok` · `needs_password` · `error` |
| `Unlock.ProbeReadiness(path, candidates)` — unchanged | as today |

`Zipper.Extract(string)` and `ZipMerge.MergeZip(string)` — today's signatures —
are removed, not kept as overloads: an operation that cannot ask is the
failure this change exists to remove, and two ways to extract is exactly what
the one-version rule forbids. Their callers are the two view models and the
tests, all of which change anyway.

**`Zipper`** — `CreateZip`, `DefaultName`, the created-gate discipline and the
cleanup helpers are untouched. `Extract` reads through `ZipFile`: resolve the
password if any entry `IsCrypted` (candidates, then `ask`, each verified
against the smallest encrypted entry); create the output folder behind the
existing `created` gate; then per entry — path guard, directory entries create
a folder, file entries are written with `FileMode.CreateNew` (so a duplicate
entry path still fails loudly, as `ExtractToDirectory` made it) while the copy
computes the CRC. `Probe` is the same password resolution with no `ask` and no
output folder; it writes nothing, anywhere.

**`PdfMerge`** replaces `ZipMerge` (rename, not a second class). `MergeZip`
and the new `MergeFiles` share one private routine — buffer the bytes, open
with passwords, add every page — and differ only in where the bytes come from
and where the result goes. `MergeResult` gains `Item`: the file or entry that
stopped a merge, which is what lets the view model mark the right row.
`MergeFiles` reads each file with the same in-use exception discipline
`Unlock.UnlockBuffered` uses, and places its output through `Collision.FreeFile`
(default name) or `AtomicPlace.TryReplace` (Save-As), the two branches
`Zipper.CreateZip` already documents.

**`PdfPasswords`** — new, small, the one place that knows what "wrong
password" looks like to PdfSharp. `Unlock`'s private `IsProvablyNotEncrypted`
and its candidate loop (`PdfReaderException` = wrong password, try the next;
anything else = unreadable, stop) move here and grow the `ask` step.
`Unlock.ProbeReadiness` and `UnlockBuffered` call it; their behaviour does not
change, and `UnlockTests` plus `UnlockProbeAgreementTests` passing untouched
is the proof.

### Wpf

**`IDialogService.AskPassword(PasswordRequest) -> string?`**, defaulted to
`null` in the interface so the fourteen fakes, recorders and scripted stubs
inherit "skip" — the same reasoning `AskOpenFiles` and the labelled `Confirm`
record for their defaults. `DialogService` implements it with
**`PasswordWindow`**, a themed window in `MessageWindow`'s shape (owner-modal,
no `IsCancel` button, Escape handled once at the window and always meaning
Skip).

**`ZipListViewModel`** (the shared base) grows three things:

- **Units.** A batch is a list of units; a unit is one zip row *or* the whole
  loose-PDF group. Each unit is one Core call; its result is applied to every
  row in it. `RunBatchAsync` takes units instead of rows; today's one-row unit
  is the degenerate case, so Extract and MergeZip keep their shape.
- **Passwords.** The typed-this-window list (front-inserted), the saved list
  handed in at construction, `Candidates()` in the order above, and the `ask`
  lambda: marshal to the UI thread with `SynchronizationContext.Send`
  (synchronous — the worker waits on the person; inline when `UiContext` is
  null, which is every test and the E2E harness), show the prompt, record a
  non-null answer, return it.
- **Probe on add.** After `AddPaths` settles new rows, each is probed
  off-thread through `Scheduler`, at most four at once (Unlock's
  `MaxConcurrentUnlocks` figure, same reasoning: a probe is a real read, often
  over a share). The probe token is replaced on Clear and cancelled on close —
  the shape `UnlockViewModel._probeCts` already has — so a verdict never lands
  on a row nobody can see. Each subclass says which rows it probes and how.

Constructors become `(IDialogService dialogs, IReadOnlyList<string> savedPasswords, IWorkScheduler? scheduler = null, SynchronizationContext? uiContext = null, …)`
plus the existing `Func<…>` seams for the Core calls, so tests drive real view
models with scripted engines and no PDF fixtures.

**`ZipExtractViewModel`** adds its unit builder (one per runnable zip row) and
probe (`Zipper.Probe` on zip rows). **`MergePdfsViewModel`** adds intake
`{pdf, zip}`, its unit builder (zips, then the loose group), `MergeToCommand`,
and its probe (`Zipper.Probe` on zips, `Unlock.ProbeReadiness` on PDFs).
**`ZipToolsViewModel`** is deleted; it only ever held the two tab view models
and forwarded `Cancel`.

**`ZipItemRow`**: `NeedsPassword`; `KindOf` learns `"pdf"`, so the Kind column
reads *pdf* rather than *file* in both windows; `Apply` handles the new
statuses and the group case (the same result to every row of a unit, the
culprit row from `MergeResult.Item` marked differently from the rest).

**Windows.** `ZipToolsWindow` loses its `TabControl`, both `TabItem`s and the
swapping footer exactly as the 08-25 spec lays out; `OnDrop`'s branch is
deleted rather than improved. `MergePdfsWindow` is new — the merge grid,
toolbar and footer as a window, `DataGridColumnCap.Track` on its Result column.
`MainWindow.OnZipTools` constructs `ZipExtractViewModel`; `OnMergePdfs` is
new; both hand over the revealed saved-password list from `Shell.Cfg`.

### Files

| File | Change |
|---|---|
| `src/OrdoSort.Core/OrdoSort.Core.csproj` | `PackageReference` SharpZipLib 1.4.2. |
| `src/OrdoSort.Core/PasswordRequest.cs` | **New.** The record above. |
| `src/OrdoSort.Core/PdfPasswords.cs` | **New.** The PdfSharp password loop, extracted from `Unlock`. |
| `src/OrdoSort.Core/Zipper.cs` | `Extract` and `Probe` on SharpZipLib with the path guard and CRC/AES verification; class comment rewritten for a guard that is now ours. `CreateZip` untouched. |
| `src/OrdoSort.Core/ZipMerge.cs` → `PdfMerge.cs` | Renamed. `MergeZip` gains passwords; `MergeFiles` new; one shared routine; `MergeResult.Item`. |
| `src/OrdoSort.Core/Unlock.cs` | Calls `PdfPasswords`; no behaviour change. |
| `src/OrdoSort.Wpf/Services/IDialogService.cs`, `DialogService.cs` | `AskPassword`. |
| `src/OrdoSort.Wpf/Windows/PasswordWindow.xaml(.cs)` | **New.** |
| `src/OrdoSort.Wpf/ViewModels/ZipListViewModel.cs` | Units, passwords, probe, `NeedsPassword`, `"pdf"` kind. |
| `src/OrdoSort.Wpf/ViewModels/ZipExtractViewModel.cs` | Unit builder, probe, new constructor; doc comment (no longer "the tab"). |
| `src/OrdoSort.Wpf/ViewModels/MergePdfsViewModel.cs` | Intake, unit builder, `MergeToCommand`, probe, new constructor. |
| `src/OrdoSort.Wpf/ViewModels/ZipToolsViewModel.cs` | **Deleted.** |
| `src/OrdoSort.Wpf/Windows/ZipToolsWindow.xaml(.cs)` | De-tabbed per the 08-25 spec; `DataContext` is `ZipExtractViewModel`. |
| `src/OrdoSort.Wpf/Windows/MergePdfsWindow.xaml(.cs)` | **New.** |
| `src/OrdoSort.Wpf/MainWindow.xaml(.cs)` | `Merge _PDFs…` item; `OnMergePdfs`; both handlers pass saved passwords. |
| `tools/OrdoSort.Smoke/E2E/ScriptedDialogs.cs` | Scripts `AskPassword` answers. |
| `README.md` | Tools list: nine entries. |
| `CONTEXT.md` | `ZipMerge.MergeZipCore` → `PdfMerge.MergeZipCore` in the created-by-me gate section; nothing else. |
| `docs/superpowers/specs/2026-08-25-zip-window-split-design.md` | Status line: superseded by this spec. |
| `docs/superpowers/plans/2026-08-26-zip-window-split.md` | **Deleted** — never executed; its tasks are absorbed here. |

## Testing

Fixtures are built in-test, never checked in: SharpZipLib writes ZipCrypto
archives (`ZipOutputStream.Password`) and AES ones (`ZipEntry.AESKeySize = 256`);
PdfSharp writes password-protected PDFs the way `UnlockTests` already does.

### Core

- **The regression net for the library swap:** every existing `ZipperTests`
  and `ZipMergeTests` fact passes through the new reader with, at most, the
  call-site change to the new signatures. Behaviour on plain zips is
  unchanged by definition, and this is what proves it.
- `Zipper.Extract`: a locked zip with the right candidate; wrong candidate,
  then `ask` supplies it; `ask` returns null → `needs_password`, no output
  folder left behind; `ask` is not called when a candidate works; the re-ask
  carries `PreviousAttemptFailed`; mixed encrypted and plain entries; a later
  entry with a different password fails naming that entry; ZipCrypto and AES
  both.
- **A wrong password that passes ZipCrypto's check byte is still rejected.**
  Test setup finds one by brute force — opening the fixture through
  SharpZipLib with `wrong0`, `wrong1`, … until `GetInputStream` accepts the
  header (about 256 tries on a tiny entry) — then asserts `Extract` reports an
  error with no output and `Probe` reports `needs_password`. The plan verifies
  SharpZipLib's header-check behaviour before relying on it.
- ZipSlip pin as today, plus rooted (`/evil.txt`) and drive-qualified
  (`C:\evil.txt`) entries: refused, no trace inside or outside.
- `Zipper.Probe`: the four verdicts; a *writes-nothing* proof in
  `UnlockProbeWritesNothingTests`' shape (directory snapshot before and after).
- `PdfMerge.MergeFiles`: natural-sort order across a `2.pdf`/`10.pdf` pair;
  default name and place; Save-As through `AtomicPlace`; collision suffix;
  fail-whole on skip with `Item` naming the culprit; a locked PDF opened by a
  candidate; output is not encrypted.
- `PdfMerge.MergeZip`: a locked PDF inside hands `ask` the zip name as
  `Inside`; every existing `ZipMergeTests` case unchanged in outcome.
- `PdfPasswords`: `UnlockTests` and `UnlockProbeAgreementTests` pass without
  modification.

### View models

Scripted engines and a scripted `IDialogService` — no PDFs, no zips:

- `NeedsPassword` rows are selected by the next run; `Ok` and `Error` rows are
  not.
- A password typed for the first item is a candidate for the second: `ask` is
  called once, not twice.
- **`ask` reaches `UiContext`**: a recording `SynchronizationContext` asserts
  `Send` was used. The 2026-08-19 merge shipped a marshalling gap that every
  test hid by passing `uiContext: null`; this pin exists so that cannot
  happen again here.
- Probe verdicts land as the table above says; Clear cancels an in-flight
  probe; close cancels it.
- The loose group: one `MergeFiles` call for N rows; success applies the
  output to all N; failure marks the culprit from `Item` and leaves the rest
  pending with the note.
- `MergeToCommand` enables only with a runnable loose PDF in the list.
- `ZipExtractViewModelTests`, `MergePdfsViewModelTests`,
  `ZipListClearAndRemoveTests` and `ZipItemRowTests` keep passing with
  constructor updates only.

### Windows

- The 08-25 regression pin, on both windows: zero `TabControl`s, exactly one
  `DataGrid`, and a `FileDrop` of a `.zip` adds one row to that one list. A
  count assertion, because with one list "the right list got it" and "one
  list got it" are the same claim and the count is the one that keeps failing
  if a second list is ever reintroduced.
- `ZipToolsWindowTests.FooterActionsFollowTheSelectedTab` is deleted, not
  ported — it guards the machinery being removed.
- `PasswordWindow`: Enter opens, Esc skips, the show toggle reveals, every
  control has an accessible name.
- Registries: `MergePdfsWindow` joins `AutoFitColumnTests`,
  `DataGridSelectionContrastTests`, `DataGridNoteColourTests` (the amber
  `NeedsPassword` note), `DataGridWindowCoverageTests.CoveredWindows`,
  `DataGridSizingCoverageTests.SizingCovered` and `WindowOverflowTests` (which
  feeds `AccessibleNameTests`), with `MinExamined` measured, not guessed.
  `PasswordWindow` joins `WindowOverflowTests`. `ZipToolsWindow`'s
  `MinExamined` is re-measured: the old floor summed two tabs.

### End to end

`ZipMergeScenarios` retargets to `MergePdfsWindow`; `ZipScenarios` and
`UnzipScenarios` drop their tab-selection step; `ScriptedDialogs` scripts
`AskPassword`. Three new demonstrations: a locked zip → prompt → extracted; a
handful of loose PDFs → one document; a locked PDF skipped → nothing merged,
and the row says why.

### The check

`dotnet build OrdoSort.sln -t:Rebuild -v minimal` then
`dotnet test OrdoSort.sln --no-build -v minimal`, reading the `Passed!` counts,
before every commit — `docs/known-flakes.md`'s rule.

## Docs

- `README.md`'s Tools list goes from eight entries to nine: *Zip and unzip*
  loses its "one window, two tabs" clause and gains "asks for a password";
  *Merge PDFs* is new.
- `Zipper`'s class comment is rewritten: the ZipSlip paragraph describes our
  guard, not the framework's, and the created-gate paragraphs stay.
- `CONTEXT.md` changes in exactly one place — checked, not assumed: its only
  zip mentions are the created-by-me gate in `ZipMerge.MergeZipCore` and
  `Zipper`, and that gate is untouched; the `ZipMerge` name follows the
  rename to `PdfMerge`.
- The 2026-08-25 spec gets a one-line *superseded by* status. The 2026-08-18
  spec is not edited; it records a decision that was right on its evidence.

## Out of scope

Each of these is a separate piece of work with its own justification to make:

- Creating password-protected zips.
- Saving a password from these windows (the Unlock-style banner).
- Drag-reorder of loose PDFs; merge order is natural sort.
- Folders as merge sources.
- An extract destination picker, browsing inside an archive, selective
  extraction, compression level, per-archive progress.
- 7z, rar, or any format but zip.
- Probing PDFs inside zips on add.
- Renaming `ZipToolsWindow`: it holds one tool now, but the rename touches
  fourteen files for nothing a user sees.
