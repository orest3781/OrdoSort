using OrdoSort.Core;
using OrdoSort.Wpf.ViewModels;

namespace OrdoSort.Wpf.Tests;

/// <summary>QC-18: OK ran every folder check (and each destination's
/// write probe) on the UI thread, so a destination on a dead share froze
/// Settings until the network gave up, and Windows marked it "Not
/// Responding". The checks now run off the UI thread while OK waits.</summary>
public class SettingsOkFolderChecksTests : IDisposable
{
    private readonly TempDir _dir = new();

    public void Dispose() => _dir.Dispose();

    [Fact]
    public async Task OkChecksTheFoldersOffTheUiThreadAndSaysSoMeanwhile()
    {
        var cfg = new Config
        {
            Inbox = _dir.Dir("inbox"),
            Deferred = _dir.Dir("set-aside"),
            Routes = { new Route { Label = "Invoices", Path = @"\\dead-server\share\invoices" } },
        };
        var scheduler = new ControlledWorkScheduler();
        var probes = 0;
        using var vm = new SettingsViewModel(cfg, new FakeDialogs(), cfgPath: Path.Combine(_dir.Path, "config.json"),
            validateRoute: _ => { probes++; return ""; },
            scheduler: scheduler, time: new ManualTimeProvider());
        var queuedBefore = scheduler.Queued;

        var ok = vm.TryBuildResultAsync();

        Assert.False(ok.IsCompleted);
        Assert.True(vm.IsCheckingFolders);
        Assert.Equal(0, probes);   // not on the click itself
        Assert.Equal(queuedBefore + 1, scheduler.Queued);

        scheduler.ReleaseNewest();

        Assert.True(await ok);
        Assert.False(vm.IsCheckingFolders);
        Assert.Equal(1, probes);
        Assert.NotNull(vm.Result);
    }
}
