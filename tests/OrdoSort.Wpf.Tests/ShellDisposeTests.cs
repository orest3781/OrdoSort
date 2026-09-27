namespace OrdoSort.Wpf.Tests;

/// <summary>A shell whose window has closed is disposed, and whatever work it
/// still had in flight must end quietly: there is no window left to show a
/// warning over. A modal "that didn't finish" warning from a closed
/// dashboard's start-up hung the E2E run on GitHub (2026-09-27).</summary>
public class ShellDisposeTests
{
    // xUnit installs its own synchronization context just before each test
    // method (after the constructor), and an await inside the shell started
    // here would send its continuation back through it, after the assert.
    // Cleared at the top of each test, released work runs inline.
    private static void NoSynchronizationContext() => SynchronizationContext.SetSynchronizationContext(null);

    [Fact]
    public void StartUpThatFailsAfterTheShellWasDisposedWarnsNobody()
    {
        NoSynchronizationContext();
        var scheduler = new ManualWorkScheduler();
        using var fx = new ShellFixture(scheduler: scheduler);
        fx.Shell.Initialize();   // the inbox scan is now held on the scheduler

        fx.Shell.Dispose();      // the window closed meanwhile
        fx.Viewer.ThrowOnBlank = new ObjectDisposedException("Viewer");
        scheduler.ReleaseAll();  // the scan finishes; start-up carries on

        Assert.Empty(fx.Dialogs.Warnings);
    }

    [Fact]
    public void StartUpThatFailsWhileTheShellIsAliveStillWarns()
    {
        NoSynchronizationContext();
        var scheduler = new ManualWorkScheduler();
        using var fx = new ShellFixture(scheduler: scheduler);
        fx.Viewer.ThrowOnBlank = new InvalidOperationException("the viewer broke");

        fx.Shell.Initialize();
        scheduler.ReleaseAll();

        Assert.Single(fx.Dialogs.Warnings);
    }
}
