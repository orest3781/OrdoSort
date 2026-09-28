using OrdoSort.Core;
using OrdoSort.Wpf.ViewModels;

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

    /// <summary>QC-19: closing the app while a document was still being moved
    /// over the network disposed the history database under the move. The
    /// document was filed, its history row was not, and nothing said so.
    /// The database now stays open until the move in flight has finished.</summary>
    [Fact]
    public async Task ClosingWhileADocumentIsBeingFiledStillRecordsItInHistory()
    {
        NoSynchronizationContext();
        var scheduler = new ManualWorkScheduler();
        using var fx = new ShellFixture(scheduler: scheduler);
        fx.AddInboxFile("20240115--111111.pdf");
        fx.Shell.Initialize();
        scheduler.ReleaseAll();
        var start = fx.Shell.StartProcessingAsync();
        scheduler.ReleaseAll();
        await start;
        Assert.Equal(Screen.Processing, fx.Shell.Screen);

        var filing = fx.Shell.OnRouteAsync(0);   // the move is held on the scheduler
        fx.Shell.Dispose();                     // the window closed meanwhile
        scheduler.ReleaseAll();                 // the move lands, then its history row
        await filing;
        await fx.Shell.FilingInFlight;

        using var history = new History(Config.ResolveBeside(fx.CfgPath, fx.Cfg.HistoryDb));
        Assert.Equal(1, history.Count());
        Assert.Single(Directory.GetFiles(fx.RouteDir));
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
