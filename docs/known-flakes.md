# Known test flakes

**The standing rule: report, never chase, never weaken.** A flake is reported in the run summary and left alone. It is never "fixed" by loosening an assertion, adding a retry, or raising a timeout — that converts a noisy test into a silent one. A flake is only ever closed by making the test *deterministic by construction*, which usually means asserting on a seam instead of on elapsed time.

This file is the canonical list. It supersedes the ones in `docs/superpowers/plans/2026-08-09-v1-release-blockers.md` and `docs/superpowers/audits/2026-08-09-v1-release-audit-tests-build.md`, both of which are dated artifacts that record what was true when they were written and are not edited afterwards.

---

## Before you call something a flake

**Run the suite the right way.** Plain `dotnet test` has been observed to skip the entire WPF assembly and still exit 0, because Smart App Control blocks the test assembly by hash:

```
dotnet build OrdoSort.sln -t:Rebuild -p:Deterministic=false -v minimal
dotnet test OrdoSort.sln --no-build -v minimal
```

`-p:Deterministic=false` is load-bearing — do not "fix" it. The 2026-08-09 audit found the skip did *not* reproduce on that machine, so treat it as machine-state-dependent rather than universal: keep the explicit rebuild, and **always read the `Passed!` line and its count**. An exit code of 0 is not evidence that anything ran.

**Baseline as of 2026-08-15** (`main` at `40b6eba`): **Core 661, Wpf 1738.**

**Baseline as of 2026-08-22** (`fix/app-qc-2026-08-21`, the QC batch-A fix pass):
**Core 685, Wpf 1797.** Measured on `main` at `66be355` immediately before that pass:
**Core 660, Wpf 1764.**

Note the Core count is **one lower** than the 2026-08-15 line above, and that difference
cannot be explained from this repository: `40b6eba` is not reachable here — it predates
the 2026-08-20 history rebuild — so the commits between the two measurements are gone.
Recorded as unexplained rather than guessed at. Anyone with the pre-rebuild bundle could
settle it; absent that, treat 660 as the real floor for `66be355` and do not read the
2026-08-15 line as evidence a test was lost.

**Then prove it's non-deterministic.** A test is a flake only if it passes on a re-run of the *identical binary*. If it fails twice, it is a defect. Run it in isolation too — most flakes here are parallel-schedule interference and pass alone.

---

## Live

### `TilePreviewProbeTests.EditingANonSelectedWatchFolderNeverProbesAtAll`
`tests/OrdoSort.Wpf.Tests/TilePreviewProbeTests.cs` · listed 2026-08-22, **observed once**

`Assert.Equal() Failure: Expected: 1, Actual: 2` — one more probe landed than the test
expects. Seen during a full-solution run on 2026-08-21, in a task that changed only
`src/OrdoSort.Core/Commit.cs` and its Core tests — nothing in the WPF layer.

**Evidence it is non-deterministic:** the changes were stashed, the tree rebuilt on
unmodified code, and the test run three times in isolation — it passed all three
(`Passed! - Failed: 0, Passed: 4 … Duration: 26-27 ms`). The changes were then restored
and the full suite re-run: clean.

Mechanism unknown. An extra probe landing suggests a debounced probe from an adjacent
test's fixture arriving late under parallel load, which would make it the same
suite-load interference family as the entries below — but that is a hypothesis, not a
diagnosis, and nobody has reproduced it deliberately.

**Why it is being written down now:** this test was already believed to be flaky from an
earlier pass, but that belief lived only in a working note and never reached this file —
which is why the 2026-08-21 implementer that hit it had to spend a full stash-rebuild-
isolate cycle re-deriving what someone already knew. That cost is the reason this file
exists. The durable fix is to assert on the probe seam rather than a call count.

### `FocusRingCoverageTests.TabItemShowsTheBronzeFocusRing`
`tests/OrdoSort.Wpf.Tests/FocusRingCoverageTests.cs:403` · listed 2026-08-09, **unconfirmed**

Carried on the 2026-08-09 list with **no mechanism ever recorded**. Did not reproduce in nine full-suite runs on 2026-08-15.

**Ruled out — the theme race.** The obvious suspicion was this repo's recurring defect shape: process-wide mutable state plus xUnit's parallel classes (three instances are described in PR #6). The test does mutate app-wide state — `ThemeManager.Apply` on the shared `App`, and `KeyboardNavigation.AlwaysShowFocusVisual` via the `FocusVisualsEnabled` helper. But **every** test class that mutates the theme was checked: all 19 that call `ThemeManager.Apply`, plus `ThemeManagerSetModeTests` which goes through `SetMode`, are in the `HighlightContrastTests` collection and therefore serialized. They cannot interleave, so this is not the mechanism.

**Leading hypothesis — a foreground steal, not the test suite.** What remains environmental in the assertion chain is real keyboard focus: `target.Focus()` and `IsKeyboardFocused`. Those depend on OS focus state, which anything on the desktop can take — a notification, another window activating, someone launching the app under test. That fits a failure which is rare, unreproducible in isolation, and indifferent to parallel load.

**So when it fails, the failure message decides it.** The assertions carry distinct text:

| message | meaning |
|---|---|
| `never accepted keyboard focus` / `IsKeyboardFocused is false after Focus()` | focus was stolen — the hypothesis above |
| `pixels already in the band BEFORE it was focused` | the tab under test became selected; selected tabs paint a 2px AccentBronze underline, which is why this case deliberately focuses the second, UNSELECTED tab |
| `NO AccentBronze pixel appears in the ring band` | a real theme/style regression, not a flake — do not dismiss it |
| `WPF added no focus-visual adorner at all` | the `AlwaysShowFocusVisual` plumbing broke |

Kept rather than dropped: nine clean runs are not proof. But it is no longer an entry with nothing in it — the next failure has somewhere to land.

---

## Environment-dependent — not flakes

### The WPF test host can crash on WebView2 teardown, aborting the whole run
Observed 2026-08-21 mid-run at 1298 Wpf tests:

```
Test host process crashed : [ERROR:ui\gfx\win\window_impl.cc:172]
Failed to unregister class Chrome_WidgetWin_0. Error = 1411
```

The identical binaries then passed 1789/1789, and the two runs after that were clean.
The task that hit it touched no WebView2 code.

**This is the reason the `Passed!` rule at the top of this file is not pedantry.** A run
that aborts this way can present as a non-zero exit with no failing test, or — worse —
be mistaken for a completed run. Read the `Passed!` line and its count; if there isn't
one, the run did not finish, whatever the exit code says.

Related, same session: builds were repeatedly blocked by Smart App Control by hash (five
different assemblies at various points), each time producing a **zero-test run**, always
cleared by a full `bin`/`obj` clean and rebuild. And a full run once hung past ten
minutes on ~19 stray `dotnet.exe`/`testhost.exe` processes left from earlier cycles —
if a run hangs, check for and kill those before diagnosing anything else.


### `WebViewPdfViewerGuardBehaviourTests` (all 5)
`tests/OrdoSort.Wpf.Tests/ViewerNavigationPolicyTests.cs:107`

All five fail together with COM `Class not registered` when the **WebView2 runtime is absent or unregistered** on the machine. That is deterministic per machine, not random: they fail every run where the runtime is missing and pass every run where it is present. Filed here only because the failure looks alarming and gets mistaken for a flake.

They passed in all four runs on 2026-08-15, so the runtime is registered on this machine. There is no `Skip` guard on them; `4c8fe04` made the *product* report a missing runtime, which did not change these tests.

---

## Fixed — do not re-add

### `SettingsViewModelTests.ValidateRouteProbeRunsOncePerPauseNotPerKeystroke`
`tests/OrdoSort.Wpf.Tests/SettingsViewModelTests.cs:1646` · observed 2026-08-15, fixed same day

It typed a ~70-character burst at the **production** 300ms debounce and then asserted an exact call count after a `Thread.Sleep(350)` — two independent clocks. `DebouncedProbe.Trigger` re-arms one shared Timer per keystroke, so `calls == 1` held only if every assignment landed within 300ms of the previous one; one gap over 300ms on a loaded parallel run fired the timer mid-burst and the count became 2.

Fixed by giving the test both clocks instead of racing them, with **no production change**: `probeDelayMs` (already a ctor param threaded into every `RouteEditVm`) widens the debounce window past any possible burst, so the burst asserts **zero** probes — strictly tighter than the old total-only check, which tolerated a mid-burst fire — and the pause is then caused explicitly via the existing `RefreshProblem(immediate: true)` rather than waited out. No `Thread.Sleep` remains.

Revert-proof: with `DebouncedProbe.Trigger` mutated to run `compute` inline on every call, it fails the burst assertion with **78** calls instead of 0. 25/25 in a loop, plus a clean full-solution run.

### `UnlockProbeWritesNothingTests.NothingChangesInTheFixtureDirectoryOrTemp`
`tests/OrdoSort.Core.Tests/UnlockProbeWritesNothingTests.cs:83` · fixed `cd331ef`, 2026-08-12

It snapshots `ordosort_*` in `Path.GetTempPath()` before and after, while `UnlockNeverOverwritesTests`/`UnlockTests` force the streaming unlock path, which writes its working copy into that same directory. A concurrent streamed unlock landed a file inside the window and this test failed on someone else's write.

Fixed by `[Collection(UnlockNeverOverwritesTests.Name)]`, pinned by `UnlockThresholdTestCollectionMembershipTests` so the membership cannot silently lapse. It was still on the 2026-08-09 list when this file was written, three days after the fix — which is the reason this file exists.

---

## Adding an entry

Record the **failure message**, not just the test name. An entry that says only "this one flakes" cannot be acted on later, as the focus-ring entry above demonstrates. Include:

- test name and `file:line`
- the date observed, and what the surrounding run was doing (full solution? parallel? loaded machine?)
- the actual assertion failure — expected vs actual
- the evidence it is non-deterministic — isolation runs, re-runs of the same binary
- the mechanism, if known, and what the durable fix would be

When a flake is fixed, **move it to "Fixed"** with the commit that did it rather than deleting it, so the same test does not get re-added from an older list.
