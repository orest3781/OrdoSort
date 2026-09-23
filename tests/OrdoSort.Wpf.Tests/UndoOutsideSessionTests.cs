using OrdoSort.Core;
using OrdoSort.Wpf.Services;
using OrdoSort.Wpf.ViewModels;

namespace OrdoSort.Wpf.Tests;

/// <summary>Runs background work inline, except while <see cref="Holding"/>
/// is set: then each item is queued until <see cref="ReleaseAll"/>, so a
/// test can look at the shell while one operation is still in flight.</summary>
file sealed class HoldableWorkScheduler : IWorkScheduler
{
    private readonly ControlledWorkScheduler _held = new();
    public bool Holding { get; set; }

    public Task<T> Run<T>(Func<T> work) =>
        Holding ? _held.Run(work) : Task.FromResult(work());

    public Task Run(Action work)
    {
        if (Holding) return _held.Run(work);
        work();
        return Task.CompletedTask;
    }

    public void ReleaseAll() { Holding = false; _held.ReleaseAll(); }
}

/// <summary>Undo belongs to the session on screen: once the user is back on
/// the Ready dashboard, Ctrl+Shift+Z must not quietly move a filed document
/// back into the inbox. And Rescan (Refresh) must not drop to Ready while an
/// undo is still moving a file.</summary>
public class UndoOutsideSessionTests
{
    [Fact]
    public async Task UndoOnTheReadyDashboardAfterStoppingDoesNothing()
    {
        using var fx = new ShellFixture();
        fx.AddInboxFile("20240115--111111.pdf");
        fx.AddInboxFile("20240116--222222.pdf");
        fx.Shell.Initialize();
        fx.Shell.StartProcessing();
        fx.Shell.TypedName = "SMITH JOHN";
        await fx.Shell.OnRouteAsync(0);
        var filed = Path.Combine(fx.RouteDir, "20240115-SMITH JOHN-111111.pdf");
        Assert.True(File.Exists(filed));

        fx.Shell.StopSession();
        Assert.Equal(Screen.Ready, fx.Shell.Screen);
        var shownBefore = fx.Viewer.Shown.Count;

        Assert.False(fx.Shell.CanUndo);
        Assert.False(fx.Shell.UndoCommand.CanExecute(null));
        await fx.Shell.OnUndoAsync();   // the Ctrl+Shift+Z path, bypassing CanExecute

        Assert.True(File.Exists(filed));   // still filed
        Assert.False(File.Exists(Path.Combine(fx.Inbox, "20240115--111111.pdf")));
        Assert.Equal(shownBefore, fx.Viewer.Shown.Count);   // hidden viewer untouched
        Assert.Equal(Screen.Ready, fx.Shell.Screen);
    }

    [Fact]
    public async Task UndoFromTheDoneScreenStillReopensTheSession()
    {
        using var fx = new ShellFixture();
        fx.AddInboxFile("20240115--111111.pdf");
        fx.Shell.Initialize();
        fx.Shell.StartProcessing();
        fx.Shell.TypedName = "SMITH JOHN";
        await fx.Shell.OnRouteAsync(0);
        Assert.Equal(Screen.Done, fx.Shell.Screen);
        Assert.True(fx.Shell.UndoCommand.CanExecute(null));

        await fx.Shell.OnUndoAsync();

        Assert.Equal(Screen.Processing, fx.Shell.Screen);
        Assert.True(File.Exists(Path.Combine(fx.Inbox, "20240115--111111.pdf")));
    }

    [Fact]
    public async Task RescanWhileAnUndoIsInFlightIsIgnored()
    {
        var scheduler = new HoldableWorkScheduler();
        using var fx = new ShellFixture(scheduler: scheduler);
        fx.AddInboxFile("20240115--111111.pdf");
        fx.AddInboxFile("20240116--222222.pdf");
        fx.Shell.Initialize();
        fx.Shell.StartProcessing();
        fx.Shell.TypedName = "SMITH JOHN";
        await fx.Shell.OnRouteAsync(0);

        scheduler.Holding = true;
        var undo = fx.Shell.OnUndoAsync();   // parked on the file move
        Assert.True(fx.Shell.IsBusy);

        fx.Shell.Rescan();                   // Refresh pressed mid-undo
        Assert.Equal(Screen.Processing, fx.Shell.Screen);

        scheduler.ReleaseAll();
        await undo;
        Assert.Equal(Screen.Processing, fx.Shell.Screen);
        Assert.Equal("20240115--111111.pdf", fx.Shell.CurrentFilename);
    }
}
