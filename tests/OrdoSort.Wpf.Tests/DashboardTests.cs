using OrdoSort.Core;
using OrdoSort.Wpf.Services;
using OrdoSort.Wpf.Theme;
using OrdoSort.Wpf.ViewModels;

namespace OrdoSort.Wpf.Tests;

public class DashboardTests
{
    // ------------------------------------------------------------ alerts
    [Fact]
    public void ANewAlertingFileChimesFlashesAndToasts()
    {
        using var fx = WithWatchFolder(out var watched);
        fx.Shell.Initialize();                       // seeds silently, folder empty
        var flashed = 0;
        fx.Shell.AlertArrived += () => flashed++;

        File.WriteAllText(Path.Combine(watched, "URGENT-callback.pdf"), "x");
        fx.Shell.OnFolderActivity();                 // the file arrives

        Assert.Contains(fx.Sounds.Played, p => p.Evt == SoundEvent.NewAlert);
        Assert.Equal(1, flashed);
        Assert.True(fx.Shell.ToastVisible);
        Assert.Contains("URGENT-callback.pdf", fx.Shell.ToastDetail);
        Assert.True(fx.Shell.HasActiveAlert);
    }

    [Fact]
    public void PreExistingAlertsAreAdoptedSilentlyOnStartup()
    {
        using var fx = WithWatchFolder(out var watched);
        File.WriteAllText(Path.Combine(watched, "URGENT.pdf"), "x");   // there before launch
        fx.Shell.Initialize();

        Assert.DoesNotContain(fx.Sounds.Played, p => p.Evt == SoundEvent.NewAlert);
        Assert.False(fx.Shell.ToastVisible);          // no toast storm on open
        Assert.True(fx.Shell.HasActiveAlert);         // but the badge still shows
    }

    [Fact]
    public void TheSameAlertDoesNotReChimeOnEveryPoll()
    {
        using var fx = WithWatchFolder(out var watched);
        fx.Shell.Initialize();
        File.WriteAllText(Path.Combine(watched, "URGENT.pdf"), "x");
        fx.Shell.OnFolderActivity();
        fx.Sounds.Played.Clear();

        fx.Shell.OnFolderActivity();                  // poll, nothing new
        fx.Shell.OnFolderActivity();
        Assert.DoesNotContain(fx.Sounds.Played, p => p.Evt == SoundEvent.NewAlert);
    }

    [Fact]
    public void DisablingSoundsSilencesChimesButKeepsTheToast()
    {
        using var fx = WithWatchFolder(out var watched, cfg => cfg.Sounds.Enabled = false);
        fx.Shell.Initialize();
        File.WriteAllText(Path.Combine(watched, "URGENT.pdf"), "x");
        fx.Shell.OnFolderActivity();

        Assert.Empty(fx.Sounds.Played);               // the master toggle gates sound
        Assert.True(fx.Shell.ToastVisible);           // visual alerts are not gated
    }
    private static ShellFixture WithWatchFolder(out string watched,
        Action<Config>? extraTweak = null)
    {
        string? path = null;
        var fx = new ShellFixture(cfg =>
        {
            path = Path.Combine(cfg.Inbox, "..", "watched");
            Directory.CreateDirectory(path);
            cfg.WatchFolders.Add(new WatchFolder
            {
                Label = "Failed faxes",
                Path = path,
                Filetypes = "pdf",
                Color = "#c0392b",
            });
            cfg.AlertTexts.Add("URGENT");
            cfg.MonitorTitle = "Needs attention";
            extraTweak?.Invoke(cfg);
        });
        watched = path!;
        return fx;
    }

    [Fact]
    public void EmptyWatchedFolderShowsNoTile()
    {
        using var fx = WithWatchFolder(out _);
        fx.Shell.Initialize();
        Assert.Empty(fx.Shell.Tiles);
        Assert.False(fx.Shell.DashboardVisible);
    }

    [Fact]
    public void FolderWithFilesGetsATileWithCountAndConfiguredColor()
    {
        using var fx = WithWatchFolder(out var watched);
        File.WriteAllText(Path.Combine(watched, "a.pdf"), "x");
        File.WriteAllText(Path.Combine(watched, "b.pdf"), "x");
        File.WriteAllText(Path.Combine(watched, "ignored.txt"), "x");   // filetype filter
        fx.Shell.Initialize();

        var tile = Assert.Single(fx.Shell.Tiles);
        Assert.Equal("Failed faxes: 2", tile.Display);
        Assert.Equal("Failed faxes", tile.Label);
        Assert.Equal("2", tile.CountText);   // the grid card's big number
        Assert.Equal(new Rgb(192, 57, 43), tile.Back);
        Assert.Equal(ThemePalette.IdealForeground(new Rgb(192, 57, 43)), tile.Fore);
        Assert.True(fx.Shell.DashboardVisible);
        Assert.Equal("Needs attention", fx.Shell.MonitorTitle);
        Assert.False(fx.Shell.FlashRunning);   // nothing alerting
    }

    [Fact]
    public void MissingWatchedFolderShowsAnErrorTile()
    {
        using var fx = WithWatchFolder(out var watched);
        Directory.Delete(watched);
        fx.Shell.Initialize();
        var tile = Assert.Single(fx.Shell.Tiles);
        Assert.StartsWith("Failed faxes: folder not available", tile.Display);
        Assert.Equal("⚠", tile.CountText);
    }

    [Fact]
    public void AlertingTileFlashesRed()
    {
        using var fx = WithWatchFolder(out var watched);
        File.WriteAllText(Path.Combine(watched, "URGENT-callback.pdf"), "x");
        fx.Shell.Initialize();

        var tile = Assert.Single(fx.Shell.Tiles);
        Assert.True(tile.Alerting);
        Assert.Contains("⚠", tile.Display);
        Assert.Equal("1 ⚠", tile.CountText);
        Assert.True(fx.Shell.FlashRunning);

        var baseBack = tile.Back;
        fx.Shell.FlashTick();   // on
        Assert.Equal(ThemePalette.Light.Danger, tile.Back);
        Assert.Equal(ThemePalette.Light.DangerText, tile.Fore);
        fx.Shell.FlashTick();   // off
        Assert.Equal(baseBack, tile.Back);
    }

    [Fact]
    public void SubfolderAlertNamesTheSubfolderOnTheCard()
    {
        using var fx = WithWatchFolder(out var watched,
            cfg => cfg.WatchFolders[0].Recursive = true);
        Directory.CreateDirectory(Path.Combine(watched, "retries"));
        File.WriteAllText(Path.Combine(watched, "retries", "URGENT-fax.pdf"), "x");
        fx.Shell.Initialize();

        var tile = Assert.Single(fx.Shell.Tiles);
        Assert.True(tile.Alerting);
        Assert.Equal("in retries", tile.SubfolderNote);
        Assert.True(tile.HasSubfolderNote);
        Assert.Contains(Path.Combine("retries", "URGENT-fax.pdf"), tile.Tooltip);
    }

    [Fact]
    public void TopLevelAlertShowsNoSubfolderNote()
    {
        using var fx = WithWatchFolder(out var watched);
        File.WriteAllText(Path.Combine(watched, "URGENT.pdf"), "x");
        fx.Shell.Initialize();
        var tile = Assert.Single(fx.Shell.Tiles);
        Assert.True(tile.Alerting);
        Assert.False(tile.HasSubfolderNote);
        Assert.Equal("", tile.SubfolderNote);
    }

    [Fact]
    public void SteadyHighlightWhenFlashingIsDisabled()
    {
        using var fx = WithWatchFolder(out var watched, cfg => cfg.FlashAlerts = false);
        File.WriteAllText(Path.Combine(watched, "URGENT.pdf"), "x");
        fx.Shell.Initialize();

        var tile = Assert.Single(fx.Shell.Tiles);
        Assert.False(fx.Shell.FlashRunning);              // no blinking...
        Assert.Equal(ThemePalette.Light.Danger, tile.Back);   // ...steady red
    }

    [Fact]
    public void InvalidConfiguredColorFallsBackToThemeTile()
    {
        using var fx = WithWatchFolder(out var watched,
            cfg => cfg.WatchFolders[0].Color = "not-a-color");
        File.WriteAllText(Path.Combine(watched, "a.pdf"), "x");
        fx.Shell.Initialize();
        Assert.Equal(ThemePalette.Light.TileDefaultBg, Assert.Single(fx.Shell.Tiles).Back);
    }

    [Fact]
    public void InboxAlertTermTurnsTheCountRedWhileFlashing()
    {
        using var fx = new ShellFixture(cfg => cfg.AlertTexts.Add("555"));
        fx.AddInboxFile("20240115--555123.pdf");
        fx.Shell.Initialize();

        Assert.True(fx.Shell.InboxAlerting);
        Assert.True(fx.Shell.FlashRunning);
        Assert.False(fx.Shell.CountAlertOn);   // flash starts dark
        fx.Shell.FlashTick();
        Assert.True(fx.Shell.CountAlertOn);
    }

    [Fact]
    public void UnchangedPollDoesNotRebuildTiles()
    {
        using var fx = WithWatchFolder(out var watched);
        File.WriteAllText(Path.Combine(watched, "a.pdf"), "x");
        fx.Shell.Initialize();
        var tile = Assert.Single(fx.Shell.Tiles);

        fx.Shell.OnFolderActivity();   // the 30 s poll with nothing new
        fx.Shell.OnFolderActivity();
        Assert.Same(tile, Assert.Single(fx.Shell.Tiles));   // no blink: same tile

        File.WriteAllText(Path.Combine(watched, "b.pdf"), "x");
        fx.Shell.OnFolderActivity();   // a real change rebuilds
        var rebuilt = Assert.Single(fx.Shell.Tiles);
        Assert.NotSame(tile, rebuilt);
        Assert.Equal("2", rebuilt.CountText);
    }

    [Fact]
    public void QuietMonitoringSaysSoInsteadOfVanishing()
    {
        using var fx = WithWatchFolder(out var watched);
        fx.Shell.Initialize();
        Assert.True(fx.Shell.AllQuiet);              // folders watched, nothing up
        Assert.False(fx.Shell.DashboardVisible);

        File.WriteAllText(Path.Combine(watched, "a.pdf"), "x");
        fx.Shell.OnFolderActivity();
        Assert.False(fx.Shell.AllQuiet);             // a tile took over
        Assert.True(fx.Shell.DashboardVisible);
    }

    [Fact]
    public void AllQuietOnlyAppliesToActiveOnlyMonitoring()
    {
        using var bare = new ShellFixture();         // no watch folders at all
        bare.Shell.Initialize();
        Assert.False(bare.Shell.AllQuiet);

        using var hidden = WithWatchFolder(out _, cfg => cfg.TileVisibility = "hidden");
        hidden.Shell.Initialize();
        Assert.False(hidden.Shell.AllQuiet);         // the user asked for silence

        using var all = WithWatchFolder(out _, cfg => cfg.TileVisibility = "all");
        all.Shell.Initialize();
        Assert.False(all.Shell.AllQuiet);            // the zero-count tile shows instead

        using var filing = WithWatchFolder(out _);
        filing.AddInboxFile();
        filing.Shell.Initialize();
        filing.Shell.StartProcessing();
        Assert.False(filing.Shell.AllQuiet);         // never while filing
    }

    [Fact]
    public void AllModeKeepsAnEmptyFolderOnTheDashboard()
    {
        using var fx = WithWatchFolder(out _, cfg => cfg.TileVisibility = "all");
        fx.Shell.Initialize();

        var tile = Assert.Single(fx.Shell.Tiles);
        Assert.Equal("0", tile.CountText);
        Assert.False(tile.Alerting);
        Assert.True(fx.Shell.DashboardVisible);
        Assert.False(fx.Shell.FlashRunning);
    }

    [Fact]
    public void HiddenModeRemovesTilesEvenWithAlertingFiles()
    {
        using var fx = WithWatchFolder(out var watched,
            cfg => cfg.TileVisibility = "hidden");
        File.WriteAllText(Path.Combine(watched, "URGENT.pdf"), "x");
        fx.Shell.Initialize();

        Assert.Empty(fx.Shell.Tiles);
        Assert.False(fx.Shell.DashboardVisible);
        Assert.False(fx.Shell.FlashRunning);   // silenced folders don't flash
    }

    [Fact]
    public void ChangingTileVisibilityRefreshesLiveAndPersists()
    {
        using var fx = WithWatchFolder(out var watched);
        File.WriteAllText(Path.Combine(watched, "a.pdf"), "x");
        fx.Shell.Initialize();
        Assert.Single(fx.Shell.Tiles);           // active + file present
        Assert.True(fx.Shell.TileControlsVisible);

        fx.Shell.TileVisibilityIndex = 2;        // Hidden
        Assert.Empty(fx.Shell.Tiles);
        Assert.False(fx.Shell.DashboardVisible);
        Assert.Equal("hidden", Config.Load(fx.CfgPath).TileVisibility);

        fx.Shell.TileVisibilityIndex = 1;        // All
        Assert.Single(fx.Shell.Tiles);
        Assert.Equal("all", Config.Load(fx.CfgPath).TileVisibility);

        fx.Shell.TileVisibilityIndex = 0;        // back to Active only
        Assert.Single(fx.Shell.Tiles);           // still has a file
        Assert.Equal("active", Config.Load(fx.CfgPath).TileVisibility);
    }

    [Fact]
    public void UnknownTileVisibilityReadsAsActive()
    {
        using var fx = WithWatchFolder(out var watched,
            cfg => cfg.TileVisibility = "banana");
        fx.Shell.Initialize();
        Assert.Empty(fx.Shell.Tiles);            // not "all" — empty stays hidden
        Assert.Equal(0, fx.Shell.TileVisibilityIndex);

        File.WriteAllText(Path.Combine(watched, "a.pdf"), "x");
        fx.Shell.OnFolderActivity();
        Assert.Single(fx.Shell.Tiles);           // not "hidden" — files show
    }

    [Fact]
    public void TileControlsHideWithoutWatchFoldersAndWhileFiling()
    {
        using var bare = new ShellFixture();
        bare.Shell.Initialize();
        Assert.False(bare.Shell.TileControlsVisible);   // nothing to control

        using var fx = WithWatchFolder(out _);
        fx.AddInboxFile();
        fx.Shell.Initialize();
        fx.Shell.StartProcessing();
        Assert.False(fx.Shell.TileControlsVisible);     // hidden while filing
    }

    [Fact]
    public void StartingASessionHidesTheDashboardAndStopsTheFlash()
    {
        using var fx = WithWatchFolder(out var watched);
        File.WriteAllText(Path.Combine(watched, "URGENT.pdf"), "x");
        fx.AddInboxFile();
        fx.Shell.Initialize();
        Assert.True(fx.Shell.FlashRunning);

        fx.Shell.StartProcessing();
        Assert.False(fx.Shell.DashboardVisible);
        Assert.False(fx.Shell.FlashRunning);
    }

    // ------------------------------------------------------------- groups
    [Fact]
    public void TilesGroupBySectionInFirstAppearanceOrder()
    {
        string? incoming1 = null, defaultFolder = null, incoming2 = null;
        using var fx = new ShellFixture(cfg =>
        {
            incoming1 = Path.Combine(cfg.Inbox, "..", "incoming1");
            defaultFolder = Path.Combine(cfg.Inbox, "..", "defaultFolder");
            incoming2 = Path.Combine(cfg.Inbox, "..", "incoming2");
            Directory.CreateDirectory(incoming1);
            Directory.CreateDirectory(defaultFolder);
            Directory.CreateDirectory(incoming2);
            cfg.WatchFolders.Add(new WatchFolder
            { Label = "Incoming A", Path = incoming1, Filetypes = "pdf", Section = "Incoming" });
            cfg.WatchFolders.Add(new WatchFolder
            { Label = "Misc", Path = defaultFolder, Filetypes = "pdf" });   // blank section -> default group
            cfg.WatchFolders.Add(new WatchFolder
            { Label = "Incoming B", Path = incoming2, Filetypes = "pdf", Section = "Incoming" });
            // MonitorTitle stays default ("Monitored folders") — feeds the default group's title
        });
        File.WriteAllText(Path.Combine(incoming1!, "a.pdf"), "x");
        File.WriteAllText(Path.Combine(defaultFolder!, "b.pdf"), "x");
        File.WriteAllText(Path.Combine(incoming2!, "c.pdf"), "x");
        fx.Shell.Initialize();

        Assert.Equal(2, fx.Shell.TileGroups.Count);
        Assert.Equal("Incoming", fx.Shell.TileGroups[0].Title);
        Assert.Equal("Monitored folders", fx.Shell.TileGroups[1].Title);   // default heading
        Assert.Equal(2, fx.Shell.TileGroups[0].Tiles.Count);
        Assert.Single(fx.Shell.TileGroups[1].Tiles);
        // the same instances live in the flat list (flash + sizing rely on it)
        Assert.Equal(fx.Shell.Tiles.Count,
            fx.Shell.TileGroups.Sum(g => g.Tiles.Count));
    }

    [Fact]
    public void AnEmptySectionProducesNoGroup()
    {
        string? busy = null, quiet = null;
        using var fx = new ShellFixture(cfg =>
        {
            busy = Path.Combine(cfg.Inbox, "..", "busy");
            quiet = Path.Combine(cfg.Inbox, "..", "quiet");
            Directory.CreateDirectory(busy);
            Directory.CreateDirectory(quiet);
            cfg.WatchFolders.Add(new WatchFolder
            { Label = "Busy folder", Path = busy, Filetypes = "pdf", Section = "Busy" });
            cfg.WatchFolders.Add(new WatchFolder
            { Label = "Quiet folder", Path = quiet, Filetypes = "pdf", Section = "Quiet" });
        });
        File.WriteAllText(Path.Combine(busy!, "a.pdf"), "x");   // "quiet" stays empty
        fx.Shell.Initialize();

        // active-only default: the empty "Quiet" folder's status is swept out
        // before grouping ever sees it — no empty group appears for it.
        Assert.Single(fx.Shell.TileGroups);
        Assert.Equal("Busy", fx.Shell.TileGroups[0].Title);
    }

    [Fact]
    public void FlashReachesTilesInEveryGroup()
    {
        string? folderA = null, folderB = null;
        using var fx = new ShellFixture(cfg =>
        {
            folderA = Path.Combine(cfg.Inbox, "..", "sectionA");
            folderB = Path.Combine(cfg.Inbox, "..", "sectionB");
            Directory.CreateDirectory(folderA);
            Directory.CreateDirectory(folderB);
            cfg.WatchFolders.Add(new WatchFolder
            { Label = "A", Path = folderA, Filetypes = "pdf", Section = "A" });
            cfg.WatchFolders.Add(new WatchFolder
            { Label = "B", Path = folderB, Filetypes = "pdf", Section = "B" });
            cfg.AlertTexts.Add("URGENT");
        });
        File.WriteAllText(Path.Combine(folderA!, "URGENT-a.pdf"), "x");
        File.WriteAllText(Path.Combine(folderB!, "URGENT-b.pdf"), "x");
        fx.Shell.Initialize();

        Assert.Equal(2, fx.Shell.TileGroups.Count);
        var tileA = Assert.Single(fx.Shell.TileGroups[0].Tiles);
        var tileB = Assert.Single(fx.Shell.TileGroups[1].Tiles);
        Assert.True(tileA.Alerting);
        Assert.True(tileB.Alerting);
        Assert.True(fx.Shell.FlashRunning);

        var baseBackA = tileA.Back;
        var baseBackB = tileB.Back;
        fx.Shell.FlashTick();   // on — the same tick walks Tiles, which both groups share
        Assert.Equal(ThemePalette.Light.Danger, tileA.Back);
        Assert.Equal(ThemePalette.Light.Danger, tileB.Back);
        fx.Shell.FlashTick();   // off
        Assert.Equal(baseBackA, tileA.Back);
        Assert.Equal(baseBackB, tileB.Back);
    }

    [Fact]
    public void RenamingTheSectionHeadingUpdatesTheDefaultGroupLive()
    {
        using var fx = WithWatchFolder(out var watched);   // MonitorTitle = "Needs attention"
        File.WriteAllText(Path.Combine(watched, "a.pdf"), "x");
        fx.Shell.Initialize();

        var group = Assert.Single(fx.Shell.TileGroups);
        Assert.Equal("Needs attention", group.Title);

        fx.Cfg.MonitorTitle = "Inbound queue";
        fx.Shell.OnFolderActivity();   // nothing else changed — only the heading moved

        var renamed = Assert.Single(fx.Shell.TileGroups);
        Assert.Equal("Inbound queue", renamed.Title);
    }

    [Fact]
    public void GroupingIgnoresCaseAndTrimsWhitespaceInSectionNames()
    {
        string? incomingA = null, incomingB = null, failedFolder = null, defaultFolder = null;
        using var fx = new ShellFixture(cfg =>
        {
            incomingA = Path.Combine(cfg.Inbox, "..", "incomingA");
            incomingB = Path.Combine(cfg.Inbox, "..", "incomingB");
            failedFolder = Path.Combine(cfg.Inbox, "..", "failedFolder");
            defaultFolder = Path.Combine(cfg.Inbox, "..", "defaultFolder");
            Directory.CreateDirectory(incomingA);
            Directory.CreateDirectory(incomingB);
            Directory.CreateDirectory(failedFolder);
            Directory.CreateDirectory(defaultFolder);
            cfg.WatchFolders.Add(new WatchFolder
            { Label = "Incoming A", Path = incomingA, Filetypes = "pdf", Section = "Incoming" });
            cfg.WatchFolders.Add(new WatchFolder
            { Label = "Incoming B", Path = incomingB, Filetypes = "pdf", Section = "incoming " });   // case + trailing space
            cfg.WatchFolders.Add(new WatchFolder
            { Label = "Failed", Path = failedFolder, Filetypes = "pdf", Section = "Failed" });
            cfg.WatchFolders.Add(new WatchFolder
            { Label = "Misc", Path = defaultFolder, Filetypes = "pdf" });   // blank -> default group
        });
        File.WriteAllText(Path.Combine(incomingA!, "a.pdf"), "x");
        File.WriteAllText(Path.Combine(incomingB!, "b.pdf"), "x");
        File.WriteAllText(Path.Combine(failedFolder!, "c.pdf"), "x");
        File.WriteAllText(Path.Combine(defaultFolder!, "d.pdf"), "x");
        fx.Shell.Initialize();

        Assert.Equal(3, fx.Shell.TileGroups.Count);
        Assert.Equal("Incoming", fx.Shell.TileGroups[0].Title);   // first-seen casing wins
        Assert.Equal(2, fx.Shell.TileGroups[0].Tiles.Count);
        Assert.Equal("Failed", fx.Shell.TileGroups[1].Title);
        Assert.Single(fx.Shell.TileGroups[1].Tiles);
        Assert.Equal("Monitored folders", fx.Shell.TileGroups[2].Title);   // default heading
        Assert.Single(fx.Shell.TileGroups[2].Tiles);
    }
}
