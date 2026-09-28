using OrdoSort.Core;
using OrdoSort.Wpf.ViewModels;

namespace OrdoSort.Wpf.Tests;

/// <summary>What Settings' OK asks "Save anyway?" about. Each case starts
/// from a config, since a hand-edited or older config.json is where most of
/// these come from.</summary>
public class SettingsOkWarningsTests : IDisposable
{
    private readonly TempDir _dir = new();

    public void Dispose() => _dir.Dispose();

    private SettingsViewModel Build(Action<Config> tweak, Func<string, string>? writeProblem = null)
    {
        var cfg = new Config
        {
            Inbox = _dir.Dir("inbox"),
            Deferred = _dir.Dir("set-aside"),
            Routes = { new Route { Label = "Invoices", Path = _dir.Dir("filed") } },
        };
        tweak(cfg);
        return new SettingsViewModel(cfg, new FakeDialogs(), cfgPath: Path.Combine(_dir.Path, "config.json"),
            validateRoute: _ => "", writeProblem: writeProblem ?? (_ => ""),
            scheduler: new InlineWorkScheduler(), time: new ManualTimeProvider());
    }

    /// <summary>DW-45: "folder doesn't exist" read the same whether the
    /// folder was never made or its share or drive was simply offline, and
    /// the fixes differ (create it, or wait and reconnect). When the drive or
    /// share itself can't be reached, the note and the OK warning now say
    /// that instead.</summary>
    [Fact]
    public void AFolderOnAShareThatCanNotBeReachedSaysSoInsteadOfDoesNotExist()
    {
        const string offline = @"\\scanner-server\scans\inbox";
        var cfg = new Config
        {
            Inbox = offline,
            Deferred = _dir.Dir("set-aside"),
            WatchFolders = { new WatchFolder { Label = "Faxes", Path = @"\\scanner-server\scans\fax" } },
        };
        var reachable = new[] { _dir.Path, Path.GetPathRoot(_dir.Path)! };
        using var vm = new SettingsViewModel(cfg, new FakeDialogs(), cfgPath: Path.Combine(_dir.Path, "config.json"),
            directoryExists: p => reachable.Any(r => p.StartsWith(r, StringComparison.OrdinalIgnoreCase)),
            validateRoute: _ => "", writeProblem: _ => "",
            folderStatus: (w, _) => new FolderMonitor.FolderStatus(w.Label, w.Path, w.Color, 0, "", Array.Empty<string>(), w.Section),
            scheduler: new InlineWorkScheduler(), time: new ManualTimeProvider());

        Assert.Contains(@"can't reach \\scanner-server\scans", vm.InboxNote);
        Assert.DoesNotContain("doesn't exist", vm.InboxNote);
        Assert.Contains(vm.Warnings(), w => w.Contains("inbox") && w.Contains(@"can't reach \\scanner-server\scans"));
        Assert.Contains(vm.Warnings(), w => w.Contains("Faxes") && w.Contains("can't reach"));
    }

    /// <summary>DW-45: a folder that is really missing on a reachable drive
    /// still says it doesn't exist.</summary>
    [Fact]
    public void AMissingFolderOnAReachableDriveStillSaysItDoesNotExist()
    {
        var missing = Path.Combine(_dir.Path, "never-made");
        using var vm = Build(cfg => cfg.Inbox = missing);

        Assert.Contains("doesn't exist", vm.InboxNote);
    }

    /// <summary>DW-08: only destinations were checked for write access at
    /// OK. A set-aside folder or inbox the station can read but not change
    /// passed, and then every Skip, or every filing (a move out of the inbox
    /// deletes there), failed with "access denied". Both are now checked the
    /// way destinations are.</summary>
    [Fact]
    public void ASetAsideFolderThatCanNotBeWrittenIsWarnedAbout()
    {
        var setAside = _dir.Dir("set-aside");
        using var vm = Build(cfg => { }, writeProblem: path => path == setAside ? "Access is denied." : "");

        Assert.Contains(vm.Warnings(), w => w.Contains("set-aside folder") && w.Contains("Access is denied."));
    }

    [Fact]
    public void AnInboxThatCanNotBeChangedIsWarnedAbout()
    {
        var inbox = _dir.Dir("inbox");
        using var vm = Build(cfg => { }, writeProblem: path => path == inbox ? "Access is denied." : "");

        Assert.Contains(vm.Warnings(), w => w.Contains("inbox") && w.Contains("Access is denied."));
    }

    /// <summary>Q2-41: a monitored folder with no path passed OK without a
    /// word and became a tile that only ever shows an error, and whose click
    /// does nothing. A destination with no folder is warned about; this now
    /// is too.</summary>
    [Fact]
    public void AMonitoredFolderWithNoPathIsWarnedAbout()
    {
        using var vm = Build(cfg => cfg.WatchFolders.Add(new WatchFolder { Label = "Faxes", Path = "  " }));

        Assert.Contains(vm.Warnings(), w => w.Contains("\"Faxes\"") && w.Contains("no folder"));
    }

    /// <summary>Q2-43: on a station that never uses a set-aside folder,
    /// every OK asked "Save anyway?" about it, which teaches people to click
    /// through the prompt that also carries real warnings. It is asked only
    /// when this edit is what cleared the folder.</summary>
    [Fact]
    public void AStationThatNeverSetASetAsideFolderIsNotAskedAboutItEachTime()
    {
        using var vm = Build(cfg => cfg.Deferred = "");

        Assert.DoesNotContain(vm.Warnings(), w => w.Contains("set-aside"));
    }

    [Fact]
    public void ClearingTheSetAsideFolderIsStillWarnedAbout()
    {
        using var vm = Build(cfg => { });
        vm.Deferred = "";

        Assert.Contains(vm.Warnings(), w => w.Contains("No set-aside folder is set"));
    }

    /// <summary>DW-21: two destinations with one label are refused, but two
    /// monitored folders with one label in the same section passed, giving
    /// two identical tiles and alerts that can't be told apart. The same
    /// label in different sections is fine: the section heading tells them
    /// apart.</summary>
    [Fact]
    public void TwoMonitoredFoldersWithOneLabelInOneSectionAreWarnedAbout()
    {
        using var vm = Build(cfg =>
        {
            cfg.WatchFolders.Add(new WatchFolder { Label = "Faxes", Path = _dir.Dir("fax1") });
            cfg.WatchFolders.Add(new WatchFolder { Label = "faxes", Path = _dir.Dir("fax2") });
        });

        Assert.Contains(vm.Warnings(), w => w.Contains("faxes", StringComparison.OrdinalIgnoreCase) && w.Contains("same name"));
    }

    [Fact]
    public void OneLabelInTwoSectionsIsNotWarnedAbout()
    {
        using var vm = Build(cfg =>
        {
            cfg.WatchFolders.Add(new WatchFolder { Label = "Faxes", Path = _dir.Dir("fax1"), Section = "North" });
            cfg.WatchFolders.Add(new WatchFolder { Label = "Faxes", Path = _dir.Dir("fax2"), Section = "South" });
        });

        Assert.DoesNotContain(vm.Warnings(), w => w.Contains("same name"));
    }

    /// <summary>DW-41: every other file setting is checked at OK, but a
    /// custom sound was not. A moved or deleted .wav saved silently, and the
    /// app then played its own sound instead, with nothing saying why.</summary>
    [Fact]
    public void ACustomSoundFileThatIsGoneIsWarnedAbout()
    {
        var gone = Path.Combine(_dir.Path, "ding.wav");
        using var vm = Build(cfg => cfg.Sounds.NewAlert = gone);

        Assert.Contains(vm.Warnings(), w => w.Contains("New alert") && w.Contains(gone));
    }

    [Fact]
    public void ACustomSoundFileThatIsThereIsNotWarnedAbout()
    {
        var there = _dir.File("ding.wav");
        using var vm = Build(cfg => cfg.Sounds.NewAlert = there);

        Assert.DoesNotContain(vm.Warnings(), w => w.Contains("sound"));
    }
}
