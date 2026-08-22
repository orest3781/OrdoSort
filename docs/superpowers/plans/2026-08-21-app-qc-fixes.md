# Plan — app QC fix pass, batch A

**Spec:** `docs/superpowers/audits/2026-08-21-app-qc.md`. That audit is the binding
authority. Every task below names the `QC-nn` finding it closes; if this plan and the audit
disagree, the audit wins.

**Branch:** `fix/app-qc-2026-08-21`, cut from `main` @ `66be355`.

**Scope of batch A:** all nine **High** findings, the two test-integrity findings (because a
suite that cannot fail cannot verify the other eight), and the four cheap **Important**
findings that share the propagation theme. Everything else in the audit stays open.

---

## Global Constraints

These bind every task. A task that violates one is not done.

### 1. The test command, and reading it correctly

```
dotnet build OrdoSort.sln -t:Rebuild -p:Deterministic=false -v minimal
dotnet test OrdoSort.sln --no-build -v minimal
```

`-p:Deterministic=false` is load-bearing — Smart App Control blocks the test assembly by
hash without it. **Do not "fix" it.**

**An exit code of 0 is not evidence that anything ran.** `dotnet test` has been observed here
to skip an entire assembly and still exit 0. Always read the `Passed!` line **and its count**,
and quote it in your report.

**Baseline at `66be355`: Core 660, Wpf 1764.** Your counts must be ≥ baseline plus the tests
you added. A count that *drops* is a deleted or silently-skipped test — investigate, never
accept.

### 2. Every new test is watched FAILING first — no exceptions

This repo has been bitten **four times** by the already-true-predicate trap: a test written to
pin a fix that asserts something already true before the fix, and therefore passes against the
broken code. The most recent instance is recorded in the audit's FL-05 verdict, and QC-09 is
nine test cases that measure nothing at all.

So, for **every** test you add:

1. Write the test.
2. Run it **against the unfixed code** and watch it fail.
3. **Quote the actual failure output in your report** — the assertion message and the
   expected/actual values.
4. Then write the fix and watch it pass.

A report that says "test added, suite green" without the red-phase output has not satisfied
this constraint, and the task will be sent back. If a test cannot be made to fail against the
unfixed code, say so explicitly and explain why — that is a finding, not a formality.

Where the fix is in Core and the defect needs a filesystem condition you cannot create on this
machine (a denied ACL, a second volume), say so and pin what you *can* — usually the seam,
not the syscall.

### 3. Never weaken a test to make it pass

Do not loosen an assertion, add a retry, raise a timeout, add `Skip=`, or narrow a probe's
scope to get green. If a test you did not write starts failing because your fix made a probe
actually measure something, **that is a real defect you have surfaced** — report it, do not
suppress it. See Task 1, where this is the expected outcome.

### 4. Style

Match the surrounding code. This codebase's comments explain **why**, often citing the
incident that motivated the rule — match that density and register where you add one. Do not
add comments that restate the code. Do not reformat untouched lines.

### 5. Commit discipline

Commit on `fix/app-qc-2026-08-21`. Never commit to `main`. One commit per task is fine;
several are fine. Message style: the repo uses `fix(scope): lowercase summary` — read
`git log --oneline -10` and match it.

### 6. Do not touch these

- `docs/sample/` — real exports, out of scope, do not read, move, or reference.
- Anything under `evidence/`.
- The audit doc and this plan.

---

## Task 1 — Make the coverage probes actually measure (QC-09, QC-26)

**Do this task first.** Until it lands, the Wpf suite's "1764 passing" cannot be trusted to
cover what it appears to cover, and every later task in this plan is verified against it.

### The defect

`tests/OrdoSort.Wpf.Tests/OverflowProbe.cs:51` skips every candidate:

```csharp
if (!e.IsVisible || e.ActualWidth == 0) continue;
```

`UIElement.IsVisible` is false for any element whose root is not connected to a
`PresentationSource` — i.e. any tree `Measure`/`Arrange`d by hand but never hosted in a shown
`Window`. Four call sites do exactly that, so `offenders` is unconditionally empty and
`Assert.True(offenders.Count == 0, …)` is unconditionally green:

- `WindowOverflowTests.cs:326-330` — the `probe.Show == false` branch, reached only by the
  **TriageWindow** registry entry (`:197`). 2 theory cases. TriageWindow is the widest window
  in the app and the class doc at `:45-47` presents it as covered.
- `WindowOverflowTests.cs:368-372` — `ProcessingViewFitsTheParkedPanel`. 2 cases.
- `WindowOverflowTests.cs:418-422` — `ReadyViewTilesFitThePanel`. 3 cases.
- `WindowOverflowTests.cs:449-453` — `DoneViewFitsTheParkedPanel`. 2 cases.

**This was proven, not inferred.** Injecting `MinWidth="2000"` into a `TextBlock` in
`DoneView.xaml` — a 2000px element inside a 370px panel — and running the three facts gave
`Passed! - Failed: 0, Passed: 7`. The mutation was reverted.

`LabelMakerOverflowTests.cs:66-78` and the main `NoTextElementEscapesTheWindow` path
(`WindowOverflowTests.cs:292`) both call `window.Show()` and are **unaffected** — use
`LabelMakerOverflowTests` as the working template.

### Required

1. **Make the four call sites host their view in a real shown window**, the way
   `LabelMakerOverflowTests.cs:60-78` already does: an off-screen `Window`
   (`Left = -20000`, `ShowActivated = false`, `WindowStartupLocation = Manual`) sized to the
   width the test is asserting against, `Show()`, `UpdateLayout()`, `OverflowProbe.PumpRender()`,
   and disposed/closed in a `finally`. Preserve each test's existing width and font-size theory
   data exactly — 370 for the parked-panel views, and whatever the registry entry supplies for
   TriageWindow.

2. **Give `OverflowProbe` an examined-element floor.** Change `HorizontalEscapees` (and the
   `Escapees` core) so callers can assert how many elements were actually judged — e.g. return
   the count via an `out int examined`, or add a sibling method. Then assert a floor at every
   call site: `Assert.True(examined >= 5, $"probe examined only {examined} elements — it is
   not measuring anything")`. Pick the exact floor per call site from what the view actually
   contains once hosted; 5 is a lower bound, not a target.

3. **Same floor for `TextWrapCoverageTests` (QC-26).** `TextWrapCoverageTests.cs:141` and
   `:169` both assert `offenders.Count == 0` and never assert that a single `TextBlock` was
   judged; every candidate can be skipped by `StyleHandsItWrapOrTrim` (`:102`). Count the
   TextBlocks that reached the judgement and assert a floor. **The fix pattern already exists
   in this repo** — `DataGridSizingCoverageTests.cs:194` floors its reflection at `Count >= 10`
   with a comment naming this exact failure mode. Match it.

4. While you are in `DataGridSizingCoverageTests`, add the same floor to
   `EveryStarColumnDeclaresItsOwnFloor` (`:248`), which lacks the floor its sibling 55 lines
   above has.

### Red phase for this task

The floor assertions ARE the red phase, and they are unusually easy to watch fail: add the
floor **before** hosting the views in a window, run, and the four call sites must fail with
`examined only 0 elements`. **Quote that output.** Then add the window hosting and watch them
pass.

### Expected outcome you must not suppress

Once these probes actually measure, **they may fail for real** — a genuine element that
escapes its panel at some font size. That is the entire point of the task.

**If that happens: do NOT adjust the probe, the floor, the width, or the font-size data to get
green.** Report it as a finding with the element, the view, the font size, and the measured
bounds, and stop. A newly-failing overflow assertion is a defect this fix pass just found,
and it will be triaged as its own task. Suppressing it would recreate exactly the problem
this task exists to remove.

---

## Task 2 — `FolderMonitor` survives one unreadable subfolder (QC-01)

### The defect

`FolderMonitor.cs:60-61` walks a recursive watch folder with the bare
`SearchOption.AllDirectories` overload:

```csharp
var option = wf.Recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly;
var files = Directory.EnumerateFiles(wf.Path, "*", option)
```

One unreadable subfolder — denied ACL, unhydrated cloud placeholder, junction — throws
`UnauthorizedAccessException`, the catch at `:79-82` fires, and `Status` returns
`Count = 0, Matches = [], Error = "can't read folder: …"` for the **entire tree**. A file
whose name contains an alert term, sitting in a fully readable sibling folder, raises no alert.

`Intake.Expand` had this exact bug and was fixed. **`Intake.cs:33-49` carries a 12-line comment
explaining precisely why the `SearchOption` overload is wrong and why both
`EnumerationOptions` properties are needed.** Read it before you write anything.

### Required

Give `FolderMonitor.Status` the same treatment `Intake.Expand` already uses:

```csharp
var options = new EnumerationOptions
{
    RecurseSubdirectories = wf.Recursive,
    IgnoreInaccessible = true,
    AttributesToSkip = 0,
};
```

`AttributesToSkip = 0` is **required alongside** `IgnoreInaccessible` — the
`EnumerationOptions` default silently skips `Hidden|System` files, which the old
`SearchOption` overload never did. Leaving it at 0 keeps behaviour identical everywhere except
the abort. This reasoning is in the `Intake.cs` comment; do not restate it at length, but do
leave a short comment pointing at `Intake.cs` so the next reader knows the two are deliberately
the same.

Keep the existing `catch` — it still covers a wholly unreadable root.

### Tests

`tests/OrdoSort.Core.Tests/FolderMonitorTests.cs`.

A real denied ACL is awkward to create portably. Get as close as the machine allows:

- **Preferred**, if you can make it work reliably: create a subfolder, deny read on it for the
  current user via `DirectoryInfo.GetAccessControl`/`SetAccessControl`, assert that a matching
  file in a *sibling* readable subfolder is still counted and still alerts, and that `Error` is
  empty. Restore the ACL in a `finally` so the temp dir can be deleted.
- **If that proves unreliable on this machine**, say so in your report and instead pin the
  seam: assert that `Status` on a recursive folder returns files from multiple subfolders and
  that the enumeration is configured with `IgnoreInaccessible` — e.g. by asserting the
  hidden/system-file behaviour is unchanged (a `Hidden` file in a recursive watch is still
  counted), which is what `AttributesToSkip = 0` protects and which **does** fail if someone
  later adds `EnumerationOptions` without it.

The second form is a genuinely weaker test. If you fall back to it, say plainly in the report
that the primary failure mode (abort on inaccessible subfolder) is **not** pinned by a test.

---

## Task 3 — A blank set-aside folder is refused, not silently resolved (QC-02)

### The defect

Verified hop by hop:

- `Config.cs:99` — `Deferred` defaults to `""`, and Settings never forces a value.
- `SettingsViewModel.cs:2238-2240` — `Warnings()` reads
  `if (deferred.Length > 0 && !_directoryExists(...))`, so a **blank** set-aside warns
  **nothing**. Two lines above, a blank *inbox* does warn ("No inbox folder is set").
- `Config.cs:300-303` — `ResolveBeside(cfgPath, "")`: `Path.IsPathRooted("")` is false, and
  `Path.Combine(dir, "")` returns `dir`. **The blank becomes the config directory.**
- `Commit.cs:84-86` — the guard is
  `if (string.IsNullOrWhiteSpace(deferredDir) || !Directory.Exists(deferredDir))`. By now
  `deferredDir` is the config folder: not blank, and it exists. **The guard cannot fire**, and
  its `"(not set)"` branch is unreachable from the shipped app.

Result: pressing Skip moves a patient document next to `config.json` and reports
*"✓ Set aside for later"*.

### Required — fix it in both places

**Core (the load-bearing half).** `Session.SkipCurrent` (`Session.cs:143-152`) must refuse a
blank `_cfg.Deferred` **before** `Config.ResolveBeside` flattens it to the config directory.
Throw the same `CommitError` shape `Commit.SkipFile` already produces for an unavailable
set-aside folder, so the existing UI error path (`ShellViewModel.OnSkipAsync` catches
`CommitError`) reports it unchanged. Core refusing is what makes the misfile impossible
regardless of what any UI does.

**Settings (so the user can see and fix it).** `SettingsViewModel.Warnings()` must warn on a
blank set-aside, in the same shape as the adjacent blank-inbox warning. Keep it a **warning**,
not a hard error — a user who never skips does not need a blocked OK, and `HardErrors()` is
for things that make the config unusable.

Do not change `Config.ResolveBeside`. Its behaviour is correct for its documented job
("where would this spelling point?"), and other callers depend on it.

### Also required — the knock-on

`Scanner.DeferredSummary` (`Scanner.cs:78-90`) uses `Directory.GetFiles(folder)` with **no
extension filter**, so with a blank set-aside a fresh install reports
*"N set-aside files waiting"* counting `config.json`, `names.txt` and `history.sqlite`. With
Core refusing a blank deferred, that path is no longer reachable *through Skip* — but
`ShellViewModel.cs:535` still calls `DeferredSummary` on the resolved path.

Make `DeferredSummary` return `new DeferredInfo(0, 0)` for a blank `folder` **before**
resolution reaches it, or have its caller not resolve a blank value. Pick whichever keeps the
"never throws, problems come back as data" contract the file already documents, and say which
you chose and why.

### Tests

`tests/OrdoSort.Core.Tests/SessionDeferredResolutionTests.cs` currently has exactly two tests,
**both with a non-empty deferred value** — nothing anywhere passes a blank one. Add:

- Skip with `Deferred = ""` throws `CommitError`, **and the document is still in the inbox**
  afterwards (assert the file is where it started — the throw alone is not the requirement).
- Skip with `Deferred = "   "` (whitespace) behaves identically.
- `DeferredSummary("")` returns count 0.
- In the Wpf tests, `Warnings()` with a blank Deferred contains a warning mentioning the
  set-aside folder.

Watch each fail first. The Core ones fail loudly today by moving the file into the config
directory — **assert on the file's location, so the red phase shows the actual misfile.**

---

## Task 4 — A move that didn't move is not a success (QC-03)

### The defect

`Commit.cs:28-41`, `MoveNeverOverwrite`:

```csharp
File.Move(src, target);   // .NET Move does not overwrite by default
```

A non-throwing return is treated as proof the document moved. Grepped: the only
`File.Exists(src)` calls in `Commit.cs` are the *pre*-checks at `:46` and `:82`. Nothing
re-checks the source after the move, anywhere in `Commit.cs` or `Session.cs`.

.NET's `File.Move` is `MoveFileExW` with `MOVEFILE_COPY_ALLOWED`, documented as: *"If the file
is successfully copied to a different volume and the original file is unable to be deleted,
the function succeeds leaving the source file intact."* Inbox on one share and routes on
another is the normal deployment here, and this repo ships `docs/RemoveReadOnly.ps1`, a bulk
read-only stripper, so read-only sources demonstrably occur.

When it happens: UI says *"✓ Filed"*, a history row is written, `Pos++`, and the original is
still in the inbox — and `Commit.cs:145`'s `if (File.Exists(originalPath))` guard then makes
**Undo refuse**, so the user has two copies and no way back through the app.

### Required

After `File.Move(src, target)` returns, verify the source is gone:

```csharp
File.Move(src, target);
if (File.Exists(src))
    throw new CommitError(...);
```

Message requirements: name the file, and say plainly that a copy reached the destination while
the original could not be removed, so the user knows there are now two copies and which is
which. This is a rare path — the message is the only thing the user will have to act on, so
make it complete rather than terse.

Put the check in `MoveNeverOverwrite` so both `CommitFile` and `SkipFile` inherit it, and so
the destination-side race handling above it is untouched.

Do **not** attempt to clean up the destination copy. Deleting a file that was successfully
written, on a path that is by definition already behaving unusually, risks destroying the only
good copy. Refusing loudly is the correct behaviour; say so in a comment.

### Tests

`tests/OrdoSort.Core.Tests/` — `CommitSkipFileTests.cs` or `PipelineTests.cs`, wherever fits
the existing arrangement.

The real Win32 branch needs a read-only file across two volumes and **cannot be reproduced on
one machine reliably**. Pin the seam instead: `Commit` already has `SkipRaceHookForTests`
(`Commit.cs:91`) as precedent for a test seam in this file. Add an equivalent hook that lets a
test simulate "the move returned but the source survives", and assert:

- a `CommitError` is thrown,
- its message names the file,
- nothing else advanced (no history row, no `Pos++`) — assert through `Session` if that is
  where the state lives.

**Say explicitly in your report that the Win32 branch itself is simulated, not reproduced.**
Do not claim the documented `MoveFileEx` behaviour was verified — it was not.

---

## Task 5 — Two side-file keys may not name one file (QC-08)

### The defect

`Config.cs:492-497` writes destinations → monitored-folders → alerts in sequence, and
`WriteDoc` → `WriteJson` → `WriteAtomic` (`:617-621`) is a **full re-serialization of one doc
type**, not a read-modify-write. Grepped: no uniqueness check across the four side-file keys
exists anywhere — only `??=` defaults (`:427-430`) and per-path confinement.

Point `monitored_folders_file` at `destinations.json`: `Save` writes `{"routes":[…]}`, then
overwrites the same file with `{"watch_folders":[…]}`. `TrySave` returns `ok = true` — both
writes genuinely succeeded as I/O. On the next `Load`, `cfg.Routes` is `[]`. Every filing
destination is gone, silently.

Reachable from the shipped app: `SettingsWindow.xaml:1566, 1585, 1604` are free-text boxes with
Browse buttons, and `PickSideFile` (`SettingsViewModel.cs:1189-1215`) validates **confinement
only**.

### Required

**Core:** reject a `Save`/`TrySave` where any two of the four resolved side-file paths
coincide, the same way `ResolveConfined` already rejects an escaping path — a `ConfigException`
naming both offending keys. Compare **resolved** paths, and compare them the way this codebase
decides path identity: `PathIdentity` (`CONTEXT.md:18-19` — "decided in exactly one place").
Do not hand-roll a string compare.

`TrySave` must surface it as a failure with a readable error, not throw past its contract —
read how it reports other failures and match.

**`Load` must NOT throw on a collision.** (Controller ruling, recorded in the ledger, amending
an earlier draft of this task.) The 2026-08-07 audit records D2 — *"a list drift refuses to
start the app"* — as an Important defect precisely because a config problem that blocks startup
leaves the user no in-app recovery. Adding a second one would regress against the spec while
fixing QC-08. `Save`/`TrySave` refusing is what prevents the data loss; `HardErrors()` is where
the user fixes it. `Load` may surface the collision (an error string, the way it surfaces other
recoverable problems) but must let the app start.

**Settings:** `HardErrors()` must catch the collision before OK is accepted, so the user is
told which two fields clash rather than getting an exception. Note the architectural reason
this was missed, recorded in the audit: each field owns an independent `DebouncedProbe` note
(`SettingsViewModel.cs:617-627`) that sees only its own value, so a between-fields constraint
has no home in that design. `HardErrors()` is the right home because it already sees the whole
form.

`box_labels_file` colliding with the other three is a milder variant (its write is gated on
`!File.Exists`) and the audit does not claim the same guaranteed loss — but include it in the
uniqueness check anyway; four keys naming three files is never intended.

### Tests

- Core: `Save` with two keys resolving to one path throws `ConfigException` naming both keys;
  `TrySave` reports failure; **round-trip**: after the refusal, the pre-existing
  `destinations.json` on disk is **unchanged** (this is the assertion that actually pins the
  data-loss, so make it explicit).
- Core: two keys with *different spellings of the same path* (`./destinations.json` vs
  `destinations.json`) are also caught — this is what makes `PathIdentity` load-bearing rather
  than decorative.
- Wpf: `HardErrors()` returns an error when two side-file fields name one file.

Watch them fail first. The red phase for the round-trip test is the good one — it shows the
routes actually being destroyed.

---

## Task 6 — The label maker stops destroying printed box numbers (QC-06, QC-07, QC-14)

Three findings, one file, one theme: `CONTEXT.md:56` names reissuing a box number already
printed on a physical box as the outcome the placement rules exist to prevent, and this window
has three separate ways to do it.

### QC-06 — an unparseable number is silently written as 1

`LabelMakerViewModel.cs:50-51`:

```csharp
DestroyDays = int.TryParse(DestroyDaysText.Trim(), out var d) ? d : 30,
NextNumber  = long.TryParse(NextNumberText.Trim(), out var n) ? n : 1,
```

A silent fallback, not a refusal. `Persist()` (`:555`) never calls `Problems()`, and
`LabelMakerWindow.xaml.cs:19` runs `Persist()` from `Closing` — on **every** close including
Esc, since the Close button is `IsCancel="True"`. For an edited row, `:671` writes
`fresh.NextNumber = edited.NextNumber` unconditionally.

Clearing the box, or typing `4,211` (default `NumberStyles.Integer` rejects a thousands
separator), takes a client from 4211 to **1** on disk. The same shape rewrites retention to 30.

**Required:** when the text does not parse, `Persist` must leave the on-disk value **alone**.
Do not invent a new number and do not write a fallback. The `_numberEdited` flag already exists
to modulate the disk-wins rule — use it.

### QC-07 — the duplicate-id refusal cannot stop the close

`LabelMakerViewModel.cs:593-602` warns *"Two clients share the id … fix the duplicate before
closing; nothing was saved."* and returns. But `LabelMakerWindow.xaml.cs:19` is:

```csharp
Closing += (_, _) => vm.Persist();     // CancelEventArgs discarded; Persist returns void
```

The window closes anyway, and because the guard returns before `BoxLabelStore.Mutate` is
entered, **every edit in the session is discarded** — including deliberate counter corrections
on unrelated clients. The message tells the user to do something they can no longer do.

**Required:** `Persist` must be able to report refusal (return a result, or expose a
`TryPersist`), and the `Closing` handler must set `e.Cancel = true` when it refuses, so the
window stays open with the edits intact and the message becomes true. Apply the same treatment
to the QC-06 refusal if you make that block the close — but prefer QC-06 leaving the disk value
alone and *not* blocking, since a stale text box is not a reason to trap the user in a window.
State which you chose.

### QC-14 — *Reset to 1* has no confirmation and no way back

`LabelMakerViewModel.cs:203-205` is one line, `s.NextNumberText = "1"`, on a chip button beside
the number box and adjacent to the "10" count preset (`LabelMakerWindow.xaml:183-190`, `:209`).
It dirties the row and sets `_numberEdited`, so `Persist` writes 1 to disk — and Esc commits
it.

`RemoveClientCommand` directly above (`:170-202`) **does** confirm, under a comment calling
removal "the one destructive act in this window", and its dialog names the number that will be
lost. Reset reaches the identical end state in one click.

**Required:** give Reset the same confirmation `RemoveClientCommand` uses, naming the current
number that will be lost. Match that dialog's wording and shape — it is the house pattern and
it is already right.

### Tests

`tests/OrdoSort.Wpf.Tests/LabelMakerViewModelTests.cs` — which currently exercises `Persist`
with valid values only (`:381-706`).

- Unparseable number text (empty, `"4,211"`, `"abc"`): after `Persist`, the store still holds
  the original number. **Assert on the store**, not on the view model.
- Same for unparseable retention text.
- Duplicate ids: `Persist` reports refusal, **and** an unrelated client's edit made in the same
  session is still pending rather than discarded.
- Reset to 1 with the confirmation declined leaves the number unchanged; accepted sets it.
  (Use the existing `IDialogService` test double — see how `RemoveClientCommand`'s tests do it.)

Watch each fail first. QC-06's red phase is the one to quote: it shows a real counter going to 1.

---

## Task 7 — Bulk Rename renames off the UI thread, and Undo survives a kill (QC-04)

### The defect

`BulkRenameViewModel.cs:135-136`:

```csharp
RenameCommand = new RelayCommand(Apply, () => _changed > 0);
UndoCommand   = new RelayCommand(UndoBatch, () => _lastOutcomes.Count > 0);
```

`RelayCommand.Execute` is `=> _execute()` (`Mvvm/RelayCommand.cs:20`) — synchronous. `Apply()`
(`:354-379`) calls `BulkRename.Execute`, a `foreach` of `File.Exists`/`File.Move`. The view
model's **only** scheduler use is `_plansProbe` (`:133-134`) — the *preview*. Its own class
comment (`:64-75`) says the preview must be off-thread because `File.Exists` on an SMB
destination is too expensive for the UI thread. The renames themselves never got that.

200 files from a share freezes the whole app with no progress and no cancel. And
`_lastOutcomes` — the only thing Undo reads — is assigned at `:368`, **after** the loop
completes, so a user who force-kills an app they believe is hung is left with files renamed on
disk and no undo path.

`AddFiles` (`:228-234`) calls `Intake.Add(..., exists: File.Exists)` synchronously too, from
all three call sites (`BulkRenameWindow.xaml.cs:31`, `:38`, `:129`).

### Required

1. Move `Apply()` and `UndoBatch()` off the UI thread through the same seam the siblings use.
   **`ZipListViewModel.RunBatchAsync` (`ZipListViewModel.cs:197-251`) is the working template**
   — `AsyncRelayCommand`, `Scheduler.Run`, a live `Status` of the form `"{verb} {i+1} of
   {n}…"`, and results marshalled back to the UI thread. Match its shape rather than inventing
   a new one.
2. Give the batch a **cancel**, as the siblings have.
3. **Make undo durable across a kill:** record each rename's outcome as it happens, not after
   the loop. `_lastOutcomes` must reflect work already done at every point during the batch, so
   an app killed mid-batch can still undo what completed. This is the part that protects the
   user's files and it is the reason this task is High, so do not skip it for the threading
   half.
4. Move `AddFiles`' intake off the UI thread as well, or say why it is safe to leave.

Everything `Apply()` currently does *after* the loop — the `_files` fixup, clearing the
operation fields, `Refresh(immediate: true)`, `RaiseCanExecuteChanged`, the `Status` line —
must still happen on the UI thread. `ZipListViewModel`'s `ApplyOnUi`/`RunOnUi` show how.

Preserve the `_lastRenderedPlans` discipline (`:355-360`): the operation executed must remain
the operation last rendered. That was a deliberate fix; do not regress it.

### Also in this task — QC-11, *Remove selected* silently removes nothing

(Controller ruling, recorded in the ledger: QC-11 was drafted as part of Task 9, and moved here
because it edits the same two files and the same command wiring this task rewrites. Splitting
them across two dispatches would guarantee a conflict inside one method.)

`ApplyPlans` (`BulkRenameViewModel.cs:322-328`) does `Preview.Clear()` on every call, and all
four of Find/Replace/Prefix/Suffix debounce into it (`:156-165`,
`UpdateSourceTrigger=PropertyChanged` at `BulkRenameWindow.xaml:84, 87, 110, 113`). `Clear()`
fires a Reset, which drops `DataGrid.SelectedItems`. `OnRemoveSelected`
(`BulkRenameWindow.xaml.cs:49-51`) reads `SelectedItems` at click time only, and
`RemoveFiles([])` (`:236-245`) is a no-op with no status change.

So: select 8 rows to drop from the batch, type one more character into "At start:", wait 300ms,
click *Remove selected* — 0 rows are removed, with no error and no visual difference from a
successful removal.

**This is FL-03, fixed in `FilenameListViewModel` on 2026-08-21 and never applied here.** Use
that fix as the template: snapshot the selection by identity before `Preview.Clear()`, restore
it after re-adding, drop rows the reproject hid, and re-apply to the grid. `RenameRow.Source` is
the identity key. See `FilenameListViewModel.cs:432`, `:437`, `:576` and the `SelectionRestored`
event the window subscribes to.

Test: selecting rows, typing into Prefix, then *Remove selected* still removes the intended
rows. Mirror `TheRowSelectionSurvivesAReprojection` in `FilenameListViewModelTests.cs:447`, which
is the strongest test in that pass — it installs a `CollectionChanged`/Reset handler that
reproduces WPF pushing an empty selection back, which is what makes it fail against unfixed code.

### Tests

`tests/OrdoSort.Wpf.Tests/` — there is an existing `BulkRenameProbeTests.cs` and the Settings
tests show the "did not block the UI thread" pattern (`SettingsViewModelTests.cs:1606` and its
"eventually reflects the real state" companion). Note the audit flags the `< 50ms` family as
vacuous in one direction, so **pair any timing assertion with a real-effect assertion**, as
those tests do.

Required:
- `Apply` does not run the file work on the calling thread — assert through the injected
  scheduler seam (a `ManualWorkScheduler` already exists in the test project), not by timing.
- `_lastOutcomes` contains the completed renames **partway through** a batch — drive it with a
  scheduler/counter seam so you can observe mid-batch state. This is the durability
  requirement; a test that only checks the end state does not pin it.
- Cancel mid-batch stops further renames and `Status` reports what actually completed.
- The `_lastRenderedPlans` behaviour still holds.

---

## Task 8 — Clear and Remove stop lying about an in-flight batch (QC-05)

### The defect

`ZipListViewModel.Cancel()` (`:271`) is called from exactly one place in the app:
`ZipToolsWindow.xaml.cs:68`, the `OnClosed` handler. Neither `ClearCommand` (`:111-117`) nor
`RemoveSelected` (`:179-183`) touches `_cts`. `RunBatchAsync` snapshots `pending` at `:204`
before the loop; Unlock's `UnlockAsync` snapshots `rows` at `UnlockViewModel.cs:800`. Neither
Clear nor Remove is gated on a busy flag.

So: remove a row mid-run in Unlock and the loop still archives that file's original and writes
the unlocked file. Click Clear and `Summary`/`ResultLines` wipe, then silently repopulate
seconds later with results for files the user just cleared.

**The telling detail:** Unlock's `ClearCommand` (`:232-246`) carries a four-line comment
reasoning carefully about cancelling the **probe** token so a stale probe cannot update an
invisible row — and never cancels the **run**.

### Required

For both `ZipListViewModel` and `UnlockViewModel`:

- `Clear` must cancel the running batch (the run token, not only the probe token) — and, as
  Unlock's probe comment already establishes as the house pattern, hand out a **fresh** token
  afterwards so the next run is not born cancelled.
- `Remove selected` must either be disabled while a batch runs, or remove the row from the
  work the loop will still do. Pick one and say which; disabling is simpler and matches the
  "buttons that cannot act are disabled" direction the FL audit's FL-14 points at, but removing
  from the live set is friendlier. Either is acceptable — silently diverging is not.
- A batch that was cancelled this way must leave `Status`/`Summary` telling the truth about
  what actually completed, not a stale total.

### Tests

`tests/OrdoSort.Wpf.Tests/` — for each of the two view models:

- Clear during a run cancels it: the loop stops, and no result line appears for a row that was
  cleared.
- Clear during a run does not leave `Summary` repopulating after the wipe — assert the
  post-cancel state is the cleared state.
- Remove selected during a run: the removed row is not processed (or the command is disabled —
  assert whichever you implemented).
- Clear when nothing is running still works, and a subsequent run is not born cancelled
  (the fresh-token requirement).

Use the existing scheduler/dialog test doubles. Watch each fail first — the Unlock red phase
should show a removed row still being processed.

---

## Task 9 — The remaining propagation fixes (QC-12, QC-13, QC-16)

Three small independent fixes. Each has a correct implementation already in this repo, one file
over. Batch them; one commit is fine.

**QC-11 was drafted here and has moved into Task 7** (controller ruling, in the ledger) — it
edits the same files Task 7 rewrites.

**Task 3 has already edited `Scanner.DeferredSummary` before you** (controller ruling): it makes
a blank `folder` return `new DeferredInfo(0, 0)` before resolution. **Preserve that guard.** Your
QC-13 change alters `DeferredInfo`'s shape, so Task 3's `DeferredSummary("")` test must be
*updated* to the new shape, never deleted.

### QC-12 — `Session.Extend` decides path identity with a raw case-sensitive `HashSet`

`Session.cs:88-94`:

```csharp
var known = new HashSet<string>(Queue);   // default comparer: ordinal, case-SENSITIVE
```

`CONTEXT.md:18-19` is explicit: path identity is decided in exactly one place, `PathIdentity`.
This is a raw string compare in the movement spine, and it is stricter than even the
pre-`PathIdentity` code it replaced.

**Required:** route it through `PathIdentity`. A `HashSet<string>` with an
`IEqualityComparer<string>` backed by `PathIdentity`'s canonical form is the natural shape; if
`PathIdentity` does not already expose a comparer, adding one there is correct — it keeps the
decision in the one place `CONTEXT.md` says owns it. Do not add an `OrdinalIgnoreCase`
`HashSet` and call it done; that fixes half and re-introduces a second answer to the question.

### QC-13 — the set-aside banner can report a ~155,000-day "oldest"

`Scanner.cs:17-20`:

```csharp
private static long SafeMtime(string p)
{ try { return new FileInfo(p).LastWriteTimeUtc.Ticks; } catch { return 0; } }
```

`FileInfo.LastWriteTimeUtc` does **not** throw for a file that has gone — it returns
1601-01-01 UTC. So the `catch` never runs, and `files.Min(SafeMtime)` (`:85`) latches 1601:
one vanished file poisons the age for the whole folder. `Math.Max(0, …)` clamps only the low
end.

**The house already decided this correctly one module over** —
`docs/superpowers/specs/2026-08-19-filename-list-upgrade-design.md:108-115` chose nullable
`Size`/`Modified` explicitly *"rather than lying with 0 bytes or a 1601 date"*.

**Required:** make an unreadable/vanished file contribute **nothing** to the oldest-age
calculation rather than contributing 1601 — and if *every* file is unreadable, represent the
age as unknown rather than as a number. Follow the nullable direction that spec chose. Update
`DeferredInfo` and its `ShellViewModel` consumers (`:627-632`, `:648-653`) to render "unknown"
rather than a number when there is no answer.

Same root reaches `Scanner.Scan`'s `mtime_asc`/`mtime_desc` sorts (`:55-56`) — a file that
vanishes mid-scan sorts to the very front. Fix it there too, or say why not.

### QC-16 — a roster with non-breaking spaces matches nobody

`MatchMerge.Norm` (`MatchMerge.cs:26-28`) splits on the single char `' '`; `CleanTokens`
(`:173-176`) splits on `'-'`, `'_'`, `' '`. A cell pasted from a web portal as
`SMITH JOHN` becomes one token: the exact key never matches, and the token pass cannot
reach `substantial >= 2`, so no suggestion is produced either. Every file lands `no_match`
while the status line reports *"Roster loaded: 412 people."*

**`MatchMergeViewModel.Tokenize` (`:242-249`) already does this correctly** — it deliberately
treats "any Unicode whitespace, not just ASCII space" as a separator, with a comment saying so.
That fix was applied to *headers* only; the data path never got it.

**Required:** make `Norm` and `CleanTokens` split on any Unicode whitespace, matching
`Tokenize`. Keep the existing `ToUpperInvariant`/trim behaviour — only whitespace handling
changes.

### Tests

One per fix, each watched failing first:

- QC-12: a case-only-different path is treated as already known by `Extend` and does not
  produce a duplicate queue entry.
- QC-13: a folder whose only file vanished reports age unknown, not ~155,000 days; a folder
  with one readable and one vanished file reports the readable file's age.
- QC-16: a roster cell containing ` ` matches the same document an ASCII-space cell
  matches. Use the literal escape in the test so the intent is visible.

---

## Definition of done for batch A

- All nine tasks complete, each reviewed.
- Full suite green on the documented command, with the `Passed!` counts quoted and ≥ baseline
  (Core 660, Wpf 1764) plus the added tests.
- `e2e.bat` still passes 38/12 — run it once at the end, not per task.
- Every new test has its red-phase output quoted in the task's report.
- Any genuine overflow defect surfaced by Task 1 is reported, not suppressed.
