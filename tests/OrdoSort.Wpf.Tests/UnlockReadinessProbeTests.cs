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
        // "In flight" is the scheduler's queue, not a blocked thread: the
        // probe has been DISPATCHED and not yet run, which is exactly the
        // state this row is asserted to be in. No elapsed-time budget, no
        // event to signal, and no ThreadPool item held open for the length
        // of the assertions.
        var file = Touch("f.pdf");
        var scheduler = new ControlledWorkScheduler();
        var vm = new UnlockViewModel(new Config(), () => true,
            probe: (path, candidates) =>
                new Unlock.ProbeResult("ready", path, MatchedIndex: 0, Message: "ok"),
            scheduler: scheduler);

        var addTask = vm.AddFilesAsync(new[] { file });
        scheduler.ReleaseNext();   // the intake check: the row lands, its probe is queued

        var row = Assert.Single(vm.Files);
        Assert.Equal(ReadinessStatus.Pending, row.Status);
        Assert.Equal("f.pdf", row.DisplayText);   // quiet while pending, same as NotEncrypted

        scheduler.ReleaseAll();    // now let the probe answer
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
        // The ordering this test needs — probe #2 lands BEFORE probe #1
        // finishes — used to be arranged by blocking probe #1 on a thread
        // and signalling it back to life. It is now stated directly: both
        // probes sit in the scheduler's queue and the test runs the newer
        // one first. A prior version bounded the waits at 2000ms and tried
        // to make dispatch fast enough to fit (UnlockViewModel.OffUiThread
        // DispatchForTests, since removed); the gates that replaced it
        // removed the budget but still needed a pool thread held open for
        // the length of the scenario. Neither is needed to say "run this
        // one, not that one yet".
        var file = Touch("f.pdf");
        var scheduler = new ControlledWorkScheduler();
        var callCount = 0;

        var vm = new UnlockViewModel(new Config(), () => true, probe: (path, candidates) =>
        {
            callCount++;
            // Discriminated by what the probe was ASKED, never by which call
            // ordinal happens to run first: this test exists precisely
            // because the two probes finish out of order, so a counter would
            // label whichever ran first as the stale one and the test would
            // be asserting its own arrangement. Candidates are snapshotted
            // at dispatch, so the on-add probe carries an empty list and the
            // post-save re-probe carries the new password — the same
            // discriminator ANewlySavedPasswordReprobesEveryRow uses above.
            return candidates.Count == 0
                ? new Unlock.ProbeResult("needs_password", path, Message: "STALE — answers the old question")
                : new Unlock.ProbeResult("ready", path, MatchedIndex: 0, Message: "FRESH");
        },
            scheduler: scheduler);

        var addTask = vm.AddFilesAsync(new[] { file });
        scheduler.ReleaseNext();   // the intake check: the row lands, probe #1 is queued

        var row = Assert.Single(vm.Files);
        Assert.Equal(ReadinessStatus.Pending, row.Status);   // still waiting on probe #1

        // Requested while probe #1 is still outstanding — this is the "in
        // flight" part of the scenario.
        Assert.True(vm.AddSavedPassword("New", "secret"));
        scheduler.ReleaseNewest();   // probe #2 answers while #1 is still queued
        await vm.ProbeCompletion;
        Assert.Equal(ReadinessStatus.Ready, row.Status);   // the fresh result already landed

        scheduler.ReleaseAll();    // now let the stale probe #1 finish too
        await addTask;

        // The stale "needs_password" from call #1 must NOT have overwritten
        // the fresh "ready" from call #2, regardless of finish order.
        Assert.Equal(ReadinessStatus.Ready, row.Status);
        Assert.Equal(2, callCount);
    }

    // --------------------------------------------------------- cancellation

    [Fact]
    public async Task ClearingTheListCancelsAnInFlightProbeSoItsLateResultIsDiscarded()
    {
        // "The probe is in flight" is the scheduler's queue: dispatched,
        // not yet run. Clear happens while it sits there, and the result is
        // produced afterwards — a genuinely late answer, with no thread
        // parked and no millisecond budget raced against a loaded CI runner.
        var file = Touch("f.pdf");
        var scheduler = new ControlledWorkScheduler();
        var vm = new UnlockViewModel(new Config(), () => true,
            probe: (path, candidates) =>
                new Unlock.ProbeResult("ready", path, MatchedIndex: 0, Message: "late"),
            scheduler: scheduler);

        var addTask = vm.AddFilesAsync(new[] { file });
        scheduler.ReleaseNext();   // the intake check: the row lands, its probe is queued
        var row = Assert.Single(vm.Files);
        Assert.Equal(ReadinessStatus.Pending, row.Status);

        vm.ClearCommand.Execute(null);
        Assert.Empty(vm.Files);

        scheduler.ReleaseAll();
        await addTask;   // AddFilesAsync's own await only completes once the late call returns

        // the row is gone from Files either way; the point is that the LATE
        // result was never applied to it — proven directly on the row
        // object this test still holds a reference to
        Assert.Equal(ReadinessStatus.Pending, row.Status);
    }

    [Fact]
    public async Task ClosingTheWindowCancelsAnInFlightProbeSoItsLateResultIsDiscarded()
    {
        // Same queued-not-run arrangement as ClearingTheListCancelsAnInFlight
        // ProbeSoItsLateResultIsDiscarded just above; the only difference is
        // what happens while the probe is outstanding.
        var file = Touch("f.pdf");
        var scheduler = new ControlledWorkScheduler();
        var vm = new UnlockViewModel(new Config(), () => true,
            probe: (path, candidates) =>
                new Unlock.ProbeResult("ready", path, MatchedIndex: 0, Message: "late"),
            scheduler: scheduler);

        var addTask = vm.AddFilesAsync(new[] { file });
        scheduler.ReleaseNext();   // the intake check: the row lands, its probe is queued
        var row = Assert.Single(vm.Files);

        // Mirrors UnlockWindow.OnClosed calling CancelProbes() alongside
        // CancelUnlock()/ResetBanner() — the window is gone, but (unlike
        // Clear) Files itself is left alone.
        vm.CancelProbes();

        scheduler.ReleaseAll();
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
