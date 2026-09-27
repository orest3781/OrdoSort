using System.Windows;
using System.Windows.Threading;

namespace OrdoSort.Smoke.E2E;

/// <summary>Ends a scenario that has run far too long, saying what the UI
/// thread was doing. On GitHub a stuck scenario used to run into the job's
/// 20-minute limit and be cancelled with no clue (2026-09-27); this fires
/// after <c>limit</c> instead, from a thread-pool timer, so it works however
/// stuck the UI thread is.</summary>
public sealed class ScenarioWatchdog : IDisposable
{
    private readonly Timer _timer;

    /// <param name="onStuck">Called once, off the UI thread, with the scenario
    /// and <see cref="Describe"/>'s account of the UI thread.</param>
    public ScenarioWatchdog(Dispatcher ui, string scenario, TimeSpan limit, Action<string> onStuck) =>
        _timer = new Timer(_ => onStuck(
            $"{scenario} was still running after {limit.TotalSeconds:0} s. {Describe(ui, TimeSpan.FromSeconds(10))}"),
            null, limit, Timeout.InfiniteTimeSpan);

    public void Dispose() => _timer.Dispose();

    /// <summary>What <paramref name="ui"/> is doing, in three cases:
    /// not pumping at all (blocked in a synchronous wait: a deadlock); pumping
    /// but never getting down to Background work (higher-priority work, such
    /// as a layout that never settles, never lets up); or pumping and idle (a
    /// wait that never came true, or a dialog). Lists the open windows when the
    /// thread can answer. Each probe waits at most <paramref name="probe"/>.</summary>
    public static string Describe(Dispatcher ui, TimeSpan probe)
    {
        string? windows = null;
        var urgent = ui.BeginInvoke(DispatcherPriority.Send, new Action(() =>
        {
            // Only the app on this very thread can be asked for its windows,
            // and a diagnostic must never take the run down with it.
            try
            {
                if (Application.Current is { } app && app.Dispatcher == ui)
                    windows = string.Join("; ", app.Windows.OfType<Window>().Select(w =>
                        $"{w.GetType().Name} \"{w.Title}\" visible={w.IsVisible} active={w.IsActive}"));
            }
            catch (Exception ex) { windows = "(could not list them: " + ex.GetType().Name + ")"; }
        }));
        if (urgent.Wait(probe) != DispatcherOperationStatus.Completed)
            return "The UI thread is not pumping at all: it is blocked in a synchronous wait, most likely a deadlock.";

        var ordinary = ui.BeginInvoke(DispatcherPriority.Background, new Action(() => { }));
        var state = ordinary.Wait(probe) == DispatcherOperationStatus.Completed
            ? "The UI thread is pumping and idle: a wait that never came true, or a dialog."
            : "The UI thread is pumping, but higher-priority work (layout or rendering) never lets up.";
        return state + " Open windows: " + (string.IsNullOrEmpty(windows) ? "none" : windows);
    }
}
