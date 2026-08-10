using OrdoSort.Core;
using OrdoSort.Wpf.Services;
using OrdoSort.Wpf.ViewModels;

namespace OrdoSort.Wpf.Tests;

/// <summary>Task 2 of the 2026-08-08 unlock-readiness-probe plan: per-file
/// readiness in the Unlock window. These are pure ViewModel-level tests (no
/// WPF window needed — UnlockFileRow/UnlockViewModel are plain C#) covering
/// Step 6's list: the probe runs on drop, each row shows the right state per
/// outcome, a not-protected file doesn't nag, verdicts don't survive a
/// saved-password change, a probe in flight doesn't corrupt a concurrent
/// add/re-probe, and cancellation leaves nothing running. Every test injects
/// the <c>probe:</c> seam so the outcome under test is deterministic —
/// exercising the real Unlock.ProbeReadiness end to end is Task 1's job
/// (UnlockProbeAgreementTests), not this file's.
///
/// All assertions are on OBSERVABLE STATE (row.Status / row.DisplayText /
/// row.ToolTipText / vm.Files), never on whether the probe delegate was
/// invoked a particular number of times — the pattern this repo calls out as
/// "tests passing for the wrong reason". Step 8's teeth proof (breaking the
/// probe-to-row wiring and confirming these fail because the STATE is wrong)
/// is recorded in the Task 2 report, not encoded here.</summary>
public class UnlockReadinessProbeTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "ordoprobe_" + Guid.NewGuid());

    public UnlockReadinessProbeTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { /* best effort */ }
    }

    /// <summary>A real file needs to exist on disk for AddFilesAsync to keep
    /// it at all (File.Exists gates the add) — content is irrelevant here
    /// since every test below injects its own probe delegate instead of
    /// touching PdfSharp.</summary>
    private string Touch(string name)
    {
        var p = Path.Combine(_dir, name);
        File.WriteAllText(p, "x");
        return p;
    }

    // ---------------------------------------------------------- per-outcome

    [Theory]
    [InlineData("not_encrypted", ReadinessStatus.NotEncrypted, "")]
    [InlineData("ready", ReadinessStatus.Ready, "  —  a saved password opens this")]
    [InlineData("needs_password", ReadinessStatus.NeedsPassword, "  —  needs a password")]
    [InlineData("in_use", ReadinessStatus.InUse, "  —  in use, couldn't check")]
    [InlineData("unreadable", ReadinessStatus.Unreadable, "  —  couldn't be read")]
    public async Task TheProbeRunsOnDropAndEachOutcomeProducesTheRightRowState(
        string coreStatus, ReadinessStatus expected, string suffix)
    {
        var file = Touch("doc.pdf");
        var vm = new UnlockViewModel(new Config(), () => true,
            probe: (path, candidates) => new Unlock.ProbeResult(coreStatus, path,
                MatchedIndex: coreStatus == "ready" ? 0 : null, Message: $"core said {coreStatus}"));

        await vm.AddFilesAsync(new[] { file });

        var row = Assert.Single(vm.Files);
        Assert.Equal(expected, row.Status);
        Assert.Equal("doc.pdf" + suffix, row.DisplayText);
    }

    [Fact]
    public async Task ANotProtectedFileStaysQuietInBothTheTextAndTheTooltip()
    {
        var file = Touch("plain.pdf");
        var vm = new UnlockViewModel(new Config(), () => true,
            probe: (path, candidates) =>
                new Unlock.ProbeResult("not_encrypted", path, Message: "This PDF isn't password-protected."));

        await vm.AddFilesAsync(new[] { file });

        var row = Assert.Single(vm.Files);
        Assert.Equal(ReadinessStatus.NotEncrypted, row.Status);
        Assert.Equal("plain.pdf", row.DisplayText);   // no suffix — not a problem, doesn't read like one
        Assert.Equal(file, row.ToolTipText);          // no message appended either
    }

    [Fact]
    public async Task ARowIsPendingWithNoSuffixUntilItsOwnProbeReturns()
    {
        var file = Touch("f.pdf");
        var started = new ManualResetEventSlim(false);
        var release = new ManualResetEventSlim(false);
        var vm = new UnlockViewModel(new Config(), () => true, probe: (path, candidates) =>
        {
            started.Set();
            Assert.True(release.Wait(2000), "test never released the probe");
            return new Unlock.ProbeResult("ready", path, MatchedIndex: 0, Message: "ok");
        });

        var addTask = vm.AddFilesAsync(new[] { file });
        Assert.True(started.Wait(2000), "the probe never started");

        var row = Assert.Single(vm.Files);
        Assert.Equal(ReadinessStatus.Pending, row.Status);
        Assert.Equal("f.pdf", row.DisplayText);   // quiet while pending, same as NotEncrypted

        release.Set();
        await addTask;
        Assert.Equal(ReadinessStatus.Ready, row.Status);
    }

    // ------------------------------------------------------------ staleness

    [Fact]
    public async Task ANewlySavedPasswordReprobesEveryRowAndAStaleNeedsPasswordVerdictBecomesReady()
    {
        var file = Touch("f.pdf");
        // The mock stands in for Unlock.ProbeReadiness: ready only once a
        // candidate is actually offered, exactly like the real probe would
        // behave once a saved password exists.
        var vm = new UnlockViewModel(new Config(), () => true, probe: (path, candidates) =>
            candidates.Count > 0
                ? new Unlock.ProbeResult("ready", path, MatchedIndex: 0, Message: "ok")
                : new Unlock.ProbeResult("needs_password", path, Message: "none saved yet"));

        await vm.AddFilesAsync(new[] { file });
        var row = Assert.Single(vm.Files);
        Assert.Equal(ReadinessStatus.NeedsPassword, row.Status);

        Assert.True(vm.AddSavedPassword("New", "secret"));
        await vm.ProbeCompletion;

        Assert.Equal(ReadinessStatus.Ready, row.Status);
    }

    [Fact]
    public async Task RemovingTheOnlySavedPasswordReprobesEveryRowAndAStaleReadyVerdictRevertsToNeedsPassword()
    {
        var cfg = new Config();
        cfg.SavedPasswords.Add(new SavedPassword { Label = "X", Password = PasswordVault.Protect("secret") });
        var file = Touch("f.pdf");
        var vm = new UnlockViewModel(cfg, () => true, probe: (path, candidates) =>
            candidates.Count > 0
                ? new Unlock.ProbeResult("ready", path, MatchedIndex: 0, Message: "ok")
                : new Unlock.ProbeResult("needs_password", path, Message: "none saved"));

        await vm.AddFilesAsync(new[] { file });
        var row = Assert.Single(vm.Files);
        Assert.Equal(ReadinessStatus.Ready, row.Status);

        vm.SelectedSavedEntry = vm.Saved[0];
        vm.RemoveSavedCommand.Execute(null);
        await vm.ProbeCompletion;

        Assert.Equal(ReadinessStatus.NeedsPassword, row.Status);
    }

    [Fact]
    public async Task TheSaveBannerAlsoReprobesEveryRow()
    {
        var vm = new UnlockViewModel(new Config(), () => true,
            unlocker: (p, pw) => new Unlock.UnlockResult("ok", p, p, InPlace: true),
            probe: (path, candidates) => candidates.Count > 0
                ? new Unlock.ProbeResult("ready", path, MatchedIndex: 0, Message: "ok")
                : new Unlock.ProbeResult("needs_password", path, Message: "none saved"));
        var file = Touch("f.pdf");
        await vm.AddFilesAsync(new[] { file });
        var row = Assert.Single(vm.Files);
        Assert.Equal(ReadinessStatus.NeedsPassword, row.Status);

        vm.Password = "typed-secret";
        await vm.UnlockAsync();
        Assert.True(vm.SaveBannerVisible, "the save offer never appeared — arrangement broken, not the fix");

        vm.SaveBannerName = "Label";
        vm.SaveBannerCommand.Execute(null);
        await vm.ProbeCompletion;

        Assert.Equal(ReadinessStatus.Ready, row.Status);
    }

    // --------------------------------------------------------- concurrency

    /// <summary>Step 2's "adding files while a probe is in flight must not
    /// corrupt either result": a row can end up with two outstanding probe
    /// requests — its original on-add probe, still blocked, and a saved-
    /// password-change re-probe for the SAME row fired while the first is
    /// still running. Proves the generation guard in ProbeRowsAsync: the
    /// MOST RECENTLY REQUESTED probe wins even though it FINISHES first,
    /// and the stale, later-finishing result from the original request is
    /// discarded rather than clobbering it.</summary>
    [Fact]
    public async Task ASavedPasswordChangeDuringAnInFlightAddProbeDoesNotLetTheStaleResultWin()
    {
        // Dispatch each probe call on its own dedicated Thread rather than
        // through Task.Run's shared ThreadPool for the length of this test.
        // Task.Run's dispatch competes with every other test class xUnit
        // runs in parallel; under real ThreadPool starvation (reproduced
        // locally by saturating the pool with 64 other blocked calls, one
        // stand-in per ~26 of the ~1676 other Wpf.Tests) that dispatch can
        // take multiple seconds just to START, which is what made
        // firstCallStarted.Wait(2000) below an occasional real-time race on
        // GitHub Actions' windows-latest runner rather than a guarantee. A
        // dedicated Thread isn't subject to the CLR's throttled thread-pool
        // injection, so "the probe has started" stops depending on how busy
        // the shared pool happens to be. See
        // UnlockViewModel.OffUiThreadDispatchForTests's own doc comment for the
        // full reproduction and why no [Collection] is needed here.
        UnlockViewModel.OffUiThreadDispatchForTests = body => new Thread(() => body()) { IsBackground = true }.Start();
        try
        {
            var file = Touch("f.pdf");
            var firstCallStarted = new ManualResetEventSlim(false);
            var releaseFirstCall = new ManualResetEventSlim(false);
            var callCount = 0;

            var vm = new UnlockViewModel(new Config(), () => true, probe: (path, candidates) =>
            {
                var n = Interlocked.Increment(ref callCount);
                if (n == 1)
                {
                    // the on-add probe: blocks so the re-probe below can be
                    // requested while this one is still in flight
                    firstCallStarted.Set();
                    Assert.True(releaseFirstCall.Wait(2000), "test never released the first probe call");
                    return new Unlock.ProbeResult("needs_password", path, Message: "STALE — answers the old question");
                }
                // the re-probe, triggered by AddSavedPassword below: answers
                // fast, with the fresh candidate list actually reflected
                return new Unlock.ProbeResult("ready", path, MatchedIndex: 0, Message: "FRESH");
            });

            var addTask = vm.AddFilesAsync(new[] { file });
            Assert.True(firstCallStarted.Wait(2000), "the first probe call never started");

            var row = Assert.Single(vm.Files);
            Assert.Equal(ReadinessStatus.Pending, row.Status);   // still waiting on call #1

            // Fires while call #1 is still blocked — this is the "in flight"
            // part of the scenario.
            Assert.True(vm.AddSavedPassword("New", "secret"));
            await vm.ProbeCompletion;   // call #2 (the re-probe) finishes fast
            Assert.Equal(ReadinessStatus.Ready, row.Status);   // the fresh result already landed

            releaseFirstCall.Set();    // now let the stale call #1 finish too
            await addTask;

            // The stale "needs_password" from call #1 must NOT have overwritten
            // the fresh "ready" from call #2, regardless of finish order.
            Assert.Equal(ReadinessStatus.Ready, row.Status);
            Assert.Equal(2, callCount);
        }
        finally
        {
            UnlockViewModel.OffUiThreadDispatchForTests = null;
        }
    }

    // --------------------------------------------------------- cancellation

    [Fact]
    public async Task ClearingTheListCancelsAnInFlightProbeSoItsLateResultIsDiscarded()
    {
        // See ASavedPasswordChangeDuringAnInFlightAddProbeDoesNotLetTheStaleResultWin's
        // comment and UnlockViewModel.OffUiThreadDispatchForTests's own doc
        // comment: dispatching on a dedicated Thread instead of Task.Run's
        // shared, contended ThreadPool is what makes started.Wait(2000)
        // below a real guarantee instead of a race against however busy the
        // pool happens to be under xUnit's parallel test execution.
        UnlockViewModel.OffUiThreadDispatchForTests = body => new Thread(() => body()) { IsBackground = true }.Start();
        try
        {
            var file = Touch("f.pdf");
            var started = new ManualResetEventSlim(false);
            var release = new ManualResetEventSlim(false);
            var vm = new UnlockViewModel(new Config(), () => true, probe: (path, candidates) =>
            {
                started.Set();
                Assert.True(release.Wait(2000), "test never released the probe");
                return new Unlock.ProbeResult("ready", path, MatchedIndex: 0, Message: "late");
            });

            var addTask = vm.AddFilesAsync(new[] { file });
            Assert.True(started.Wait(2000), "the probe never started");
            var row = Assert.Single(vm.Files);
            Assert.Equal(ReadinessStatus.Pending, row.Status);

            vm.ClearCommand.Execute(null);
            Assert.Empty(vm.Files);

            release.Set();
            await addTask;   // AddFilesAsync's own await only completes once the blocked call returns

            // the row is gone from Files either way; the point is that the LATE
            // result was never applied to it — proven directly on the row
            // object this test still holds a reference to
            Assert.Equal(ReadinessStatus.Pending, row.Status);
        }
        finally
        {
            UnlockViewModel.OffUiThreadDispatchForTests = null;
        }
    }

    [Fact]
    public async Task ClosingTheWindowCancelsAnInFlightProbeSoItsLateResultIsDiscarded()
    {
        var file = Touch("f.pdf");
        var started = new ManualResetEventSlim(false);
        var release = new ManualResetEventSlim(false);
        var vm = new UnlockViewModel(new Config(), () => true, probe: (path, candidates) =>
        {
            started.Set();
            Assert.True(release.Wait(2000), "test never released the probe");
            return new Unlock.ProbeResult("ready", path, MatchedIndex: 0, Message: "late");
        });

        var addTask = vm.AddFilesAsync(new[] { file });
        Assert.True(started.Wait(2000), "the probe never started");
        var row = Assert.Single(vm.Files);

        // Mirrors UnlockWindow.OnClosed calling CancelProbes() alongside
        // CancelUnlock()/ResetBanner() — the window is gone, but (unlike
        // Clear) Files itself is left alone.
        vm.CancelProbes();

        release.Set();
        await addTask;

        Assert.Equal(ReadinessStatus.Pending, row.Status);   // the late "ready" was never applied
        Assert.Single(vm.Files);                             // Files itself is untouched by CancelProbes
    }

    [Fact]
    public async Task ANewDropAfterClearingIsProbedNormallyNotPermanentlyCancelled()
    {
        // ClearCommand cancels the CURRENT probe scope but must hand out a
        // fresh one — otherwise every drop after the first Clear would be
        // probed against an already-cancelled token forever.
        var vm = new UnlockViewModel(new Config(), () => true,
            probe: (path, candidates) => new Unlock.ProbeResult("ready", path, MatchedIndex: 0, Message: "ok"));

        await vm.AddFilesAsync(new[] { Touch("first.pdf") });
        vm.ClearCommand.Execute(null);
        Assert.Empty(vm.Files);

        await vm.AddFilesAsync(new[] { Touch("second.pdf") });

        var row = Assert.Single(vm.Files);
        Assert.Equal(ReadinessStatus.Ready, row.Status);
    }
}
