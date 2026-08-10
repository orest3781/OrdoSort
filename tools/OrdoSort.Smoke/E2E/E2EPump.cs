using System.Windows;
using System.Windows.Threading;

namespace OrdoSort.Smoke.E2E;

/// <summary>Dispatcher pumping for the E2E harness.
///
/// Scenarios run on the STA thread that owns the windows, so the usual
/// test-side wait — Thread.Sleep in a polling loop, as
/// FilenameListViewModelTests uses — is not available here: sleeping on this
/// thread blocks the very message loop DebouncedProbe&lt;T&gt; needs to
/// marshal its result back through uiContext, so the condition can never
/// become true and the wait always burns its full timeout. Pumping a nested
/// DispatcherFrame keeps that loop alive while we wait.</summary>
public static class E2EPump
{
    /// <summary>Pump until <paramref name="ready"/> is true or the timeout
    /// elapses. Returns whether it came true. Never throws — a stuck
    /// scenario is a recorded failure, not an aborted run.</summary>
    public static bool Until(Func<bool> ready, int timeoutMs = 8000, Action? kickoff = null)
    {
        if (kickoff is null && ready()) return true;

        var frame = new DispatcherFrame();
        var deadline = Environment.TickCount64 + timeoutMs;
        var success = false;
        var timer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(25),
        };
        timer.Tick += (_, _) =>
        {
            bool done;
            try { done = ready(); }
            catch { done = false; }   // a predicate that throws mid-load just isn't ready yet
            if (done) { success = true; frame.Continue = false; }
            else if (Environment.TickCount64 >= deadline) { frame.Continue = false; }
        };
        // kickoff, when given, is queued via BeginInvoke instead of called
        // inline before the pump starts. PushFrame installs a
        // DispatcherSynchronizationContext only for the frame it's running —
        // between two Until calls there is none. A caller invoking an async
        // method directly (e.g. Shell.StartProcessing, Shell.OnRouteAsync)
        // has its first `await` capture whatever context is ambient AT THAT
        // CALL; call it before the frame is live and the continuation
        // resumes on a bare thread-pool thread, which crashes the moment it
        // touches a bound ObservableCollection (WPF's CollectionView
        // enforces thread affinity). Queuing it as kickoff instead means it
        // runs once THIS pump's frame is already live, so its await
        // correctly captures this pump's context.
        if (kickoff is not null) Dispatcher.CurrentDispatcher.BeginInvoke(kickoff);
        timer.Start();
        try { Dispatcher.PushFrame(frame); }
        finally { timer.Stop(); }
        return success;
    }

    /// <summary>Run every queued dispatcher operation down to Background
    /// priority, then return — for the case where work is already posted and
    /// only needs a turn of the loop, with no condition to wait on.</summary>
    public static void Drain()
    {
        var frame = new DispatcherFrame();
        Dispatcher.CurrentDispatcher.BeginInvoke(
            DispatcherPriority.Background, new Action(() => frame.Continue = false));
        Dispatcher.PushFrame(frame);
    }

    /// <summary>Show a window far off-screen so it lays out and renders
    /// without stealing focus or appearing during a run.</summary>
    public static void ShowOffscreen(Window win)
    {
        win.WindowStartupLocation = WindowStartupLocation.Manual;
        win.Left = -20000;
        win.Top = 0;
        win.ShowActivated = false;
        win.Show();
        win.UpdateLayout();
    }
}
