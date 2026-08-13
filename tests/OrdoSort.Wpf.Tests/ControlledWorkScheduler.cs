using OrdoSort.Wpf.Services;

namespace OrdoSort.Wpf.Tests;

/// <summary>Queues scheduled work instead of running it, so a test decides
/// exactly when each item runs — and in what order.
///
/// <see cref="InlineWorkScheduler"/> is the right double when a test wants
/// everything finished by the time the call returns. It is the WRONG one for
/// the probe tests, which are about what is true while work is still in
/// flight: run inline, a probe finishes before the test can look at the row,
/// and assertions like "the row is Pending" or "the late result was
/// discarded" would pass without exercising anything. Two of them would
/// become tests that pass for the wrong reason — the thing this suite's own
/// class docs call out as the failure mode to avoid.
///
/// This is the third option those tests actually need. Work is dispatched
/// (so the view model has genuinely handed it off and moved on) but not
/// executed, which is what "in flight" means from the caller's side. No
/// thread is blocked, no ManualResetEventSlim is signalled, and nothing
/// depends on the ThreadPool getting round to anything: the previous shape
/// needed two events per test and a probe delegate that blocked a pool
/// thread to hold the window open.
///
/// Continuations resume on whichever thread calls a Release method, so
/// awaiting code runs synchronously inside that call and a test can assert
/// immediately after it returns.</summary>
public sealed class ControlledWorkScheduler : IWorkScheduler
{
    private readonly List<Action> _queued = new();

    /// <summary>How many dispatched-but-not-yet-run items are outstanding.</summary>
    public int Queued => _queued.Count;

    public Task<T> Run<T>(Func<T> work)
    {
        var completion = new TaskCompletionSource<T>();
        _queued.Add(() =>
        {
            try { completion.SetResult(work()); }
            catch (Exception ex) { completion.SetException(ex); }
        });
        return completion.Task;
    }

    public Task Run(Action work)
    {
        var completion = new TaskCompletionSource();
        _queued.Add(() =>
        {
            try { work(); completion.SetResult(); }
            catch (Exception ex) { completion.SetException(ex); }
        });
        return completion.Task;
    }

    /// <summary>Run the OLDEST outstanding item. Releasing one item often
    /// queues the next — a continuation that dispatches more work runs
    /// inside this call — which is exactly how a test walks a flow one
    /// hand-off at a time.</summary>
    public void ReleaseNext() => Release(0);

    /// <summary>Run the NEWEST outstanding item, leaving older ones still in
    /// flight. This is what lets a test land a fresh result BEFORE an older
    /// one finishes, which is the whole scenario in the staleness tests and
    /// was previously arranged by blocking the first probe on a gate.</summary>
    public void ReleaseNewest() => Release(_queued.Count - 1);

    /// <summary>Run everything outstanding, including anything queued while
    /// draining, oldest first.</summary>
    public void ReleaseAll()
    {
        while (_queued.Count > 0) Release(0);
    }

    private void Release(int index)
    {
        if (_queued.Count == 0)
            throw new InvalidOperationException(
                "nothing is scheduled — the flow under test dispatched less work than this test expects");

        var item = _queued[index];
        _queued.RemoveAt(index);
        item();
    }
}
