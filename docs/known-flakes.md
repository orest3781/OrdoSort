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

### `SettingsViewModelTests.ValidateRouteProbeRunsOncePerPauseNotPerKeystroke`
`tests/OrdoSort.Wpf.Tests/SettingsViewModelTests.cs:1646` · observed 2026-08-15

Sleeps 350ms and then asserts an **exact** debounce call count, so a loaded parallel run can straddle the debounce window and see 2 calls instead of 1. Two independent clocks, which is the same shape PR #3 fixed elsewhere.

Failed once in a full-solution run, then passed 3/3 in isolation and 1739/1739 on a re-run of the identical binary.

Precisely the wall-clock-assertion trap `docs/superpowers/plans/2026-08-05-history-indexes.md:23` warns against. The durable fix is a seam that reports the probe firing, so the loop's own counter is the only signal — a change to the test, not to the code under it. `Config.OnRetryForTests` (PR #3) is the pattern to copy.

### `FocusRingCoverageTests.TabItemShowsTheBronzeFocusRing`
`tests/OrdoSort.Wpf.Tests/FocusRingCoverageTests.cs:403` · listed 2026-08-09, **unconfirmed**

Carried on the 2026-08-09 list with **no mechanism ever recorded**, and it is not obvious from the test: it renders a real TabControl and compares a before/after pixel differential on the second, unselected tab.

Did not reproduce in four full-suite runs on 2026-08-15. Kept here rather than dropped because absence over four runs is not proof, but treat the entry as unverified — if you see it fail, **record the actual failure message**, which is what this entry has always been missing.

---

## Environment-dependent — not flakes

### `WebViewPdfViewerGuardBehaviourTests` (all 5)
`tests/OrdoSort.Wpf.Tests/ViewerNavigationPolicyTests.cs:107`

All five fail together with COM `Class not registered` when the **WebView2 runtime is absent or unregistered** on the machine. That is deterministic per machine, not random: they fail every run where the runtime is missing and pass every run where it is present. Filed here only because the failure looks alarming and gets mistaken for a flake.

They passed in all four runs on 2026-08-15, so the runtime is registered on this machine. There is no `Skip` guard on them; `4c8fe04` made the *product* report a missing runtime, which did not change these tests.

---

## Fixed — do not re-add

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
