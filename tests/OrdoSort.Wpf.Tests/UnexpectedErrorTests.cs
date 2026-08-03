using OrdoSort.Core;
using OrdoSort.Wpf.Services;

namespace OrdoSort.Wpf.Tests;

/// <summary>Nothing the filing loop can throw may disappear in silence. An
/// audit write that fails after the move says where the document went; anything
/// else surfaces as a warning and reaches crash.log.</summary>
public class UnexpectedErrorTests
{
    [Fact]
    public void AFailedAuditWriteTellsTheUserWhereTheDocumentWent()
    {
        using var fx = new ShellFixture();
        var src = fx.AddInboxFile("20240115--111111.pdf");
        fx.AddInboxFile("20240115--222222.pdf");
        fx.Shell.Initialize();
        fx.Shell.StartProcessing();

        fx.Shell.History.Dispose();          // audit DB dies mid-session
        fx.Shell.RouteCommand.Execute(0);

        // the move happened, so the user must be told - not left guessing
        var warning = Assert.Single(fx.Dialogs.Warnings);
        Assert.Contains("20240115--111111.pdf", warning.Message + fx.Shell.StatusLine);
        Assert.Contains(fx.RouteDir, warning.Message);
        Assert.False(File.Exists(src));
        Assert.Single(Directory.GetFiles(fx.RouteDir));
    }

    [Fact]
    public void AFailedAuditWriteMovesOnToTheNextDocument()
    {
        using var fx = new ShellFixture();
        fx.AddInboxFile("20240115--111111.pdf");
        fx.AddInboxFile("20240115--222222.pdf");
        fx.Shell.Initialize();
        fx.Shell.StartProcessing();

        fx.Shell.History.Dispose();
        fx.Shell.RouteCommand.Execute(0);

        // regression: the queue used to stay put, so pressing again logged the
        // already-filed document as <vanished>
        Assert.Equal("20240115--222222.pdf", fx.Shell.CurrentFilename);
        Assert.Equal("2 / 2", fx.Shell.ProgressLine);
        Assert.Equal(1, fx.Shell.Session.Filed);
        Assert.Equal(0, fx.Shell.Session.Vanished);
    }

    [Fact]
    public void AFailedAuditWriteChimesTheErrorSound()
    {
        using var fx = new ShellFixture();
        fx.AddInboxFile("20240115--111111.pdf");
        fx.Shell.Initialize();
        fx.Shell.StartProcessing();

        fx.Shell.History.Dispose();
        fx.Shell.RouteCommand.Execute(0);

        Assert.Contains(fx.Sounds.Played, p => p.Evt == SoundEvent.Error);
    }

    [Fact]
    public void AFailedAuditWriteOnSetAsideIsReportedToo()
    {
        using var fx = new ShellFixture();
        fx.AddInboxFile("20240115--111111.pdf");
        fx.AddInboxFile("20240115--222222.pdf");
        fx.Shell.Initialize();
        fx.Shell.StartProcessing();

        fx.Shell.History.Dispose();
        fx.Shell.SkipCommand.Execute(null);

        Assert.Single(fx.Dialogs.Warnings);
        Assert.Equal(1, fx.Shell.Session.Skipped);
        Assert.Equal("20240115--222222.pdf", fx.Shell.CurrentFilename);
    }

    [Fact]
    public void AnUnexpectedExceptionIsReportedInsteadOfSwallowed()
    {
        using var fx = new ShellFixture();
        fx.AddInboxFile("20240115--111111.pdf");
        fx.Shell.Initialize();
        fx.Shell.StartProcessing();

        Exception? logged = null;
        fx.Shell.UnexpectedError += ex => logged = ex;
        // the viewer blowing up mid-commit stands in for any unforeseen fault
        fx.Viewer.ThrowOnRelease = new InvalidOperationException("viewer exploded");
        fx.Shell.RouteCommand.Execute(0);

        Assert.NotNull(logged);
        Assert.Equal("viewer exploded", logged!.Message);

        // The raw exception text goes to the crash.log channel (UnexpectedError,
        // asserted above — MainWindow wires it to App.LogCrash) and NOT into the
        // dialog. Task 9 / audit finding I6: the dialog used to open "Something
        // went wrong" and paste the developer text in, which named neither the
        // action nor its consequence.
        var warning = Assert.Single(fx.Dialogs.Warnings);
        Assert.DoesNotContain("viewer exploded", warning.Message);
        Assert.DoesNotContain("Something went wrong", warning.Message);
        Assert.Contains("Filing that document didn't finish", warning.Message);
        Assert.Contains("crash.log", warning.Message);
    }

    [Fact]
    public void EnterReportsAnUnexpectedExceptionInsteadOfSwallowingIt()
    {
        // Regression: OnEnter used to call OnRouteAsync directly, bypassing
        // RouteCommand and its OnError channel — a button press was protected
        // from an unforeseen fault, but pressing Enter (the app's primary
        // filing gesture) let the exception disappear as an unobserved task.
        using var fx = new ShellFixture();
        fx.AddInboxFile("20240115--111111.pdf");
        fx.Shell.Initialize();
        fx.Shell.StartProcessing();

        Exception? logged = null;
        fx.Shell.UnexpectedError += ex => logged = ex;
        fx.Viewer.ThrowOnRelease = new InvalidOperationException("enter exploded");
        fx.Shell.OnEnter();

        Assert.NotNull(logged);
        Assert.Equal("enter exploded", logged!.Message);
        var warning = Assert.Single(fx.Dialogs.Warnings);
        Assert.DoesNotContain("enter exploded", warning.Message);
        Assert.Contains("Filing that document didn't finish", warning.Message);
    }

    /// <summary>The shared handler used to say "the last action", which was the
    /// most it could say — one <c>Action&lt;Exception&gt;</c> subscribed to
    /// three commands. Each command now names what the user was doing, so the
    /// first line of the dialog matches the button they actually pressed.
    /// Undo is the sharpest case: told "filing didn't finish" after pressing
    /// Undo, a user would look in the wrong folder.</summary>
    [Theory]
    [InlineData("file", "Filing that document didn't finish.")]
    [InlineData("skip", "Setting that document aside didn't finish.")]
    [InlineData("undo", "Undoing the last filing didn't finish.")]
    public void EachActionNamesItselfWhenItFailsUnexpectedly(string action, string expectedOpening)
    {
        using var fx = new ShellFixture();
        fx.AddInboxFile("20240115--111111.pdf");
        fx.AddInboxFile("20240115--222222.pdf");
        fx.Shell.Initialize();
        fx.Shell.StartProcessing();

        switch (action)
        {
            case "file":
                fx.Viewer.ThrowOnRelease = new InvalidOperationException("boom");
                fx.Shell.RouteCommand.Execute(0);
                break;
            case "skip":
                fx.Viewer.ThrowOnRelease = new InvalidOperationException("boom");
                fx.Shell.SkipCommand.Execute(null);
                break;
            case "undo":
                // File one for real first, so there is something to undo.
                // ReleaseAsync is not on the undo path; the fault has to come
                // from loading the restored document back into the viewer.
                fx.Shell.RouteCommand.Execute(0);
                Assert.Empty(fx.Dialogs.Warnings);
                fx.Viewer.ThrowOnShow = new InvalidOperationException("boom");
                fx.Shell.UndoCommand.Execute(null);
                break;
        }

        var warning = Assert.Single(fx.Dialogs.Warnings);
        Assert.StartsWith(expectedOpening, warning.Message);
        Assert.DoesNotContain("boom", warning.Message);
        Assert.Contains("crash.log", warning.Message);
        Assert.Equal("OrdoSort — that didn't finish", warning.Title);
    }

    [Fact]
    public void TheBusyGuardIsReleasedAfterAnUnexpectedFailure()
    {
        using var fx = new ShellFixture();
        fx.AddInboxFile("20240115--111111.pdf");
        fx.Shell.Initialize();
        fx.Shell.StartProcessing();

        fx.Viewer.ThrowOnRelease = new InvalidOperationException("boom");
        fx.Shell.RouteCommand.Execute(0);
        fx.Viewer.ThrowOnRelease = null;

        // a fault must not wedge the session shut
        fx.Shell.RouteCommand.Execute(0);
        Assert.Single(Directory.GetFiles(fx.RouteDir));
    }
}
