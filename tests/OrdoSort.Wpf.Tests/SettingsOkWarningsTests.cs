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

    private SettingsViewModel Build(Action<Config> tweak)
    {
        var cfg = new Config
        {
            Inbox = _dir.Dir("inbox"),
            Deferred = _dir.Dir("set-aside"),
            Routes = { new Route { Label = "Invoices", Path = _dir.Dir("filed") } },
        };
        tweak(cfg);
        return new SettingsViewModel(cfg, new FakeDialogs(), cfgPath: Path.Combine(_dir.Path, "config.json"),
            validateRoute: _ => "", scheduler: new InlineWorkScheduler(), time: new ManualTimeProvider());
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
