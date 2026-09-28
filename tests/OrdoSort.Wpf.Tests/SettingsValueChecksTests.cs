using OrdoSort.Core;
using OrdoSort.Wpf.ViewModels;

namespace OrdoSort.Wpf.Tests;

/// <summary>Values Settings used to accept at OK that make every filing
/// fail later, with nothing in Settings saying why. A hand-edited
/// config.json is the usual source, so each case starts from the config.</summary>
public class SettingsValueChecksTests : IDisposable
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
            scheduler: new InlineWorkScheduler(), time: new ManualTimeProvider());
    }

    /// <summary>QC-17: a ":" in a suffix saved fine, then every document
    /// filed to that destination failed with "The name can't contain ':'".</summary>
    [Fact]
    public void ASuffixWindowsCannotPutInAFileNameIsRefused()
    {
        using var vm = Build(cfg =>
        {
            cfg.Routes[0].Suffix = "_INV:2024";
            cfg.Routes[0].AppendSuffix = true;
        });

        Assert.Contains(vm.HardErrors(), e => e.Contains("\"Invoices\"") && e.Contains("suffix") && e.Contains(":"));
    }

    [Fact]
    public void AnOrdinarySuffixIsAccepted()
    {
        using var vm = Build(cfg =>
        {
            cfg.Routes[0].Suffix = "_INV";
            cfg.Routes[0].AppendSuffix = true;
        });

        Assert.DoesNotContain(vm.HardErrors(), e => e.Contains("suffix"));
    }

    /// <summary>D4: an unknown naming mode (hand-edited) showed as a blank
    /// choice and stayed saved; every filing to that destination then failed
    /// with "Unknown naming mode".</summary>
    [Fact]
    public void AnUnknownNamingModeOnADestinationIsRefused()
    {
        using var vm = Build(cfg => cfg.Routes[0].NamingMode = "overwrite");

        Assert.Contains(vm.HardErrors(), e => e.Contains("\"Invoices\"") && e.Contains("overwrite"));
    }

    [Fact]
    public void AnUnknownFilingModeIsRefused()
    {
        using var vm = Build(cfg => cfg.NamingMode = "overwrite");

        Assert.Contains(vm.HardErrors(), e => e.Contains("Filing") && e.Contains("overwrite"));
    }

    /// <summary>D4: a font that isn't one of the choices showed as a blank
    /// choice with the bad value still saved.</summary>
    [Fact]
    public void AFontThatIsNotOneOfTheChoicesIsRefused()
    {
        using var vm = Build(cfg => cfg.UiFontFamily = "Comic Papyrus");

        Assert.Contains(vm.HardErrors(), e => e.Contains("Comic Papyrus"));
    }

    /// <summary>Q2-40: a blank history database passed OK and was saved
    /// blank, which then stopped the app starting ("unable to open database
    /// file"), while the box-labels file, blanked, falls back to its default.
    /// The history database now does the same, and its note says so instead
    /// of going quiet.</summary>
    [Fact]
    public async Task ABlankHistoryDatabaseSavesAsTheDefault()
    {
        using var vm = Build(cfg => { });
        vm.HistoryDb = "   ";

        Assert.Contains(Config.DefaultHistoryDb, vm.HistoryDbNote);
        Assert.True(await vm.TryBuildResultAsync());
        Assert.Equal(Config.DefaultHistoryDb, vm.Result!.HistoryDb);
    }

    /// <summary>Q2-40: a history database pointing at a folder said nothing
    /// in Settings.</summary>
    [Fact]
    public void AHistoryDatabaseThatIsAFolderIsFlagged()
    {
        var folder = _dir.Dir("history-folder");
        using var vm = Build(cfg => cfg.HistoryDb = folder);

        Assert.True(vm.HistoryDbNoteNeedsAttention);
        Assert.Contains("folder", vm.HistoryDbNote);
    }
}
