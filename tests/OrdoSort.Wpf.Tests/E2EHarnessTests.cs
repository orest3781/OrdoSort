using System.Windows.Threading;
using OrdoSort.Smoke.E2E;

namespace OrdoSort.Wpf.Tests;

/// <summary>Unit coverage for the E2E harness's own moving parts — the pump,
/// the fixture builder, the scripted dialogs, and the evidence writer. The
/// scenarios themselves can only run under the STA harness; these are the
/// pieces that must be right before any scenario can be trusted.</summary>
public class E2EHarnessTests
{
    /// <summary>A condition already true must not arm a frame at all — the
    /// common case, and the one where an unnecessary DispatcherFrame would
    /// hang a caller that has no message loop running.</summary>
    [Fact]
    public void UntilReturnsImmediatelyWhenConditionAlreadyTrue()
    {
        Assert.True(E2EPump.Until(() => true, timeoutMs: 50));
    }

    /// <summary>A condition that never comes true reports false rather than
    /// throwing: one stuck scenario must not abort the run.</summary>
    [Fact]
    public void UntilReturnsFalseOnTimeoutWithoutThrowing()
    {
        Assert.False(E2EPump.Until(() => false, timeoutMs: 150));
    }

    /// <summary>The condition flips from a dispatcher callback, which is the
    /// real shape: DebouncedProbe marshals its result back through uiContext,
    /// so Until only observes it if it is genuinely pumping the queue.</summary>
    [Fact]
    public void UntilObservesAConditionSetFromADispatcherCallback()
    {
        var flipped = false;
        var t = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(20),
        };
        t.Tick += (_, _) => { flipped = true; t.Stop(); };
        t.Start();

        Assert.True(E2EPump.Until(() => flipped, timeoutMs: 3000));
    }

    /// <summary>Regression coverage for the bug hunted down while verifying
    /// Task 1's extraction: <paramref name="kickoff"/> must be queued
    /// through the dispatcher (BeginInvoke), not invoked inline before the
    /// pump starts. PushFrame installs a DispatcherSynchronizationContext
    /// only for the frame it's running; invoke kickoff before that frame is
    /// live and an `await` inside it captures whatever context is ambient
    /// AT THAT CALL (typically null on a bare thread), so its continuation
    /// resumes off a bare thread-pool thread instead — exactly what crashed
    /// the real StartProcessing/OnRouteAsync kickoffs in Screenshots.cs the
    /// moment they touched a bound ObservableCollection.
    ///
    /// A plain "did the continuation eventually run" flag does not catch
    /// this: Task.Yield's continuation completes either way, just via a
    /// different thread, so the flag flips true regardless. Asserting the
    /// *type* of SynchronizationContext.Current the continuation resumed on
    /// is what actually discriminates: null under the inline-invoke bug, a
    /// live DispatcherSynchronizationContext once queued correctly.</summary>
    [Fact]
    public void UntilQueuesKickoffSoItsAwaitResumesOnTheLiveDispatcherContext()
    {
        SynchronizationContext? resumedOn = null;
        var ready = false;

        E2EPump.Until(() => ready, timeoutMs: 3000, kickoff: async () =>
        {
            await Task.Yield();
            resumedOn = SynchronizationContext.Current;
            ready = true;
        });

        Assert.IsType<DispatcherSynchronizationContext>(resumedOn);
    }
}
