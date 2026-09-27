using OrdoSort.Core;
using OrdoSort.Wpf.ViewModels;

namespace OrdoSort.Wpf.Tests;

/// <summary>The Settings tile preview counts a watched folder's files
/// (Directory.Exists plus a possibly recursive enumeration). That count must
/// not run inside a keystroke, and a row that is not selected must never
/// probe at all, since only the selected row's preview is shown. Every test
/// owns the clock, so none of this depends on how busy the machine is.</summary>
public class TilePreviewProbeTests : IDisposable
{
    private static readonly TimeSpan PastTheDebounce = TimeSpan.FromSeconds(1);

    private readonly TempDir _dir = new();
    private readonly FakeDialogs _dialogs = new();
    private readonly ManualTimeProvider _time = new();
    private int _statusChecks;

    public void Dispose() => _dir.Dispose();

    private SettingsViewModel Build(Config cfg, int probeDelayMs = 300) =>
        new(cfg, _dialogs,
            folderStatus: (wf, terms) => { _statusChecks++; return FolderMonitor.Status(wf, terms); },
            scheduler: new InlineWorkScheduler(), probeDelayMs: probeDelayMs, time: _time);

    [Fact]
    public void EditingTheSelectedWatchFolderLeavesTheCountUntilTheTypingPauses()
    {
        var vm = Build(new Config());
        vm.AddWatchCommand.Execute(null);   // selects the row it creates
        var w = vm.WatchFolders[0];
        _time.Advance(PastTheDebounce);
        var before = _statusChecks;

        w.Path = _dir.Path;
        Assert.Equal(before, _statusChecks);

        _time.Advance(PastTheDebounce);
        Assert.Equal(before + 1, _statusChecks);
    }

    [Fact]
    public void EditingANonSelectedWatchFolderNeverProbesAtAll()
    {
        var vm = Build(new Config());
        vm.AddWatchCommand.Execute(null);
        var selected = vm.WatchFolders[0];
        selected.Path = _dir.Path;
        vm.AddWatchCommand.Execute(null);
        var other = vm.WatchFolders[1];   // briefly selected by AddWatchCommand itself
        vm.SelectedWatch = selected;      // `other` is now not selected
        _time.Advance(PastTheDebounce);
        Assert.True(_statusChecks > 0, "the selected row's own path edit should have probed");
        var before = _statusChecks;

        // the fields a person edits: label, path, colour, recursive, file types
        other.Path = _dir.Path;
        other.Label = "renamed";
        other.Color = "#c0392b";
        other.Recursive = true;
        other.Filetypes = "pdf";
        _time.Advance(PastTheDebounce);

        Assert.Equal(before, _statusChecks);
    }

    [Fact]
    public void TheTilePreviewReflectsTheRealFolderContents()
    {
        var watched = _dir.Dir("watched");
        _dir.File(Path.Combine("watched", "a.pdf"));
        _dir.File(Path.Combine("watched", "b.pdf"));
        var vm = Build(new Config
        {
            WatchFolders = { new WatchFolder { Label = "Failed", Path = watched } },
        });

        vm.SelectedWatch = vm.WatchFolders[0];
        _time.Advance(PastTheDebounce);

        Assert.Equal("2", vm.TilePreviewCount);
        Assert.Equal("Failed", vm.TilePreviewLabel);
        Assert.Equal("", vm.TilePreviewHint);
    }

    [Fact]
    public void SelectingADifferentWatchResolvesWithoutWaitingForTheDebounce()
    {
        var watched = _dir.Dir("watched");
        _dir.File(Path.Combine("watched", "a.pdf"));
        // A huge debounce window: a selection that waited it out like typed
        // text would leave the count unset below, with no clock movement.
        var vm = Build(new Config
        {
            WatchFolders =
            {
                new WatchFolder { Label = "Empty" },
                new WatchFolder { Label = "Real", Path = watched },
            },
        }, probeDelayMs: 60_000);

        vm.SelectedWatch = vm.WatchFolders[1];

        Assert.Equal("1", vm.TilePreviewCount);
    }
}
