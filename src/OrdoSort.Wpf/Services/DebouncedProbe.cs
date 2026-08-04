namespace OrdoSort.Wpf.Services;

/// <summary>Debounces a filesystem/network probe so a bound property that
/// re-evaluates per keystroke (a path box) never runs the probe more than
/// once per pause in typing, and never runs it ON the UI thread.
///
/// <see cref="Trigger"/> re-arms a single, reused <see cref="System.Threading.Timer"/>
/// via <c>Change</c> — the same one-timer-reset-on-activity shape
/// FolderWatchService's own debounce timer already uses — so only the LAST
/// call in a burst of keystrokes ever actually runs the probe; every call in
/// between is superseded before its due time arrives. When the timer does
/// fire, <c>compute</c> runs off the UI thread via <see cref="IWorkScheduler"/> —
/// the same "gather (thread pool) → apply (UI)" shape
/// ShellViewModel.RefreshFoldersAsync/ApplySnapshot already use for folder
/// scans — and the result is applied back via <c>apply</c>, marshaled onto
/// the UI thread through the captured <see cref="SynchronizationContext"/>
/// (a raw Timer callback has none of its own, unlike an awaited
/// continuation, so it must be posted explicitly — the same reasoning
/// behind ShellViewModel's own flash/toast/last-action timers).
///
/// A generation counter guards the gap between the timer firing and the
/// probe completing: if a newer <see cref="Trigger"/> call arrived in that
/// gap (the user kept typing while the old probe was still in flight), this
/// probe's result is dropped instead of overwriting a newer one. A lock
/// around the (generation, pending compute) pair keeps that check honest
/// against the timer callback, which runs on a thread-pool thread.
///
/// <see cref="Cancel"/> exists for the OTHER half of that guarantee: a
/// caller with a synchronous, no-I/O answer (a blank or relative path) MUST
/// cancel whatever's pending before applying that answer directly — merely
/// returning early leaves a previously-armed probe alive, and it can still
/// resolve later and silently overwrite the answer for whatever the user
/// has since changed to. <see cref="Resolve"/> packages that shape (fast
/// path vs. neutral-then-probe) as the one mechanism every call site should
/// share, rather than each hand-rolling the same two branches.</summary>
public sealed class DebouncedProbe<T> : IDisposable where T : class
{
    private readonly IWorkScheduler _scheduler;
    private readonly SynchronizationContext? _uiContext;
    private readonly Action<T> _apply;
    private readonly int _intervalMs;
    private readonly System.Threading.Timer _timer;
    private readonly object _gate = new();

    private long _generation;
    private Func<T>? _pendingCompute;
    private bool _disposed;

    public DebouncedProbe(IWorkScheduler scheduler, SynchronizationContext? uiContext,
        Action<T> apply, int intervalMs = 300)
    {
        _scheduler = scheduler;
        _uiContext = uiContext;
        _apply = apply;
        _intervalMs = intervalMs;
        _timer = new System.Threading.Timer(_ => Fire());
    }

    /// <summary>Either resolve synchronously — <paramref name="fastPathResult"/>
    /// non-null means no I/O is needed (a blank/relative path, an unusable
    /// section path, etc.) — cancelling any in-flight/pending probe so it can
    /// never later overwrite this answer; or, when null, go neutral
    /// (<paramref name="neutralValue"/>, typically "") and (re)trigger the
    /// real, debounced, off-thread check. This is the one shared shape
    /// behind every call site that needs it — RouteEditVm/WatchEditVm's
    /// Problem, and SettingsViewModel's per-field notes — because six
    /// hand-copied versions of "short-circuit without cancelling" is exactly
    /// how a stale probe was able to silently overwrite a note for a path
    /// the user had already cleared.</summary>
    public void Resolve(T? fastPathResult, T neutralValue, Func<T> compute, bool immediate = false)
    {
        if (fastPathResult is not null)
        {
            Cancel();
            _apply(fastPathResult);
            return;
        }
        _apply(neutralValue);
        Trigger(compute, immediate);
    }

    /// <summary>Schedule <paramref name="compute"/> to run after the debounce
    /// interval (or immediately — still off the UI thread — when
    /// <paramref name="immediate"/> is set, for the very first check on
    /// load). Re-arms the one shared timer: only the most recent call ever
    /// actually runs the probe.</summary>
    public void Trigger(Func<T> compute, bool immediate = false)
    {
        if (_disposed) return;
        lock (_gate)
        {
            _generation++;
            _pendingCompute = compute;
        }
        _timer.Change(immediate ? 0 : _intervalMs, Timeout.Infinite);
    }

    /// <summary>Cancel whatever's pending: bumps the generation (so an
    /// already-in-flight probe's eventual result is discarded as stale) and
    /// disarms the timer (so a not-yet-fired one never runs at all).</summary>
    public void Cancel()
    {
        if (_disposed) return;
        lock (_gate)
        {
            _generation++;
            _pendingCompute = null;
        }
        _timer.Change(Timeout.Infinite, Timeout.Infinite);
    }

    private void Fire()
    {
        long generation;
        Func<T>? compute;
        lock (_gate)
        {
            generation = _generation;
            compute = _pendingCompute;
            _pendingCompute = null;   // consumed — don't hold the closure alive past this
        }
        if (compute is null) return;
        _ = RunAsync(generation, compute);
    }

    private async Task RunAsync(long generation, Func<T> compute)
    {
        T result;
        try { result = await _scheduler.Run(compute).ConfigureAwait(false); }
        catch { return; }   // a failed probe just leaves the previous note in place

        // Superseded by a later keystroke (or a cancel) while the probe was
        // in flight — that newer call owns the timer now and will apply its
        // own result; this stale one must never win the race and overwrite it.
        lock (_gate) { if (_generation != generation) return; }

        if (_uiContext is null) _apply(result);
        else _uiContext.Post(_ => _apply(result), null);
    }

    /// <summary>Same guarantee as <see cref="Cancel"/>, for good reason:
    /// disposal must cancel whatever's pending outright (per every caller's
    /// doc, e.g. SettingsViewModel.Dispose's "disposing cancels it outright
    /// instead of waiting it out"), not merely stop new work from being
    /// scheduled. Without bumping the generation here, a probe already past
    /// <see cref="Fire"/> (armed and running on the scheduler, generation
    /// already captured) would still find its stale generation matching on
    /// return and call <c>_apply</c> on a view model this object was just
    /// told to let go of.</summary>
    public void Dispose()
    {
        lock (_gate)
        {
            _generation++;
            _pendingCompute = null;
        }
        _disposed = true;
        _timer.Dispose();
    }
}
