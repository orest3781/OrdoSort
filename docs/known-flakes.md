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

**Then prove it's non-deterministic.** A test is a flake only if it passes on a re-run of the *identical binary*. If it fails twice, it is a defect. Run it in isolation too — most flakes here are parallel-schedule interference and pass alone.

---

## Live

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
