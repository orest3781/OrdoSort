using OrdoSort.Core;
using OrdoSort.Wpf.ViewModels;

namespace OrdoSort.Wpf.Tests;

/// <summary>Owner request 2026-09-29: the processing screen counts the files
/// still to go, and a setting keeps files that arrive mid-session out of it.</summary>
public class SessionArrivalsTests
{
    private const string First = "20240115--111111.pdf";
    private const string Second = "20240116--222222.pdf";
    private const string Arrival = "20240117--333333.pdf";

    private static ShellFixture Started(bool addNewFiles, params string[] files)
    {
        var fx = new ShellFixture(c => c.AddNewFilesToSession = addNewFiles);
        foreach (var f in files) fx.AddInboxFile(f);
        fx.Shell.Initialize();
        fx.Shell.StartProcessing();
        return fx;
    }

    [Theory]
    [InlineData(0, "0 files left")]
    [InlineData(1, "1 file left")]
    [InlineData(2, "2 files left")]
    [InlineData(-1, "0 files left")]
    public void FilesLeftReadsNaturally(int left, string expected) =>
        Assert.Equal(expected, ShellViewModel.FilesLeftText(left));

    [Fact]
    public async Task TheCountGoesDownAsDocumentsAreFiled()
    {
        using var fx = Started(addNewFiles: true, First, Second);
        Assert.Equal("2 files left", fx.Shell.ProgressLine);

        await fx.Shell.OnRouteAsync(0);
        Assert.Equal("1 file left", fx.Shell.ProgressLine);

        await fx.Shell.OnSkipAsync();
        Assert.Equal(Screen.Done, fx.Shell.Screen);
    }

    [Fact]
    public void ByDefaultANewFileJoinsTheSessionAndTheCountGoesUp()
    {
        using var fx = Started(addNewFiles: true, First);
        fx.AddInboxFile(Arrival);

        fx.Shell.OnFolderActivity();

        Assert.Equal(2, fx.Shell.Session.Total);
        Assert.Equal("2 files left", fx.Shell.ProgressLine);
        Assert.Contains("added to this session", fx.Shell.StatusLine);
    }

    [Fact]
    public async Task WithTheSettingOffANewFileWaitsForTheNextSession()
    {
        using var fx = Started(addNewFiles: false, First);
        fx.AddInboxFile(Arrival);

        fx.Shell.OnFolderActivity();

        Assert.Equal(1, fx.Shell.Session.Total);
        Assert.Equal("1 file left", fx.Shell.ProgressLine);
        Assert.Contains("waits for the next session", fx.Shell.StatusLine);

        await fx.Shell.OnRouteAsync(0);

        Assert.Equal(Screen.Done, fx.Shell.Screen);   // the arrival was never filed
        Assert.True(File.Exists(Path.Combine(fx.Inbox, Arrival)));
    }

    [Fact]
    public void TheNextSessionPicksUpWhatWaited()
    {
        using var fx = Started(addNewFiles: false, First);
        fx.AddInboxFile(Arrival);
        fx.Shell.OnFolderActivity();

        fx.Shell.StopSession();
        fx.Shell.StartProcessing();

        Assert.Equal(2, fx.Shell.Session.Total);
    }

    [Fact]
    public void AnArrivalIsAnnouncedOnceNotAtEveryScan()
    {
        using var fx = Started(addNewFiles: false, First);
        fx.AddInboxFile(Arrival);
        fx.Shell.OnFolderActivity();
        fx.Shell.ExpireStatusNote();

        fx.Shell.OnFolderActivity();   // the next scan finds the same file

        Assert.DoesNotContain("next session", fx.Shell.StatusLine);
    }
}
