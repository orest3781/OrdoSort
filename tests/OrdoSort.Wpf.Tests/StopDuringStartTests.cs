using OrdoSort.Wpf.ViewModels;

namespace OrdoSort.Wpf.Tests;

/// <summary>Start processing keeps working after its inbox scan: it opens the
/// session window, refreshes the name suggestions and measures the first
/// page. Esc in that gap closes the session. If start-up then carried on and
/// showed the first document, it would be open in a hidden preview, and Edge
/// holds a shown document's file open: another station could not file it
/// until this one started a new session or quit.</summary>
public class StopDuringStartTests
{
    [Fact]
    public async Task EscWhileASessionIsStillStartingShowsNoDocument()
    {
        // released work's continuations must run inside Release, before the asserts
        SynchronizationContext.SetSynchronizationContext(null);
        var scheduler = new ManualWorkScheduler();
        using var fx = new ShellFixture(scheduler: scheduler);
        fx.AddInboxFile("20240115--111111.pdf");
        fx.Shell.Initialize();
        scheduler.ReleaseAll();

        var scan = scheduler.PendingCount;
        var start = fx.Shell.StartProcessingAsync();
        scheduler.Release(scan);                  // the scan lands; start-up carries on
        Assert.Equal(Screen.Processing, fx.Shell.Screen);

        fx.Shell.StopSession();                   // Esc, before the first document shows
        scheduler.ReleaseAll();
        await start;

        Assert.Equal(Screen.Ready, fx.Shell.Screen);
        Assert.Empty(fx.Viewer.Shown);
    }
}
