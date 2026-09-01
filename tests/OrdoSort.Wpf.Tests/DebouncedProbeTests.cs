using OrdoSort.Wpf.Services;

namespace OrdoSort.Wpf.Tests;

/// <summary>A scheduler the test fully controls: Run() never actually
/// invokes the work — it queues it and hands back an incomplete Task, so the
/// test can force two probes to complete in whichever order it wants
/// (including reversed from how they were triggered) instead of racing real
/// threads. Guarded by a lock since Add (from the timer's callback thread)
/// and Count/Release (from the test thread) run concurrently.</summary>
internal sealed class ManualWorkScheduler : IWorkScheduler
{
    private readonly object _gate = new();
    private readonly List<Action> _pending = new();

    public int PendingCount { get { lock (_gate) return _pending.Count; } }

    public Task<T> Run<T>(Func<T> work)
    {
        var tcs = new TaskCompletionSource<T>();
        lock (_gate) _pending.Add(() => tcs.SetResult(work()));
        return tcs.Task;
    }

    public Task Run(Action work)
    {
        var tcs = new TaskCompletionSource();
        lock (_gate) _pending.Add(() => { work(); tcs.SetResult(); });
        return tcs.Task;
    }

    /// <summary>Actually run the Nth queued probe now, regardless of what
    /// else is pending — this is how the test simulates out-of-order
    /// completion. Note this only *starts* the completion: per
    /// TaskCompletionSource semantics the awaiter's continuation is not
    /// guaranteed to run synchronously within this call (the runtime is free
    /// to hop it to another thread, e.g. under stack-depth guards when other
    /// async chains are active) — callers must poll for the effect, not
    /// assert immediately after calling this.</summary>
    public void Release(int index)
    {
        Action a;
        lock (_gate) a = _pending[index];
        a();
    }
}

public class DebouncedProbeTests
{
    private static void WaitFor(Func<bool> condition, string because, int timeoutMs = 3000)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (true)
        {
            bool result;
            try
            {
                result = condition();
            }
            // Fix round 2, item 2(b) — same fix as the WaitFor copy in
            // ToolViewModelTests/SettingsViewModelTests/TilePreviewProbeTests:
            // a predicate reading a collection that a background thread is
            // mid-mutating can throw INSIDE the read rather than just
            // observe a stale-but-valid value. Both exceptions below are
            // the SAME "not true yet" outcome a plain false would be, so
            // they are retried, not surfaced. Nothing else is caught: a
            // predicate that throws for a REAL reason must still fail the
            // test immediately.
            catch (Exception ex) when (ex is ArgumentOutOfRangeException or InvalidOperationException)
            {
                result = false;
            }
            if (result) return;
            if (sw.ElapsedMilliseconds > timeoutMs)
                Assert.Fail($"condition never became true within {timeoutMs}ms: {because}");
            Thread.Sleep(5);
        }
    }

    /// <summary>The core non-negotiable guarantee: if an OLDER probe is still
    /// in flight when a NEWER one is triggered (the user kept typing while a
    /// slow SMB check was outstanding), the older one's result — even if it
    /// finishes LAST — must never stomp the newer one's answer.</summary>
    [Fact]
    public void AStaleResultNeverOverwritesANewerOne()
    {
        var scheduler = new ManualWorkScheduler();
        var applied = new List<string>();
        var probe = new DebouncedProbe<string>(scheduler, uiContext: null, v => { lock (applied) applied.Add(v); }, intervalMs: 0);

        probe.Trigger(() => "A (stale)", immediate: true);
        WaitFor(() => scheduler.PendingCount == 1, "probe A's work should reach the scheduler");

        probe.Trigger(() => "B (fresh)", immediate: true);
        WaitFor(() => scheduler.PendingCount == 2, "probe B's work should reach the scheduler");

        // Out-of-order completion: B (the newer probe) finishes FIRST...
        scheduler.Release(1);
        WaitFor(() => { lock (applied) return applied.Count >= 1; }, "B's result should eventually apply");
        lock (applied) Assert.Equal(new[] { "B (fresh)" }, applied);

        // ...then the stale A finally finishes. It must be dropped, not
        // applied — give it a generous moment to (wrongly) show up, then
        // confirm it never did.
        scheduler.Release(0);
        Thread.Sleep(200);
        lock (applied) Assert.Equal(new[] { "B (fresh)" }, applied);   // unchanged — A never landed
    }

    /// <summary>Sanity check the mechanism the other direction: with only
    /// one probe in flight, its result DOES apply — the staleness guard
    /// isn't just eating every result.</summary>
    [Fact]
    public void ASingleInFlightResultDoesApply()
    {
        var scheduler = new ManualWorkScheduler();
        var applied = new List<string>();
        var probe = new DebouncedProbe<string>(scheduler, uiContext: null, v => { lock (applied) applied.Add(v); }, intervalMs: 0);

        probe.Trigger(() => "only", immediate: true);
        WaitFor(() => scheduler.PendingCount == 1, "the probe's work should reach the scheduler");
        scheduler.Release(0);

        WaitFor(() => { lock (applied) return applied.Count == 1; }, "the result should eventually apply");
        lock (applied) Assert.Equal(new[] { "only" }, applied);
    }

    /// <summary>M2 (2026-08-03 final-review): Dispose() must cancel an
    /// in-flight probe outright, the same guarantee <see cref="Cancel"/>
    /// gives and the one every caller's own doc promises (e.g.
    /// SettingsViewModel.Dispose: "disposing cancels it outright instead of
    /// waiting it out"). Triggers a probe (reaches the scheduler, i.e. past
    /// Fire()), disposes while it's still pending there, THEN releases it —
    /// mirroring the real race: a probe already running when the owning
    /// view model is torn down. The result must never reach <c>applied</c>.</summary>
    [Fact]
    public void DisposeDropsAnInFlightProbesResultInsteadOfApplyingIt()
    {
        var scheduler = new ManualWorkScheduler();
        var applied = new List<string>();
        var probe = new DebouncedProbe<string>(scheduler, uiContext: null, v => { lock (applied) applied.Add(v); }, intervalMs: 0);

        probe.Trigger(() => "late", immediate: true);
        WaitFor(() => scheduler.PendingCount == 1, "the probe's work should reach the scheduler");

        probe.Dispose();   // the view model that owns this probe is gone

        scheduler.Release(0);
        Thread.Sleep(200);   // give the (wrongly) applied result a generous moment to show up
        lock (applied) Assert.Empty(applied);
    }

    /// <summary>Debounce semantics: rapid re-triggering (the shape of fast
    /// keystrokes) must only ever let the LAST call's work reach the
    /// scheduler — earlier ones are cancelled outright, never merely
    /// discarded after running.</summary>
    [Fact]
    public void RapidRetriggeringOnlyEverRunsTheLastOne()
    {
        var scheduler = new ManualWorkScheduler();
        var applied = new List<string>();
        // a real (non-zero) interval: each Trigger() call must cancel the
        // previous still-pending timer before it ever fires
        var probe = new DebouncedProbe<string>(scheduler, uiContext: null, v => { lock (applied) applied.Add(v); }, intervalMs: 300);

        for (var i = 0; i < 20; i++)
        {
            var captured = i;
            probe.Trigger(() => $"value {captured}");
        }

        WaitFor(() => scheduler.PendingCount == 1, "only the last Trigger() should ever reach the scheduler", 2000);
        scheduler.Release(0);

        WaitFor(() => { lock (applied) return applied.Count == 1; }, "the result should eventually apply");
        lock (applied) Assert.Equal(new[] { "value 19" }, applied);
    }
}
