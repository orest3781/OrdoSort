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
    private readonly HashSet<int> _released = new();

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

    /// <summary>Runs the Nth queued piece of work now, regardless of what
    /// else is pending: this is how a test simulates out-of-order completion.
    /// What awaits the work runs inside this call, so the test can assert
    /// right after it. That needs the synchronization context cleared while
    /// the work completes: xUnit gives each test thread its own, and .NET
    /// will not run an await continuation inline under a non-default one;
    /// it posts it to the thread pool, where it lands after the assert.</summary>
    public void Release(int index)
    {
        Action a;
        lock (_gate) { a = _pending[index]; _released.Add(index); }
        var context = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(null);
        try { a(); }
        finally { SynchronizationContext.SetSynchronizationContext(context); }
    }

    /// <summary>Runs every queued piece of work not yet run, in order,
    /// including work that running it queues. Safe to call again later.</summary>
    public void ReleaseAll()
    {
        for (var i = 0; i < PendingCount; i++)
        {
            bool done;
            lock (_gate) done = _released.Contains(i);
            if (!done) Release(i);
        }
    }
}

public class DebouncedProbeTests
{
    [Fact]
    public void ATypedChangeIsComputedOnlyOnceTheDelayHasPassed()
    {
        var time = new ManualTimeProvider();
        var applied = new List<string>();
        using var probe = new DebouncedProbe<string>(new InlineWorkScheduler(), uiContext: null,
            applied.Add, intervalMs: 300, time: time);

        probe.Trigger(() => "a");
        time.Advance(TimeSpan.FromMilliseconds(299));
        Assert.Empty(applied);

        time.Advance(TimeSpan.FromMilliseconds(1));
        Assert.Equal(new[] { "a" }, applied);
    }

    [Fact]
    public void AnotherChangeInsideTheDelayRestartsItAndOnlyTheLastIsComputed()
    {
        var time = new ManualTimeProvider();
        var applied = new List<string>();
        using var probe = new DebouncedProbe<string>(new InlineWorkScheduler(), uiContext: null,
            applied.Add, intervalMs: 300, time: time);

        probe.Trigger(() => "a");
        time.Advance(TimeSpan.FromMilliseconds(200));
        probe.Trigger(() => "ab");
        time.Advance(TimeSpan.FromMilliseconds(200));
        Assert.Empty(applied);

        time.Advance(TimeSpan.FromMilliseconds(100));
        Assert.Equal(new[] { "ab" }, applied);
    }

    [Fact]
    public void AnImmediateChangeDoesNotWaitForTheDelay()
    {
        var time = new ManualTimeProvider();
        var applied = new List<string>();
        using var probe = new DebouncedProbe<string>(new InlineWorkScheduler(), uiContext: null,
            applied.Add, intervalMs: 300, time: time);

        probe.Trigger(() => "a", immediate: true);

        Assert.Equal(new[] { "a" }, applied);
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
        var probe = new DebouncedProbe<string>(scheduler, uiContext: null, v => { lock (applied) applied.Add(v); }, intervalMs: 0, time: new ManualTimeProvider());

        probe.Trigger(() => "A (stale)", immediate: true);
        Assert.Equal(1, scheduler.PendingCount);

        probe.Trigger(() => "B (fresh)", immediate: true);
        Assert.Equal(2, scheduler.PendingCount);

        // Out-of-order completion: B (the newer probe) finishes FIRST...
        scheduler.Release(1);
        lock (applied) Assert.Equal(new[] { "B (fresh)" }, applied);

        // ...then the stale A finally finishes. It must be dropped, not
        // applied.
        scheduler.Release(0);
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
        var probe = new DebouncedProbe<string>(scheduler, uiContext: null, v => { lock (applied) applied.Add(v); }, intervalMs: 0, time: new ManualTimeProvider());

        probe.Trigger(() => "only", immediate: true);
        Assert.Equal(1, scheduler.PendingCount);
        scheduler.Release(0);

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
        var probe = new DebouncedProbe<string>(scheduler, uiContext: null, v => { lock (applied) applied.Add(v); }, intervalMs: 0, time: new ManualTimeProvider());

        probe.Trigger(() => "late", immediate: true);
        Assert.Equal(1, scheduler.PendingCount);

        probe.Dispose();   // the view model that owns this probe is gone

        scheduler.Release(0);
        lock (applied) Assert.Empty(applied);
    }

    /// <summary>DW-25: the disposed check in Trigger and Cancel sat outside
    /// the lock Dispose takes, so a Dispose on another thread could slip in
    /// between and leave work armed on a disposed probe. The check and the
    /// arming now share that lock. The interleaving itself needs a second
    /// thread at an exact instant; this pins what it must come to: after
    /// Dispose, Trigger and Cancel do nothing at all.</summary>
    [Fact]
    public void AfterDisposeTriggerAndCancelDoNothing()
    {
        var scheduler = new ManualWorkScheduler();
        var time = new ManualTimeProvider();
        var applied = new List<string>();
        var probe = new DebouncedProbe<string>(scheduler, uiContext: null, v => { lock (applied) applied.Add(v); }, intervalMs: 300, time: time);

        probe.Dispose();
        probe.Trigger(() => "after dispose", immediate: true);
        probe.Trigger(() => "after dispose, debounced");
        probe.Cancel();
        time.Advance(TimeSpan.FromSeconds(1));

        Assert.Equal(0, scheduler.PendingCount);
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
        var time = new ManualTimeProvider();
        var probe = new DebouncedProbe<string>(scheduler, uiContext: null, v => { lock (applied) applied.Add(v); }, intervalMs: 300, time: time);

        for (var i = 0; i < 20; i++)
        {
            var captured = i;
            probe.Trigger(() => $"value {captured}");
        }

        time.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal(1, scheduler.PendingCount);
        scheduler.Release(0);

        lock (applied) Assert.Equal(new[] { "value 19" }, applied);
    }

    /// <summary>The generation check used to run only BEFORE the result was
    /// posted to the UI thread. A Resolve fast path (the user cleared the
    /// box) that ran on the UI thread after that check but before the posted
    /// callback got its turn was then overwritten by the stale probe result.
    /// The queued context lets the test land the Resolve in exactly that gap.</summary>
    [Fact]
    public void AResolveBetweenPostAndApplyIsNotOverwrittenByTheStaleResult()
    {
        var scheduler = new ManualWorkScheduler();
        var ui = new QueuedSynchronizationContext();
        var applied = new List<string>();
        var probe = new DebouncedProbe<string>(scheduler, ui, v => { lock (applied) applied.Add(v); }, intervalMs: 0, time: new ManualTimeProvider());

        probe.Trigger(() => "probe (stale)", immediate: true);
        Assert.Equal(1, scheduler.PendingCount);
        scheduler.Release(0);
        Assert.Equal(1, ui.QueuedCount);

        // On the "UI thread", before the posted callback runs: a fast-path answer.
        probe.Resolve("cleared", "", () => "never probed");
        ui.RunQueued();

        lock (applied) Assert.Equal(new[] { "cleared" }, applied);
    }
}

/// <summary>A UI context that only queues posts; the test decides when the
/// "UI thread" gets round to running them.</summary>
internal sealed class QueuedSynchronizationContext : SynchronizationContext
{
    private readonly object _gate = new();
    private readonly List<(SendOrPostCallback Callback, object? State)> _queued = new();

    public int QueuedCount { get { lock (_gate) return _queued.Count; } }

    public override void Post(SendOrPostCallback d, object? state)
    {
        lock (_gate) _queued.Add((d, state));
    }

    public void RunQueued()
    {
        List<(SendOrPostCallback Callback, object? State)> batch;
        lock (_gate)
        {
            batch = _queued.ToList();
            _queued.Clear();
        }
        foreach (var (callback, state) in batch) callback(state);
    }
}
