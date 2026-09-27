using OrdoSort.Core;
using OrdoSort.Wpf.ViewModels;

namespace OrdoSort.Wpf.Tests;

/// <summary>Q2-03 (refinement checklist, High), the Settings half: a
/// set-aside folder or a destination that IS the inbox, however it is spelled,
/// is refused at OK, before it can make Skip or filing "move" documents in
/// place. The filing core refuses such a move too (PipelineTests).</summary>
public class SettingsFolderClashTests : IDisposable
{
    private readonly TempDir _dir = new();

    public void Dispose() => _dir.Dispose();

    private SettingsViewModel Build(Action<Config> tweak)
    {
        var cfg = new Config
        {
            Inbox = _dir.Dir("inbox"),
            Deferred = _dir.Dir("set-aside"),
            Routes = { new Route { Label = "Filed", Path = _dir.Dir("filed") } },
        };
        tweak(cfg);
        return new SettingsViewModel(cfg, new FakeDialogs(), cfgPath: Path.Combine(_dir.Path, "config.json"),
            scheduler: new InlineWorkScheduler(), time: new ManualTimeProvider());
    }

    [Fact]
    public void ASetAsideFolderThatIsTheInboxIsRefused()
    {
        using var vm = Build(cfg => cfg.Deferred = cfg.Inbox.ToUpperInvariant() + Path.DirectorySeparatorChar);

        Assert.Contains(vm.HardErrors(), e => e.Contains("set-aside folder") && e.Contains("inbox"));
    }

    [Fact]
    public void ADestinationThatIsTheInboxIsRefused()
    {
        using var vm = Build(cfg => cfg.Routes[0].Path = "inbox");   // relative: resolved beside config.json

        Assert.Contains(vm.HardErrors(), e => e.Contains("\"Filed\"") && e.Contains("inbox"));
    }

    [Fact]
    public void SeparateFoldersAreFine()
    {
        using var vm = Build(_ => { });

        Assert.DoesNotContain(vm.HardErrors(), e => e.Contains("inbox"));
    }
}
