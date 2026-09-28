using OrdoSort.Core;
using OrdoSort.Wpf.Services;
using OrdoSort.Wpf.ViewModels;

namespace OrdoSort.Wpf.Tests;

/// <summary>Q2-25: every Browse… in Settings handed the current path to the
/// picker, which first checked it existed, on the UI thread. When that path
/// is on a dead share (exactly when someone clicks Browse… to re-point it)
/// the check held the window until the network gave up. The check now runs
/// off the UI thread and gets half a second: the picker then opens in the
/// current folder if it answered that it exists, and in the default place
/// otherwise.</summary>
public class SettingsBrowseStartTests : IDisposable
{
    private readonly TempDir _dir = new();

    public void Dispose() => _dir.Dispose();

    private (SettingsViewModel Vm, FakeDialogs Dialogs) Build(
        string inbox, IWorkScheduler? scheduler = null, ManualTimeProvider? time = null)
    {
        var dialogs = new FakeDialogs { NextFolder = _dir.Dir("picked") };
        var cfg = new Config { Inbox = inbox, Deferred = _dir.Dir("set-aside") };
        var vm = new SettingsViewModel(cfg, dialogs, cfgPath: Path.Combine(_dir.Path, "config.json"),
            validateRoute: _ => "", writeProblem: _ => "",
            scheduler: scheduler ?? new InlineWorkScheduler(), time: time ?? new ManualTimeProvider());
        return (vm, dialogs);
    }

    [Fact]
    public void BrowseStartsInTheCurrentFolderWhenItIsThere()
    {
        var inbox = _dir.Dir("inbox");
        var (vm, dialogs) = Build(inbox);
        using var _ = vm;

        vm.BrowseInboxCommand.Execute(null);

        Assert.Equal(new[] { inbox }, dialogs.BrowseStarts);
        Assert.Equal(_dir.Dir("picked"), vm.Inbox);
    }

    [Fact]
    public void BrowseDoesNotWaitOnAFolderThatDoesNotAnswer()
    {
        var scheduler = new ControlledWorkScheduler();
        var time = new ManualTimeProvider();
        var (vm, dialogs) = Build(@"\\dead-server\scans\inbox", scheduler: scheduler, time: time);
        using var _ = vm;

        vm.BrowseInboxCommand.Execute(null);
        Assert.Empty(dialogs.BrowseStarts);   // the check is out, the window is free

        time.Advance(TimeSpan.FromSeconds(1));   // it never answered

        Assert.Equal(new string?[] { null }, dialogs.BrowseStarts);
        Assert.Equal(_dir.Dir("picked"), vm.Inbox);
    }

    [Fact]
    public void BrowseStartsInTheDefaultPlaceWhenTheCurrentFolderIsGone()
    {
        var (vm, dialogs) = Build(Path.Combine(_dir.Path, "never-made"));
        using var _ = vm;

        vm.BrowseInboxCommand.Execute(null);

        Assert.Equal(new string?[] { null }, dialogs.BrowseStarts);
    }
}
