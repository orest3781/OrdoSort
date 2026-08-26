# Two zip windows

**Date:** 2026-08-25
**Status:** approved, ready for planning

## Problem

A zip dropped on the zip tools window lands in whichever tab happens to be
selected, and both tabs accept it. The mistake is silent and the recovery is
manual.

The routing is a single branch in `ZipToolsWindow.OnDrop`:

```csharp
if (ReferenceEquals(Tabs.SelectedItem, MergePdfsTab)) _ = _vm.MergePdfs.AddPaths(paths);
else                                                  _ = _vm.ZipExtract.AddPaths(paths);
```

`Drop` is handled on the **Window**, not on either list, and three things
follow from that:

- **Aiming at a tab does not select it.** A zip dropped directly onto the
  *Merge PDFs* tab header, while *Zip & unzip* is showing, is added to *Zip &
  unzip*. The person aimed at the destination they wanted and got the other
  one. Nothing switches tabs on drag-hover, so the header is a target that
  looks live and is not.
- **Nothing rejects the stray row.** A zip is valid input to both lists —
  `ZipExtractViewModel.Extensions` is `null` (anything that exists) and
  `MergePdfsViewModel.Extensions` is `{zip}`. So the wrong-tab drop produces
  no error, no note, no visible difference from a correct one.
- **The counts absorb it quietly.** `Extract 2 zips` becomes `Extract 3 zips`
  and reads as if it were intended. The button label, which exists precisely
  so an action states its own scope, states a scope the person did not choose.

The `OnDrop` doc comment defends the design as it stands — *"a drop lands on
whichever tab is showing — the tab is the statement of intent, so routing
anywhere else would silently put the files in a list the person is not looking
at"* — and it is right about the alternative it rejects. The flaw is upstream
of the choice: a tab is being asked to carry a statement of intent while the
drop target is the whole window, so the intent is read from a selection made
before the drag started, not from where the mouse was released.

### This is not a failure of the 2026-08-18 merge

That work chose the tab split deliberately, and for a reason which still
holds: *"it removes the only genuine ambiguity in an auto-detecting surface —
drop a zip, did you mean extract or merge? — by having the answer already
given."* An earlier draft folded all three actions onto one list and had to
accept that extracting an archive left *Merge PDFs* dead for it until the list
was cleared.

The reasoning was sound. The container was too weak to carry it. Separating
the two jobs so a zip's destination is unambiguous is exactly right — but a
tab puts both destinations inside one droppable window and picks between them
by selection state. Two windows make the same separation physical.

Note also that both tabs' empty states already say *"Drag ... anywhere on this
window"*. Both say it, about the same window. The copy was accurate and the
arrangement made it ambiguous.

## Goal

Keep the separation the tab split was introduced for, and put it in a
container that cannot be dropped past: one job per window, one list per
window, no routing decision left to get wrong.

## Approach

Split `ZipToolsWindow` into two single-purpose windows and delete the
`TabControl`.

**`ZipToolsWindow`** keeps the *Zip & unzip* job and becomes what that tab
already was: one list holding files, folders and archives, with the buttons
lighting from the contents. Its `DataContext` becomes `ZipExtractViewModel`
directly.

**`MergePdfsWindow`** is new and takes the *Merge PDFs* job unchanged: its own
list, zips-only intake, its own Tools entry. Its `DataContext` is
`MergePdfsViewModel` directly.

`OnDrop`'s branch is deleted rather than improved. With one list per window
the destination is the window the drop landed on, so there is no affordance to
add — no drag-hover tab switching, no per-list highlight, no confirmation
prompt — because there is no ambiguity left for an affordance to resolve.

### What this does and does not undo

It undoes the `TabControl` and nothing else. The 2026-08-18 merge's real work
was deleting three near-clone view models and three near-clone windows by
extracting `ZipListViewModel` — intake, dedupe, remove, clear, notes, the
cancellable batch runner. That base class is untouched here, and it is exactly
what makes a second window cheap: `MergePdfsWindow` is a thin shell over
machinery that already exists and is already tested. The duplication that
merge existed to delete stays deleted.

Two alternatives were rejected:

- **Fix the aim, keep the tabs.** Move `Drop` onto each tab's list, highlight
  the list under the cursor, make tab headers real drop targets, switch tabs
  on drag-hover. This makes the mistake harder without making it impossible —
  two lists still sit in one window and a correctly-aimed drop into the wrong
  mental model still succeeds silently. It also spends four pieces of
  affordance machinery compensating for a routing decision that does not need
  to exist.
- **One list, per-action outcome per row.** Fold Extract and Merge onto a
  single list, tracking which actions a row has already had so extracting an
  archive does not retire it for merging. This reopens the problem the
  2026-08-18 spec documented rejecting, and pays new per-row state to re-solve
  it. Shown both, the user chose to keep the jobs apart.

**The Core engines do not change.** `Zipper.CreateZip`, `Zipper.Extract` and
`ZipMerge.MergeZip` keep their signatures and behaviour; `ZipperTests` and
`ZipMergeTests` are untouched. So do both view models' own behaviours. This is
a container change, which is what keeps its risk proportionate to its reach.

## Behaviour

### `ZipToolsWindow` — Zip and unzip

Unchanged from today's *Zip & unzip* tab, promoted to the whole window.

```
[ Zip 5 items ]  [ Zip to... ]  [ Extract 2 zips ]                  [ Close ]
```

| Action | Enabled when | Acts on |
|---|---|---|
| Zip, Zip to... | the list is not empty | every row |
| Extract | at least one zip row is `Pending` | pending zip rows |

Permissive intake (`Extensions` is `null`), `AddNote` noun "item", Zip folding
the whole list, Extract mapping each pending archive to its own sibling
folder, Extract cancellable and Zip not — all unchanged.

### `MergePdfsWindow` — Merge PDFs from zips

Unchanged from today's *Merge PDFs* tab, promoted to its own window.

```
[ Add zips... ]  [ Remove selected ]  [ Clear ]
...
[ Merge 3 zips ]                                                    [ Close ]
```

Zips-only intake and its "isn't a zip" rejection stay correct here, on a
window that can only merge. Pending-only processing unchanged: merging twice
leaves already-merged rows alone.

### Shared surface details

- Both windows: 700x520, min 580x420 — today's dimensions, kept for both so
  neither has to be re-tuned against `WindowOverflowTests` at the large font
  presets.
- Both keep `AllowDrop` at window level. That is now unambiguous rather than
  merely convenient, and keeps the existing empty-state copy — *"Drag files,
  folders or zips anywhere on this window"* / *"Drag zips anywhere on this
  window"* — true of exactly one list each.
- Each window owns its own view model and cancels it in `OnClosed`, replacing
  the forwarding `ZipToolsViewModel.Cancel` did.
- Window titles: "OrdoSort — Zip and unzip" (unchanged) and "OrdoSort — Merge
  PDFs from zips".

### Tools menu

`Merge _PDFs from zips...` is added directly below `_Zip and unzip...`, in the
same group after the last separator, since both consume archives.

`P` is the accelerator. The menu already uses `U B M X F C L Z`, so `M` for
"Merge" would collide with `_Match and merge...` — a genuinely confusable
neighbour, being the app's other merge tool and a different job entirely.

Icon: `&#xE8A5;` (document), pairing with `&#xE8B7;` on the zip entry.

## Structure

### Files

| File | Change |
|---|---|
| `src/OrdoSort.Wpf/Windows/ZipToolsWindow.xaml` | Delete `TabControl`, both `TabItem`s and the swapping footer. Promote the Zip & unzip grid and its toolbar to the window body; footer holds Zip / Zip to... / Extract / Close. |
| `src/OrdoSort.Wpf/Windows/ZipToolsWindow.xaml.cs` | `DataContext` is `ZipExtractViewModel`. `OnDrop` loses its branch. `OnAddZips` / `OnRemoveSelectedMerge` move to the new window. `OnClosed` cancels its own VM. |
| `src/OrdoSort.Wpf/Windows/MergePdfsWindow.xaml` | **New.** The Merge PDFs tab's grid, toolbar and footer as a window. |
| `src/OrdoSort.Wpf/Windows/MergePdfsWindow.xaml.cs` | **New.** `OnAddZips`, `OnRemoveSelected`, `OnDragOver`, `OnDrop`, `OnClosed`, plus `DataGridColumnCap.Track(ZipsGrid, ZipsResultColumn)`. |
| `src/OrdoSort.Wpf/ViewModels/ZipToolsViewModel.cs` | **Deleted.** Its only job was holding the two tab view models and forwarding `Cancel`. |
| `src/OrdoSort.Wpf/ViewModels/ZipExtractViewModel.cs` | Doc comment only — it is no longer "the Zip & unzip tab". |
| `src/OrdoSort.Wpf/ViewModels/MergePdfsViewModel.cs` | Doc comment, plus dropping the unused `dialogs` parameter kept "for ctor-shape consistency with the sibling tab" — there is no sibling tab now. |
| `src/OrdoSort.Wpf/ViewModels/ZipListViewModel.cs` | **Untouched.** |
| `src/OrdoSort.Wpf/MainWindow.xaml` | Add the `Merge _PDFs from zips...` item. |
| `src/OrdoSort.Wpf/MainWindow.xaml.cs` | `OnZipTools` constructs `ZipExtractViewModel`; add `OnMergePdfs`. |
| `src/OrdoSort.Core/Zipper.cs`, `ZipMerge.cs` | **Untouched.** |

`MergePdfsViewModel`'s constructor currently takes an `IDialogService` it does
not use and discards with `_ = dialogs`, justified by symmetry with the tab
beside it. That justification dies with the tab, so the parameter goes with
it — one fewer thing for a caller to supply and be wrong about.

## Testing

### The regression pin

The defect is "a drop can reach a list you did not aim at". The test that
holds it shut asserts the structural fact that makes it impossible, in both
windows: the window contains **zero** `TabControl`s and exactly one
`DataGrid`, and a `FileDrop` of a `.zip` adds one row to that one list.

A count assertion, not a "the right list got it" assertion — with one list
those are the same claim, and the count is the one that keeps failing if a
second list is ever reintroduced.

### View models

`ZipExtractViewModelTests`, `MergePdfsViewModelTests`, `ZipListClearAndRemoveTests`
and `ZipItemRowTests` are unaffected: they exercise view models, and no view
model behaviour changes. `MergePdfsViewModelTests` needs only its constructor
calls updated for the dropped `dialogs` parameter.

### Window tests

`ZipToolsWindowTests.FooterActionsFollowTheSelectedTab` is **deleted**, not
ported. It exists solely to prove the footer swaps with the selected tab —
including its measured note that a broken `ElementName` binding silently
stacks both footers in one Grid cell. Both the machinery and its guard go.
Deleting a passing test is correct here precisely because the behaviour it
defends is the behaviour being removed.

`MergePdfsWindowTests` is new, covering construction, the grid binding to
`Rows`, and `DataGridColumnCap` tracking on the Result column.

### Registries

`MergePdfsWindow` must be added to the four suites that enumerate windows, or
it silently gets none of their coverage:

- `AutoFitColumnTests`
- `DataGridSelectionContrastTests`
- `DataGridWindowCoverageTests`
- `WindowOverflowTests`

Adding it there is most of what proves the new window is a first-class citizen
rather than a copy that drifted.

### End to end

- `ZipMergeScenarios` retargets from the tab to `MergePdfsWindow`.
- `ZipScenarios` and `UnzipScenarios` drop their tab selection step.
- `E2ERunner` / `ScenarioKit` window construction updated for both.

Scenario names and counts do not change: the same twelve surfaces and the same
demonstrations, driven through two windows instead of two tabs.

## Docs

- `README.md:117` currently reads *"Zip and unzip — one window, two tabs; the
  second merges the PDFs held inside an archive."* That becomes two entries,
  and the Tools list goes from eight to nine.
- `CONTEXT.md` needs **no** change — checked, not assumed. Its only zip
  mentions are `ZipMerge.MergeZipCore` and `Zipper` in the created-by-me gate
  discussion, and both engines are untouched.
- The 2026-08-18 spec is **not** edited. It records a decision that was
  correct on its evidence; this document supersedes its container choice and
  says why.

## Out of scope

- **Renaming `ZipToolsWindow`.** It holds one tool now, so the name is
  slightly stale, but renaming touches 14 files for nothing a user sees.
- Any Core engine change.
- Drag-over highlighting, drag-hover tab switching, drop confirmation — all
  rejected above as affordances for an ambiguity this design removes.
- The capability gaps found while surveying: no destination picker for
  Extract, no way to browse inside an archive, no selective extraction, no
  encrypted archives, no compression level, no per-archive progress. Each is a
  separate piece of work with its own justification to make.
