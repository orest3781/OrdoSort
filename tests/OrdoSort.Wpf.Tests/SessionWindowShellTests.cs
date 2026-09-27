using OrdoSort.Wpf.ViewModels;

namespace OrdoSort.Wpf.Tests;

/// <summary>The view model side of "processing in its own window"
/// (2026-09-26): the dashboard's Ready content stays true while a session
/// runs, the Start button shows the session instead of starting a second one,
/// and the window gets a moment to open before the first document loads.</summary>
public class SessionWindowShellTests
{
    [Fact]
    public void StartWaitsForTheSessionWindowBeforeLoadingTheFirstDocument()
    {
        using var fx = new ShellFixture();
        fx.AddInboxFile("20240115--111111.pdf");
        var order = new List<string>();
        fx.Shell.PrepareSessionView = () => { order.Add($"prepare, {fx.Viewer.Shown.Count} shown"); return Task.CompletedTask; };
        fx.Shell.Initialize();

        fx.Shell.StartProcessing();

        Assert.Equal(new[] { "prepare, 0 shown" }, order);
        Assert.Single(fx.Viewer.Shown);
    }

    [Fact]
    public void DuringASessionTheStartButtonShowsTheSessionInsteadOfStartingAnother()
    {
        using var fx = new ShellFixture();
        fx.AddInboxFile();
        fx.AddInboxFile();
        fx.Shell.Initialize();
        Assert.Equal("Start processing", fx.Shell.StartButtonText);
        fx.Shell.StartProcessing();
        var shown = 0;
        fx.Shell.ShowSessionRequested += () => shown++;

        Assert.True(fx.Shell.IsSessionOpen);
        Assert.Equal("Processing… (show)", fx.Shell.StartButtonText);
        Assert.True(fx.Shell.StartCommand.CanExecute(null));
        fx.Shell.StartCommand.Execute(null);

        Assert.Equal(1, shown);
        Assert.True(fx.Shell.IsProcessing);   // still the same session
    }

    [Fact]
    public async Task TheDoneSummaryNeverOverwritesTheDashboardsCountLine()
    {
        using var fx = new ShellFixture();
        fx.AddInboxFile();
        fx.Shell.Initialize();
        fx.Shell.StartProcessing();

        await fx.Shell.OnSkipAsync();   // the only document: the session ends

        Assert.True(fx.Shell.IsDone);
        Assert.Equal("Session complete", fx.Shell.DoneTitle);
        Assert.Contains("set aside", fx.Shell.DoneDetail);
        Assert.NotEqual("Session complete", fx.Shell.CountLine);
    }

    /// <summary>Risk 2: filing a document moves it out of the inbox, which
    /// the watcher reports as activity. That must not rescan every monitored
    /// folder mid-session; the poll timer does, at the configured cadence.</summary>
    [Fact]
    public void DuringASessionTilesRefreshOnThePollTimerNotOnInboxActivity()
    {
        var watched = Path.Combine(Path.GetTempPath(), "ordowatch_" + Guid.NewGuid());
        Directory.CreateDirectory(watched);
        using var fx = new ShellFixture(cfg => cfg.WatchFolders.Add(new Core.WatchFolder { Label = "Scanner", Path = watched }));
        try
        {
            File.WriteAllText(Path.Combine(watched, "a.pdf"), "x");
            fx.AddInboxFile();
            fx.AddInboxFile();
            fx.Shell.Initialize();
            fx.Shell.StartProcessing();
            Assert.Equal("1", Assert.Single(fx.Shell.Tiles).CountText);

            File.WriteAllText(Path.Combine(watched, "b.pdf"), "x");
            fx.Shell.OnFolderActivity();                        // what filing a document looks like
            Assert.Equal("1", Assert.Single(fx.Shell.Tiles).CountText);

            fx.Shell.OnPoll();
            fx.Shell.OnFolderActivity();                        // the poll timer's refresh
            Assert.Equal("2", Assert.Single(fx.Shell.Tiles).CountText);
        }
        finally { Directory.Delete(watched, true); }
    }

    /// <summary>Risk 8: the Ready screen and the session screens must not
    /// bind the same view-model text, now that both are on screen at once.
    /// Per-item bindings inside templates (a tile's Label, a route's Back)
    /// are on different objects and are allowed.</summary>
    [Fact]
    public void TheReadyScreenAndTheSessionScreensBindNoCommonText()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (!File.Exists(Path.Combine(root.FullName, "OrdoSort.sln"))) root = root.Parent!;
        var views = Path.Combine(root.FullName, "src", "OrdoSort.Wpf", "Views");
        static HashSet<string> Bound(string file) =>
            System.Text.RegularExpressions.Regex.Matches(File.ReadAllText(file), @"\{Binding ([A-Za-z]+)")
                .Select(m => m.Groups[1].Value).ToHashSet();
        var perItem = new[] { "ActualWidth", "Back", "Fore", "Label", "Display", "Tooltip", "Title", "DataContext", "ElementName" };

        var ready = Bound(Path.Combine(views, "ReadyView.xaml"));
        var session = Bound(Path.Combine(views, "ProcessingView.xaml"));
        session.UnionWith(Bound(Path.Combine(views, "DoneView.xaml")));

        Assert.Empty(ready.Intersect(session).Except(perItem));
    }

    [Fact]
    public void StoppingClosesTheSessionAgain()
    {
        using var fx = new ShellFixture();
        fx.AddInboxFile();
        fx.Shell.Initialize();
        fx.Shell.StartProcessing();

        fx.Shell.StopCommand.Execute(null);

        Assert.False(fx.Shell.IsSessionOpen);
        Assert.Equal("Start processing", fx.Shell.StartButtonText);
    }
}
