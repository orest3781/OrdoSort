using OrdoSort.Core;
using OrdoSort.Wpf.ViewModels;

namespace OrdoSort.Wpf.Tests;

/// <summary>The one-line note under the buttons on the Processing and Done
/// screens (<c>StatusLine</c>).
///
/// Two kinds of text share that one line and they are not the same thing. A
/// NOTE reports something that just happened — "Undid …", "Nothing to undo.",
/// "That file disappeared from the inbox" — and it is stale the moment the
/// next document is on screen, so it expires on its own, exactly as the
/// last-action card and the toast beside it already do. A STANDING line
/// states a condition that is still true — "3 files waiting in the inbox" on
/// the Done screen — and must stay until something replaces it.
///
/// Before 2026-08-27 every one of them was a bare assignment with no expiry
/// at all: the line was blanked only by starting, stopping, or finishing a
/// session, so "Undid A → B" sat under the buttons for the rest of the
/// session, describing an action several documents ago.</summary>
public class StatusNoteTests
{
    /// <summary>The reported defect, end to end and with no user input after
    /// the undo. This is the only test here that lets a real timer run —
    /// which is the point: the three below assert that an expiry was ARMED,
    /// and they would all pass just as happily against a timer that never
    /// fires. This one is what grounds them.</summary>
    [Fact]
    public async Task TheUndoNoteDisappearsWithNoFurtherInput()
    {
        using var fx = new ShellFixture();
        fx.AddInboxFile("20240115--111111.pdf");
        fx.AddInboxFile("20240116--222222.pdf");
        fx.Shell.Initialize();
        fx.Shell.StartProcessing();
        fx.Shell.StatusNoteMs = 30;   // production's four seconds, compressed

        fx.Shell.TypedName = "DOE JANE";
        await fx.Shell.OnRouteAsync(0);
        await fx.Shell.OnUndoAsync();
        Assert.StartsWith("Undid ", fx.Shell.StatusLine);

        Assert.True(await Eventually(() => fx.Shell.StatusLine.Length == 0),
            $"the note never cleared itself — it still reads '{fx.Shell.StatusLine}'");
    }

    [Fact]
    public async Task TheNothingToUndoNoteExpiresLikeAnyOther()
    {
        using var fx = new ShellFixture();
        fx.AddInboxFile("20240115--111111.pdf");
        fx.Shell.Initialize();
        fx.Shell.StartProcessing();

        await fx.Shell.OnUndoAsync();   // nothing filed yet
        Assert.Equal("Nothing to undo.", fx.Shell.StatusLine);
        Assert.True(fx.Shell.StatusNoteExpires);

        fx.Shell.ExpireStatusNote();   // what the 4s timer does
        Assert.Equal("", fx.Shell.StatusLine);
    }

    /// <summary>The other half of the distinction: the Done screen's inbox
    /// count is a standing condition, not an event, and the watcher only
    /// re-states it when the folder next changes. Expiring it would blank the
    /// only sign that work is waiting, for as long as nothing else moves.</summary>
    [Fact]
    public async Task TheDoneScreenInboxCountIsNotANoteAndDoesNotExpire()
    {
        using var fx = new ShellFixture();
        fx.AddInboxFile("20240115--111111.pdf");
        fx.Shell.Initialize();
        fx.Shell.StartProcessing();
        fx.Shell.TypedName = "SMITH JOHN";
        await fx.Shell.OnRouteAsync(0);
        Assert.Equal(Screen.Done, fx.Shell.Screen);

        fx.AddInboxFile("20240116--222222.pdf");
        fx.Shell.OnFolderActivity();

        Assert.Equal("1 file waiting in the inbox.", fx.Shell.StatusLine);
        Assert.False(fx.Shell.StatusNoteExpires);

        // and a countdown left over from an earlier note cannot blank it
        fx.Shell.ExpireStatusNote();
        Assert.Equal("1 file waiting in the inbox.", fx.Shell.StatusLine);
    }

    /// <summary>Clearing the line also has to disarm the countdown, or an
    /// expiring note could outlive its own screen and blank a line written
    /// after it — the failure this design would otherwise have introduced.</summary>
    [Fact]
    public async Task StoppingTheSessionCancelsAPendingNotesCountdown()
    {
        using var fx = new ShellFixture();
        fx.AddInboxFile("20240115--111111.pdf");
        fx.Shell.Initialize();
        fx.Shell.StartProcessing();

        await fx.Shell.OnUndoAsync();   // "Nothing to undo.", counting down
        Assert.True(fx.Shell.StatusNoteExpires);

        fx.Shell.StopSession();
        Assert.Equal("", fx.Shell.StatusLine);
        Assert.False(fx.Shell.StatusNoteExpires);
    }

    /// <summary>Polls instead of sleeping a fixed span: it returns the
    /// instant the timer has fired, and only a timer that never fires at all
    /// can burn the whole five-second cap.</summary>
    private static async Task<bool> Eventually(Func<bool> condition)
    {
        for (var i = 0; i < 500 && !condition(); i++) await Task.Delay(10);
        return condition();
    }
}
