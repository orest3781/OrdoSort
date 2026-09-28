namespace OrdoSort.Wpf.Tests;

/// <summary>Q2-24: Open inbox, Open set-aside, the arrival toast's Open and
/// Open backups checked the folder exists and launched Explorer right on the
/// UI thread. The inbox and set-aside folders are the share paths most
/// likely to be dead, and a dead share holds that check for as long as the
/// network takes to give up: the whole window froze on an everyday click.
/// Both now run off the UI thread. A folder that can't be opened used to do
/// nothing at all; the status line now says so.</summary>
public class OpenFolderOffTheUiThreadTests
{
    [Fact]
    public void OpenInboxChecksAndLaunchesOffTheUiThread()
    {
        var scheduler = new ControlledWorkScheduler();
        using var fx = new ShellFixture(scheduler: scheduler);
        var launched = new List<string>();
        fx.Shell.LaunchFolder = launched.Add;
        var queuedBefore = scheduler.Queued;

        fx.Shell.OpenInboxCommand.Execute(null);

        Assert.Empty(launched);   // nothing touched the folder on the click itself
        Assert.Equal(queuedBefore + 1, scheduler.Queued);
        scheduler.ReleaseNewest();
        Assert.Equal(new[] { fx.Inbox }, launched);
    }

    [Fact]
    public void OpenSetAsideChecksAndLaunchesOffTheUiThread()
    {
        var scheduler = new ControlledWorkScheduler();
        using var fx = new ShellFixture(scheduler: scheduler);
        var launched = new List<string>();
        fx.Shell.LaunchFolder = launched.Add;

        fx.Shell.OpenDeferredCommand.Execute(null);

        Assert.Empty(launched);
        scheduler.ReleaseNewest();
        Assert.Equal(new[] { fx.Deferred }, launched);
    }

    [Fact]
    public void AFolderThatCanNotBeOpenedSaysSoInsteadOfDoingNothing()
    {
        using var fx = new ShellFixture(cfg => cfg.Deferred = Path.Combine(Path.GetTempPath(), "ordo_gone_" + Guid.NewGuid()));
        var launched = new List<string>();
        fx.Shell.LaunchFolder = launched.Add;

        fx.Shell.OpenDeferredCommand.Execute(null);

        Assert.Empty(launched);
        Assert.Contains("Can't open the folder — it doesn't exist", fx.Shell.StatusLine);
    }
}
