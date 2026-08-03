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
        while (!condition())
        {
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
