using OrdoSort.Wpf.ViewModels;

namespace OrdoSort.Wpf.Tests;

public class DoneAndExportTests
{
    [Fact]
    public async Task DoneSummarizesFiledAndSetAside()
    {
        using var fx = new ShellFixture();
        fx.AddInboxFile("20240115--111111.pdf");
        fx.AddInboxFile("20240116--222222.pdf");
        fx.Shell.Initialize();
        fx.Shell.StartProcessing();

        fx.Shell.TypedName = "SMITH JOHN";
        await fx.Shell.OnRouteAsync(0);
        await fx.Shell.OnSkipAsync();

        Assert.Equal(Screen.Done, fx.Shell.Screen);
        Assert.Equal("Session complete", fx.Shell.CountLine);
        Assert.Equal("1 filed, 1 set aside", fx.Shell.DetailLine);
        Assert.Equal("Every move is in the log.", fx.Shell.LogLine);
    }

    [Fact]
    public async Task FolderActivityOnDoneNotifiesWithoutClobberingTheSummary()
    {
        using var fx = new ShellFixture();
        fx.AddInboxFile("20240115--111111.pdf");
        fx.Shell.Initialize();
        fx.Shell.StartProcessing();
        fx.Shell.TypedName = "SMITH JOHN";
        await fx.Shell.OnRouteAsync(0);
        Assert.Equal(Screen.Done, fx.Shell.Screen);

        // the last commit's own file event fires the watcher moments after
        // the summary appears — it must not replace the summary text
        fx.Shell.OnFolderActivity();
        Assert.Equal("Session complete", fx.Shell.CountLine);
        Assert.Equal("1 filed, 0 set aside", fx.Shell.DetailLine);
        Assert.Equal("Every move is in the log.", fx.Shell.LogLine);
        Assert.Equal("", fx.Shell.StatusLine);   // empty inbox -> no note

        // a NEW arrival while on Done gets a quiet note, summary intact
        fx.AddInboxFile("20240116--222222.pdf");
        fx.Shell.OnFolderActivity();
        Assert.Equal(Screen.Done, fx.Shell.Screen);
        Assert.Equal("Session complete", fx.Shell.CountLine);
        Assert.Equal("1 filed, 0 set aside", fx.Shell.DetailLine);
        Assert.Equal("Every move is in the log.", fx.Shell.LogLine);
        Assert.Equal("1 file waiting in the inbox.", fx.Shell.StatusLine);

        // Back to inbox picks it up
        fx.Shell.RescanCommand.Execute(null);
        Assert.Equal(Screen.Ready, fx.Shell.Screen);
        Assert.Equal("1 file ready", fx.Shell.CountLine);
    }

    [Fact]
    public async Task VanishedFilesAppearInTheSummary()
    {
        using var fx = new ShellFixture();
        var path = fx.AddInboxFile("20240115--111111.pdf");
        fx.Shell.Initialize();
        fx.Shell.StartProcessing();

        File.Delete(path);   // yanked mid-session
        await fx.Shell.OnRouteAsync(0);

        Assert.Equal(Screen.Done, fx.Shell.Screen);
        Assert.Equal("0 filed, 0 set aside, 1 vanished", fx.Shell.DetailLine);
    }

    [Fact]
    public async Task ExportWritesTheAuditCsv()
    {
        using var fx = new ShellFixture();
        fx.AddInboxFile("20240115--111111.pdf");
        fx.Shell.Initialize();
        fx.Shell.StartProcessing();
        fx.Shell.TypedName = "DOE JANE";
        await fx.Shell.OnRouteAsync(0);

        var dest = Path.Combine(fx.Dir, "export.csv");
        fx.Dialogs.NextSaveFile = dest;
        fx.Shell.ExportHistory();

        Assert.True(File.Exists(dest));
        Assert.Contains("DOE JANE", File.ReadAllText(dest));
        Assert.Single(fx.Dialogs.Infos);
    }

    [Fact]
    public void ExportFailureWarnsInsteadOfCrashing()
    {
        using var fx = new ShellFixture();
        fx.Dialogs.NextSaveFile = Path.Combine(fx.Dir, "no-such-dir", "export.csv");
        fx.Shell.ExportHistory();
        Assert.Single(fx.Dialogs.Warnings);
        Assert.Empty(fx.Dialogs.Infos);
    }

    [Fact]
    public void ExportCancelledDoesNothing()
    {
        using var fx = new ShellFixture();
        fx.Dialogs.NextSaveFile = null;
        fx.Shell.ExportHistory();
        Assert.Empty(fx.Dialogs.Warnings);
        Assert.Empty(fx.Dialogs.Infos);
    }

    [Fact]
    public async Task ASessionThatMovedSomethingVouchesForTheLog()
    {
        using var fx = new ShellFixture();
        fx.AddInboxFile("20240115--111111.pdf");
        fx.Shell.Initialize();
        fx.Shell.StartProcessing();
        fx.Shell.TypedName = "SMITH JOHN";
        await fx.Shell.OnRouteAsync(0);

        Assert.Equal("1 filed, 0 set aside", fx.Shell.DetailLine);
        Assert.Equal("Every move is in the log.", fx.Shell.LogLine);
        Assert.True(fx.Shell.HasLogLine);
    }

    [Fact]
    public async Task ASessionThatMovedNothingPromisesNothing()
    {
        using var fx = new ShellFixture();
        var path = fx.AddInboxFile("20240115--111111.pdf");
        fx.Shell.Initialize();
        fx.Shell.StartProcessing();

        File.Delete(path);          // vanished is not a move
        await fx.Shell.OnRouteAsync(0);

        Assert.Equal("0 filed, 0 set aside, 1 vanished", fx.Shell.DetailLine);
        Assert.Equal("", fx.Shell.LogLine);
        Assert.False(fx.Shell.HasLogLine);
    }

    [Fact]
    public async Task AnAuditFailureDuringTheSessionSuppressesTheLogVouch()
    {
        using var fx = new ShellFixture();
        fx.AddInboxFile("20240115--111111.pdf");
        fx.Shell.Initialize();
        fx.Shell.StartProcessing();

        fx.Shell.TypedName = "SMITH JOHN";
        fx.Shell.History.Dispose();   // the history DB dies mid-session
        await fx.Shell.OnRouteAsync(0);

        Assert.Equal(Screen.Done, fx.Shell.Screen);
        // the document really did move — the tally must still say so
        Assert.Equal("1 filed, 0 set aside", fx.Shell.DetailLine);
        // …but the session must not vouch for a log it just failed to write
        Assert.Equal("", fx.Shell.LogLine);
        Assert.False(fx.Shell.HasLogLine);
        // the user was told about the failure
        Assert.Single(fx.Dialogs.Warnings);
        // MarkRouteState() still ran on the AuditError path
        Assert.True(fx.Shell.Routes[0].IsLastUsed);
    }
}
