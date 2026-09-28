using OrdoSort.Core;
using OrdoSort.Wpf.ViewModels;

namespace OrdoSort.Wpf.Tests;

/// <summary>Q2-02 (refinement checklist, High): Merge and Undo ran every
/// rename on the UI thread before returning. A big batch on a share froze the
/// window, and a frozen window is what gets killed, leaving files renamed
/// with no undo. They now run one file at a time off the UI thread, the way
/// Bulk rename does (QC-04), with the window's other actions held off.</summary>
public class MatchMergeBatchTests : IDisposable
{
    private readonly TempDir _dir = new();

    public void Dispose() => _dir.Dispose();

    // xUnit installs its own synchronization context just before each test
    // method; with it, work the scheduler releases would finish after the
    // asserts. Cleared at the top of each test, released work runs inline.
    private static void NoSynchronizationContext() => SynchronizationContext.SetSynchronizationContext(null);

    /// <summary>Two documents that each match one roster row: ready to merge.</summary>
    private (MatchMergeViewModel Vm, ManualWorkScheduler Scheduler, string A, string B) TwoMatches()
    {
        var scheduler = new ManualWorkScheduler();
        var vm = new MatchMergeViewModel(new Config(), _ => { }, new FakeDialogs(), scheduler: scheduler);
        vm.LoadRosterFrom(_dir.File("roster.csv", "Last,First,Control\nSMITH,JOHN,1111\nJONES,MARY,2222\n"));
        var a = _dir.File("20240101-SMITH-JOHN.pdf", "pdf");
        var b = _dir.File("20240102-JONES-MARY.pdf", "pdf");
        vm.AddFiles(new[] { a, b });
        Assert.Equal(2, vm.MergeCount);
        return (vm, scheduler, a, b);
    }

    [Fact]
    public void MergeRenamesOffTheUiThreadOneFileAtATime()
    {
        NoSynchronizationContext();
        var (vm, scheduler, a, b) = TwoMatches();

        vm.MergeCommand.Execute(null);

        Assert.True(File.Exists(a), "Merge renamed a file on the click itself");
        Assert.True(File.Exists(b));
        Assert.True(vm.IsBusy);
        Assert.Equal("Merging 1 of 2…", vm.Status);

        scheduler.ReleaseAll();

        Assert.False(File.Exists(a));
        Assert.False(File.Exists(b));
        Assert.False(vm.IsBusy);
        Assert.Equal("Merged 2 files.", vm.Status);
        Assert.True(vm.UndoCommand.CanExecute(null));
    }

    [Fact]
    public void WhileMergingNothingElseCanTouchTheList()
    {
        NoSynchronizationContext();
        var (vm, _, _, _) = TwoMatches();

        vm.MergeCommand.Execute(null);

        Assert.False(vm.MergeCommand.CanExecute(null));
        Assert.False(vm.UndoCommand.CanExecute(null));
        Assert.False(vm.ClearCommand.CanExecute(null));
        Assert.False(vm.LoadRosterCommand.CanExecute(null));
        Assert.False(vm.CanReview);
    }

    /// <summary>DW-52: a dropped or picked folder was walked on the UI thread,
    /// so a big folder on a share froze the window while it was read. The
    /// walk now runs on the scheduler, and its files land once it finishes.</summary>
    [Fact]
    public async Task AFolderIsReadOffTheUiThread()
    {
        NoSynchronizationContext();
        var scheduler = new ManualWorkScheduler();
        var vm = new MatchMergeViewModel(new Config(), _ => { }, new FakeDialogs(), scheduler: scheduler);
        var folder = _dir.Dir("scans");
        File.WriteAllText(Path.Combine(folder, "20240101-SMITH-JOHN.pdf"), "pdf");
        File.WriteAllText(Path.Combine(folder, "notes.txt"), "x");

        var adding = vm.AddPathsAsync(new[] { folder });

        Assert.Empty(vm.Rows);   // nothing read on the call itself
        scheduler.ReleaseAll();
        await adding;
        Assert.Equal("20240101-SMITH-JOHN.pdf", Assert.Single(vm.Rows).File);
    }

    /// <summary>DW-52, with Q2-05's rule: Clear pressed while a folder is
    /// still being read clears that add too, rather than the walk finishing
    /// and refilling the list the user just emptied.</summary>
    [Fact]
    public async Task ClearWhileAFolderIsBeingReadDropsThatAdd()
    {
        NoSynchronizationContext();
        var scheduler = new ManualWorkScheduler();
        var vm = new MatchMergeViewModel(new Config(), _ => { }, new FakeDialogs(), scheduler: scheduler);
        var folder = _dir.Dir("scans");
        File.WriteAllText(Path.Combine(folder, "20240101-SMITH-JOHN.pdf"), "pdf");

        var adding = vm.AddPathsAsync(new[] { folder });
        vm.ClearCommand.Execute(null);
        scheduler.ReleaseAll();
        await adding;

        Assert.Empty(vm.Rows);
    }

    [Fact]
    public void UndoPutsTheNamesBackOffTheUiThread()
    {
        NoSynchronizationContext();
        var (vm, scheduler, a, b) = TwoMatches();
        vm.MergeCommand.Execute(null);
        scheduler.ReleaseAll();

        vm.UndoCommand.Execute(null);

        Assert.False(File.Exists(a), "Undo renamed a file on the click itself");
        Assert.True(vm.IsBusy);

        scheduler.ReleaseAll();

        Assert.True(File.Exists(a));
        Assert.True(File.Exists(b));
        Assert.False(vm.IsBusy);
        Assert.Equal("Original names restored.", vm.Status);
    }
}
