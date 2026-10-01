using OrdoSort.Core;
using OrdoSort.Wpf.Services;
using OrdoSort.Wpf.ViewModels;
using static OrdoSort.Core.BulkRename;

namespace OrdoSort.Wpf.Tests;

/// <summary>StandardiseNamesViewModel (owner request 2026-09-29: preview,
/// then Rename; letter case; separator; date options; segment chips),
/// through the real Standardise.Plan and BulkRename.Execute/Revert on a
/// temp folder. The naming rule itself is pinned in StandardiseTests; these
/// facts are about the window's flow: what is shown, when the disk changes,
/// what each control reaches.</summary>
public class StandardiseNamesViewModelTests : IDisposable
{
    private const string Date = "20260115";
    private readonly TempDir _dir = new();
    public void Dispose() => _dir.Dispose();

    private static StandardiseNamesViewModel NewViewModel(IWorkScheduler? scheduler = null) =>
        new(scheduler ?? new InlineWorkScheduler()) { DateText = Date };

    private string OnDisk(string name) => Path.Combine(_dir.Path, name);

    // ---------------------------------------------------------- preview

    [Fact]
    public async Task AddingAFileShowsItsNewNameAndRenamesNothing()
    {
        var src = _dir.File("smith, john_A12345.pdf");
        var vm = NewViewModel();

        await vm.AddFilesAsync(new[] { src });

        var row = Assert.Single(vm.Results);
        Assert.Equal("smith, john_A12345.pdf", row.Current);
        Assert.Equal("20260115-SMITH-JOHN-A12345.pdf", row.Result);
        Assert.Equal(StandardiseRowStatus.Pending, row.Status);
        Assert.True(File.Exists(src));
        Assert.Equal(1, vm.PendingCount);
        Assert.Equal("Rename 1 file", vm.RenameButtonText);
        Assert.True(vm.RenameCommand.CanExecute(null));
    }

    [Fact]
    public void TheDateStartsAsToday()
    {
        var vm = new StandardiseNamesViewModel(new InlineWorkScheduler());

        Assert.Equal(DateTime.Today.ToString("yyyyMMdd"), vm.DateText);
        Assert.True(vm.IsDateValid);
    }

    [Fact]
    public async Task AnAlreadyStandardisedFileIsShownAsSuchAndNotCounted()
    {
        var done = _dir.File("20260115-SMITH-JOHN.pdf");
        var messy = _dir.File("jones mary.pdf");
        var vm = NewViewModel();

        await vm.AddFilesAsync(new[] { done, messy });

        var doneRow = vm.Results.Single(r => r.CurrentPath == done);
        Assert.Equal(StandardiseRowStatus.Unchanged, doneRow.Status);
        Assert.Equal("Already standardised", doneRow.Result);
        Assert.Equal(1, vm.PendingCount);
    }

    [Fact]
    public async Task AddNoteSaysWhatTheDropContained()
    {
        var real = _dir.File("smith.pdf");
        var missing = OnDisk("does-not-exist.pdf");
        var vm = NewViewModel();

        await vm.AddFilesAsync(new[] { real, missing });

        Assert.Equal("1 added · 1 ignored (1 doesn't exist)", vm.AddNote);
    }

    [Fact]
    public async Task AddingTheSameFileTwiceListsItOnce()
    {
        var src = _dir.File("smith.pdf");
        var vm = NewViewModel();

        await vm.AddFilesAsync(new[] { src });
        await vm.AddFilesAsync(new[] { src });

        Assert.Single(vm.Results);
    }

    [Fact]
    public async Task TwoFilesThatWouldGetOneNameShowTheCounterBeforeRenaming()
    {
        var a = _dir.File("smith.pdf");
        var b = _dir.File("SMITH_.pdf");
        var vm = NewViewModel();

        await vm.AddFilesAsync(new[] { a, b });

        Assert.Equal("20260115-SMITH.pdf", vm.Results[0].Result);
        Assert.StartsWith("20260115-SMITH-2.pdf", vm.Results[1].Result);
        Assert.Contains("counter", vm.Results[1].Result);
    }

    [Fact]
    public async Task ClearEmptiesTheList()
    {
        var vm = NewViewModel();
        await vm.AddFilesAsync(new[] { _dir.File("smith.pdf") });

        vm.ClearCommand.Execute(null);

        Assert.Empty(vm.Results);
        Assert.Equal(0, vm.PendingCount);
        Assert.False(vm.ClearCommand.CanExecute(null));
        Assert.False(vm.RenameCommand.CanExecute(null));
    }

    // ---------------------------------------------------------- controls

    [Theory]
    [InlineData(NameCase.Upper, "20260115-SMITH-JOHN.pdf")]
    [InlineData(NameCase.Title, "20260115-Smith-John.pdf")]
    [InlineData(NameCase.AsIs, "20260115-smith-john.pdf")]
    public async Task TheLetterCaseChangesThePreview(NameCase nameCase, string expected)
    {
        var vm = NewViewModel();
        await vm.AddFilesAsync(new[] { _dir.File("smith john.pdf") });

        vm.Case = nameCase;

        Assert.Equal(expected, Assert.Single(vm.Results).Result);
    }

    [Theory]
    [InlineData(SegmentJoin.Dash, "20260115-SMITH-JOHN.pdf")]
    [InlineData(SegmentJoin.Underscore, "20260115_SMITH_JOHN.pdf")]
    [InlineData(SegmentJoin.Space, "20260115 SMITH JOHN.pdf")]
    public async Task TheSeparatorChangesThePreview(SegmentJoin separator, string expected)
    {
        var vm = NewViewModel();
        await vm.AddFilesAsync(new[] { _dir.File("smith_john.pdf") });

        vm.Separator = separator;

        Assert.Equal(expected, Assert.Single(vm.Results).Result);
    }

    [Theory]
    [InlineData(DatePlacement.Front, "20260115-SMITH.pdf")]
    [InlineData(DatePlacement.Back, "SMITH-20260115.pdf")]
    [InlineData(DatePlacement.None, "SMITH.pdf")]
    public async Task TheDatesPlaceChangesThePreview(DatePlacement placement, string expected)
    {
        var vm = NewViewModel();
        await vm.AddFilesAsync(new[] { _dir.File("20251201-smith.pdf") });

        vm.DatePlacement = placement;

        Assert.Equal(expected, Assert.Single(vm.Results).Result);
    }

    [Fact]
    public async Task TheDateAlreadyInTheNameCanBeKept()
    {
        var vm = NewViewModel();
        await vm.AddFilesAsync(new[] { _dir.File("20251201_smith.pdf") });

        vm.DateSource = DateSource.InName;

        Assert.Equal("20251201-SMITH.pdf", Assert.Single(vm.Results).Result);
    }

    [Fact]
    public async Task TheFilesModifiedDateCanBeUsed()
    {
        var src = _dir.File("smith.pdf");
        File.SetLastWriteTime(src, new DateTime(2024, 3, 17, 10, 0, 0));
        var vm = NewViewModel();
        await vm.AddFilesAsync(new[] { src });

        vm.DateSource = DateSource.Modified;

        Assert.Equal("20240317-SMITH.pdf", Assert.Single(vm.Results).Result);
    }

    [Fact]
    public async Task ANewTypedDateChangesThePreview()
    {
        var vm = NewViewModel();
        await vm.AddFilesAsync(new[] { _dir.File("smith.pdf") });

        vm.DateText = "20251231";

        Assert.Equal("20251231-SMITH.pdf", Assert.Single(vm.Results).Result);
    }

    [Theory]
    [InlineData("2026011")]
    [InlineData("20260230")]
    [InlineData("tomorrow")]
    public async Task ADateThatIsNotRealHoldsRenameBackAndSaysWhy(string typed)
    {
        var vm = NewViewModel();
        await vm.AddFilesAsync(new[] { _dir.File("smith.pdf") });

        vm.DateText = typed;

        Assert.False(vm.IsDateValid);
        Assert.True(vm.HasDateError);
        Assert.Contains("YYYYMMDD", vm.DateError);
        Assert.False(vm.RenameCommand.CanExecute(null));
    }

    [Fact]
    public async Task WithNoDateTheDateBoxCannotHoldRenameBack()
    {
        var vm = NewViewModel();
        await vm.AddFilesAsync(new[] { _dir.File("smith.pdf") });
        vm.DateText = "nonsense";

        vm.DatePlacement = DatePlacement.None;

        Assert.True(vm.IsDateValid);
        Assert.False(vm.UsesDate);
        Assert.Equal("SMITH.pdf", Assert.Single(vm.Results).Result);
        Assert.True(vm.RenameCommand.CanExecute(null));
    }

    // ---------------------------------------------------------- word chips

    [Fact]
    public async Task WithNothingSelectedTheChipsShowTheFirstFileAndReachEveryFile()
    {
        var vm = NewViewModel();
        await vm.AddFilesAsync(new[] { _dir.File("smith john scan.pdf"), _dir.File("jones mary scan.pdf") });

        Assert.Equal(new[] { "smith", "john", "scan" }, vm.SegmentChips.Select(c => c.Text));
        Assert.Contains("all 2 files", vm.SegmentBarCaption);

        vm.SegmentChips[2].IsKept = false;

        Assert.Equal("20260115-SMITH-JOHN.pdf", vm.Results[0].Result);
        Assert.Equal("20260115-JONES-MARY.pdf", vm.Results[1].Result);
    }

    [Fact]
    public async Task WithFilesSelectedTheChipsReachOnlyThoseFiles()
    {
        var vm = NewViewModel();
        await vm.AddFilesAsync(new[] { _dir.File("smith john scan.pdf"), _dir.File("jones mary scan.pdf") });

        vm.SelectedRows = new[] { vm.Results[1] };

        Assert.Equal(new[] { "jones", "mary", "scan" }, vm.SegmentChips.Select(c => c.Text));
        Assert.Contains("this file", vm.SegmentBarCaption);

        vm.SegmentChips[1].IsKept = false;

        Assert.Equal("20260115-SMITH-JOHN-SCAN.pdf", vm.Results[0].Result);
        Assert.Equal("20260115-JONES-SCAN.pdf", vm.Results[1].Result);
    }

    [Fact]
    public async Task KeepAllWordsPutsDroppedWordsBack()
    {
        var vm = NewViewModel();
        await vm.AddFilesAsync(new[] { _dir.File("smith john.pdf") });
        vm.SegmentChips[1].IsKept = false;
        Assert.Equal("20260115-SMITH.pdf", Assert.Single(vm.Results).Result);

        vm.ResetSegmentsCommand.Execute(null);

        Assert.Equal("20260115-SMITH-JOHN.pdf", Assert.Single(vm.Results).Result);
        Assert.All(vm.SegmentChips, chip => Assert.True(chip.IsKept));
    }

    [Fact]
    public async Task ASelectedFilesDroppedWordsShowAsDroppedChips()
    {
        var vm = NewViewModel();
        await vm.AddFilesAsync(new[] { _dir.File("smith john.pdf") });
        vm.SegmentChips[0].IsKept = false;

        vm.SelectedRows = new[] { vm.Results[0] };

        Assert.False(vm.SegmentChips[0].IsKept);
        Assert.True(vm.SegmentChips[1].IsKept);
    }

    [Fact]
    public async Task DroppingEveryWordWithNoDateIsSkippedWithAReason()
    {
        var vm = NewViewModel();
        await vm.AddFilesAsync(new[] { _dir.File("smith.pdf") });
        vm.DatePlacement = DatePlacement.None;

        vm.SegmentChips[0].IsKept = false;

        var row = Assert.Single(vm.Results);
        Assert.Equal(StandardiseRowStatus.Skipped, row.Status);
        Assert.StartsWith("Skipped:", row.Result);
        Assert.Equal(0, vm.PendingCount);
    }

    // ---------------------------------------------------------- rename, undo

    [Fact]
    public async Task RenameAppliesWhatThePreviewShowed()
    {
        var src = _dir.File("smith john.pdf");
        var vm = NewViewModel();
        await vm.AddFilesAsync(new[] { src });
        vm.Case = NameCase.Title;
        vm.Separator = SegmentJoin.Space;
        var shown = Assert.Single(vm.Results).Result;

        vm.RenameCommand.Execute(null);
        await vm.RenameCommand.Completion;

        Assert.Equal("20260115 Smith John.pdf", shown);
        Assert.False(File.Exists(src));
        Assert.True(File.Exists(OnDisk(shown)));
        var row = Assert.Single(vm.Results);
        Assert.Equal(StandardiseRowStatus.Renamed, row.Status);
        Assert.Equal(shown, row.Current);
        Assert.Equal("Renamed 1 file.", vm.Status);
        Assert.Equal(0, vm.PendingCount);
        Assert.True(vm.UndoCommand.CanExecute(null));
    }

    [Fact]
    public async Task RenameLeavesUnchangedAndSkippedFilesAlone()
    {
        var done = _dir.File("20260115-SMITH.pdf");
        var messy = _dir.File("jones.pdf");
        var vm = NewViewModel();
        await vm.AddFilesAsync(new[] { done, messy });

        vm.RenameCommand.Execute(null);
        await vm.RenameCommand.Completion;

        Assert.True(File.Exists(done));
        Assert.True(File.Exists(OnDisk("20260115-JONES.pdf")));
        Assert.Equal(StandardiseRowStatus.Unchanged, vm.Results[0].Status);
        Assert.Equal("Renamed 1 file.", vm.Status);
    }

    [Fact]
    public async Task AClashRenamesWithTheCounterThePreviewShowed()
    {
        var vm = NewViewModel();
        await vm.AddFilesAsync(new[] { _dir.File("smith.pdf"), _dir.File("SMITH_.pdf") });

        vm.RenameCommand.Execute(null);
        await vm.RenameCommand.Completion;

        Assert.True(File.Exists(OnDisk("20260115-SMITH.pdf")));
        Assert.True(File.Exists(OnDisk("20260115-SMITH-2.pdf")));
    }

    [Fact]
    public async Task UndoPutsTheNamesBackAndShowsThePreviewAgain()
    {
        var src = _dir.File("smith john.pdf");
        var vm = NewViewModel();
        await vm.AddFilesAsync(new[] { src });
        vm.RenameCommand.Execute(null);
        await vm.RenameCommand.Completion;

        vm.UndoCommand.Execute(null);
        await vm.UndoCommand.Completion;

        Assert.True(File.Exists(src));
        var row = Assert.Single(vm.Results);
        Assert.Equal("smith john.pdf", row.Current);
        Assert.Equal(src, row.CurrentPath);
        Assert.Equal(StandardiseRowStatus.Pending, row.Status);
        Assert.Equal("20260115-SMITH-JOHN.pdf", row.Result);
        Assert.StartsWith("Undid the last rename", vm.Status);
        Assert.False(vm.UndoCommand.CanExecute(null));
    }

    [Fact]
    public async Task UndoPutsDroppedWordsBackWithTheName()
    {
        var vm = NewViewModel();
        await vm.AddFilesAsync(new[] { _dir.File("smith john scan.pdf") });
        vm.SegmentChips[2].IsKept = false;
        vm.RenameCommand.Execute(null);
        await vm.RenameCommand.Completion;

        vm.UndoCommand.Execute(null);
        await vm.UndoCommand.Completion;

        Assert.Equal("20260115-SMITH-JOHN.pdf", Assert.Single(vm.Results).Result);
        Assert.False(vm.SegmentChips[2].IsKept);
    }

    /// <summary>Undo finds the files by where they are, not by the rows it
    /// renamed: the list may have been cleared and the files added again.</summary>
    [Fact]
    public async Task UndoAfterClearingAndAddingAgainUpdatesTheNewRows()
    {
        var src = _dir.File("smith.pdf");
        var vm = NewViewModel();
        await vm.AddFilesAsync(new[] { src });
        vm.RenameCommand.Execute(null);
        await vm.RenameCommand.Completion;
        vm.ClearCommand.Execute(null);
        await vm.AddFilesAsync(new[] { OnDisk("20260115-SMITH.pdf") });

        vm.UndoCommand.Execute(null);
        await vm.UndoCommand.Completion;

        Assert.True(File.Exists(src));
        var row = Assert.Single(vm.Results);
        Assert.Equal(src, row.CurrentPath);
        Assert.Equal(StandardiseRowStatus.Pending, row.Status);
    }

    /// <summary>Another file took the old name meanwhile: Undo leaves this
    /// one under its new name, and the row must stay pointing at it, not at
    /// the other file.</summary>
    [Fact]
    public async Task UndoThatCannotPutANameBackKeepsTheRowOnItsFile()
    {
        var src = _dir.File("smith.pdf");
        var vm = NewViewModel();
        await vm.AddFilesAsync(new[] { src });
        vm.RenameCommand.Execute(null);
        await vm.RenameCommand.Completion;
        File.WriteAllText(src, "someone else's file");

        vm.UndoCommand.Execute(null);
        await vm.UndoCommand.Completion;

        var row = Assert.Single(vm.Results);
        Assert.Equal(OnDisk("20260115-SMITH.pdf"), row.CurrentPath);
        Assert.Contains("exists again", vm.Status);
    }

    /// <summary>A file Undo could not put back is still renamed on disk, so
    /// Undo must stay on for it: once whatever was in the way is gone, a
    /// second Undo puts it back. Forgetting it left the file under its new
    /// name with no way back through the window.</summary>
    [Fact]
    public async Task UndoThatCouldNotPutANameBackCanBeTriedAgain()
    {
        var src = _dir.File("smith.pdf");
        var vm = NewViewModel();
        await vm.AddFilesAsync(new[] { src });
        vm.RenameCommand.Execute(null);
        await vm.RenameCommand.Completion;
        File.WriteAllText(src, "someone else's file");
        vm.UndoCommand.Execute(null);
        await vm.UndoCommand.Completion;

        Assert.True(vm.UndoCommand.CanExecute(null));
        File.Delete(src);
        vm.UndoCommand.Execute(null);
        await vm.UndoCommand.Completion;

        Assert.True(File.Exists(src));
        Assert.False(File.Exists(OnDisk("20260115-SMITH.pdf")));
        Assert.Equal(src, Assert.Single(vm.Results).CurrentPath);
        Assert.StartsWith("Undid the last rename (1 file)", vm.Status);
        Assert.False(vm.UndoCommand.CanExecute(null));
    }

    /// <summary>Undo keeps exactly the files still under their new names:
    /// the one that went back is done with, the one that didn't can be tried
    /// again, and it gets its dropped words back when it does.</summary>
    [Fact]
    public async Task UndoKeepsOnlyTheFilesItCouldNotPutBack()
    {
        var smith = _dir.File("smith john scan.pdf");
        var jones = _dir.File("jones mary scan.pdf");
        var vm = NewViewModel();
        await vm.AddFilesAsync(new[] { smith, jones });
        vm.SegmentChips[2].IsKept = false;   // "scan", in both files
        vm.RenameCommand.Execute(null);
        await vm.RenameCommand.Completion;
        File.WriteAllText(smith, "someone else's file");

        vm.UndoCommand.Execute(null);
        await vm.UndoCommand.Completion;

        Assert.True(File.Exists(jones));
        Assert.True(File.Exists(OnDisk("20260115-SMITH-JOHN.pdf")));
        Assert.True(vm.UndoCommand.CanExecute(null));

        File.Delete(smith);
        vm.UndoCommand.Execute(null);
        await vm.UndoCommand.Completion;

        Assert.True(File.Exists(smith));
        Assert.True(File.Exists(jones));
        var smithRow = vm.Results.Single(r => r.CurrentPath == smith);
        Assert.Equal("20260115-SMITH-JOHN.pdf", smithRow.Result);   // "scan" is dropped again
        Assert.False(vm.UndoCommand.CanExecute(null));
    }

    /// <summary>Undo covers the last rename that moved something. A Rename
    /// that moves nothing (its only file is in use) must not wipe that
    /// record: the earlier files are still under their new names (the rule
    /// Bulk rename follows, Q2-01).</summary>
    [Fact]
    public async Task ARenameThatMovesNothingKeepsTheLastRenamesUndo()
    {
        var first = _dir.File("smith.pdf");
        var vm = NewViewModel();
        await vm.AddFilesAsync(new[] { first });
        vm.RenameCommand.Execute(null);
        await vm.RenameCommand.Completion;
        var locked = _dir.File("jones.pdf");
        await vm.AddFilesAsync(new[] { locked });

        using (File.Open(locked, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            vm.RenameCommand.Execute(null);
            await vm.RenameCommand.Completion;
        }

        Assert.Equal(StandardiseRowStatus.Failed, vm.Results.Single(r => r.CurrentPath == locked).Status);
        Assert.True(vm.UndoCommand.CanExecute(null));
        vm.UndoCommand.Execute(null);
        await vm.UndoCommand.Completion;
        Assert.True(File.Exists(first));
    }

    [Fact]
    public void UndoCannotRunWithNothingToUndo() =>
        Assert.False(NewViewModel().UndoCommand.CanExecute(null));

    /// <summary>A file held open without delete sharing, which is what makes
    /// File.Move fail on Windows ("in use by another program"): the row
    /// says so, the other file is renamed, and Undo covers only the one
    /// that moved.</summary>
    [Fact]
    public async Task AFileInUseIsReportedAsFailedAndTheRestAreRenamed()
    {
        var locked = _dir.File("smith, john.pdf");
        var ok = _dir.File("jones, mary.pdf");
        var vm = NewViewModel();
        await vm.AddFilesAsync(new[] { locked, ok });

        using (File.Open(locked, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            vm.RenameCommand.Execute(null);
            await vm.RenameCommand.Completion;
        }

        var lockedRow = vm.Results.Single(r => r.Current == "smith, john.pdf");
        Assert.Equal(StandardiseRowStatus.Failed, lockedRow.Status);
        Assert.StartsWith("Couldn't rename:", lockedRow.Result);
        Assert.Equal(StandardiseRowStatus.Renamed, vm.Results.Single(r => r.CurrentPath == OnDisk("20260115-JONES-MARY.pdf")).Status);
        Assert.Contains("couldn't be renamed", vm.Status);

        vm.UndoCommand.Execute(null);
        await vm.UndoCommand.Completion;
        Assert.True(File.Exists(locked));
        Assert.True(File.Exists(ok));
    }

    // ---------------------------------------------------------- threading

    /// <summary>The preview reads the disk (clash checks), so it runs off the
    /// UI thread; a newer preview makes an older, slower one stale, and the
    /// older result must not overwrite the newer one when it lands.</summary>
    [Fact]
    public async Task AStalePreviewDoesNotOverwriteANewerOne()
    {
        var scheduler = new ControlledWorkScheduler();
        var vm = NewViewModel(scheduler);
        var add = vm.AddFilesAsync(new[] { _dir.File("smith john.pdf") });
        scheduler.ReleaseAll();
        await add;

        vm.Case = NameCase.Title;    // preview 1, left in flight
        vm.Case = NameCase.AsIs;     // preview 2
        scheduler.ReleaseNewest();
        scheduler.ReleaseAll();

        Assert.Equal("20260115-smith-john.pdf", Assert.Single(vm.Results).Result);
    }

    /// <summary>Rename carries out the preview on screen, so while a newer
    /// one is being worked out it waits: otherwise it could apply names the
    /// grid never showed.</summary>
    [Fact]
    public async Task RenameWaitsForAPreviewStillBeingWorkedOut()
    {
        var src = _dir.File("smith john.pdf");
        var scheduler = new ControlledWorkScheduler();
        var vm = NewViewModel(scheduler);
        var add = vm.AddFilesAsync(new[] { src });
        scheduler.ReleaseAll();
        await add;
        Assert.True(vm.RenameCommand.CanExecute(null));

        vm.Case = NameCase.Title;

        Assert.True(vm.IsPreviewing);
        Assert.False(vm.RenameCommand.CanExecute(null));
        scheduler.ReleaseAll();
        Assert.False(vm.IsPreviewing);
        Assert.True(vm.RenameCommand.CanExecute(null));

        vm.RenameCommand.Execute(null);
        scheduler.ReleaseAll();
        await vm.RenameCommand.Completion;
        Assert.True(File.Exists(OnDisk("20260115-Smith-John.pdf")));
    }

    [Fact]
    public async Task APreviewLandingAfterClearShowsNothing()
    {
        var scheduler = new ControlledWorkScheduler();
        var vm = NewViewModel(scheduler);
        var add = vm.AddFilesAsync(new[] { _dir.File("smith.pdf") });
        scheduler.ReleaseAll();
        await add;
        vm.Case = NameCase.Title;          // preview in flight

        vm.ClearCommand.Execute(null);
        scheduler.ReleaseAll();

        Assert.Equal(0, vm.PendingCount);
        Assert.Equal("Rename", vm.RenameButtonText);
        Assert.False(vm.RenameCommand.CanExecute(null));
    }

    [Fact]
    public async Task AControlChangedWhileRenamingDoesNotRepaintTheResult()
    {
        var scheduler = new ControlledWorkScheduler();
        var vm = NewViewModel(scheduler);
        var add = vm.AddFilesAsync(new[] { _dir.File("smith.pdf") });
        scheduler.ReleaseAll();
        await add;
        vm.RenameCommand.Execute(null);

        vm.Case = NameCase.Title;          // mid-rename
        scheduler.ReleaseAll();
        await vm.RenameCommand.Completion;

        var row = Assert.Single(vm.Results);
        Assert.Equal(StandardiseRowStatus.Renamed, row.Status);
        Assert.Equal("Renamed", row.Result);
        Assert.True(File.Exists(OnDisk("20260115-SMITH.pdf")));
    }

    [Fact]
    public async Task TheRenameRunsOffTheCallingThreadAndHoldsTheWindowBusy()
    {
        var src = _dir.File("smith.pdf");
        var scheduler = new ControlledWorkScheduler();
        var vm = NewViewModel(scheduler);
        var add = vm.AddFilesAsync(new[] { src });
        scheduler.ReleaseAll();
        await add;

        vm.RenameCommand.Execute(null);

        Assert.True(vm.IsBusy);
        Assert.True(File.Exists(src), "nothing should move until the scheduled work runs");
        Assert.False(vm.RenameCommand.CanExecute(null));
        Assert.False(vm.ClearCommand.CanExecute(null));

        scheduler.ReleaseAll();
        await vm.RenameCommand.Completion;

        Assert.False(vm.IsBusy);
        Assert.False(File.Exists(src));
    }

    [Fact]
    public async Task ADropWhileRenamingSaysToDropAgainLater()
    {
        var scheduler = new ControlledWorkScheduler();
        var vm = NewViewModel(scheduler);
        var add = vm.AddFilesAsync(new[] { _dir.File("smith.pdf") });
        scheduler.ReleaseAll();
        await add;
        vm.RenameCommand.Execute(null);

        await vm.AddFilesAsync(new[] { _dir.File("jones.pdf") });

        Assert.Single(vm.Results);
        Assert.Contains("drop again", vm.Status);
        scheduler.ReleaseAll();
        await vm.RenameCommand.Completion;
    }

    /// <summary>Throws on one numbered dispatch, so the earlier ones (intake,
    /// preview, rename) run normally and only the one under test fails.</summary>
    private sealed class FailsOnNthCallScheduler : IWorkScheduler
    {
        private int _callCount;
        private readonly int _failOnCall;
        public FailsOnNthCallScheduler(int failOnCall) => _failOnCall = failOnCall;

        public Task<T> Run<T>(Func<T> work)
        {
            _callCount++;
            if (_callCount == _failOnCall)
                throw new InvalidOperationException("the scheduler is unavailable");
            return Task.FromResult(work());
        }

        public Task Run(Action work) => Run(() => { work(); return true; });
    }

    [Fact]
    public async Task AFailedUndoIsReportedRatherThanGoingSilent()
    {
        // 1 intake, 2 preview, 3 rename, 4 undo
        var vm = NewViewModel(new FailsOnNthCallScheduler(failOnCall: 4));
        await vm.AddFilesAsync(new[] { _dir.File("smith.pdf") });
        vm.RenameCommand.Execute(null);
        await vm.RenameCommand.Completion;

        vm.UndoCommand.Execute(null);
        await vm.UndoCommand.Completion;

        Assert.Contains("unexpectedly", vm.Status);
        Assert.False(vm.IsBusy);
    }

    [Fact]
    public async Task AFailedRenameIsReportedRatherThanGoingSilent()
    {
        var vm = NewViewModel(new FailsOnNthCallScheduler(failOnCall: 3));
        await vm.AddFilesAsync(new[] { _dir.File("smith.pdf") });

        vm.RenameCommand.Execute(null);
        await vm.RenameCommand.Completion;

        Assert.Contains("unexpectedly", vm.Status);
        Assert.False(vm.IsBusy);
    }

    [Fact]
    public async Task ARowAnnouncesItsNewResultAndStatus()
    {
        var vm = NewViewModel();
        await vm.AddFilesAsync(new[] { _dir.File("smith.pdf") });
        var row = Assert.Single(vm.Results);
        var raised = new List<string?>();
        row.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        vm.RenameCommand.Execute(null);
        await vm.RenameCommand.Completion;

        Assert.Contains(nameof(StandardiseNameRow.Result), raised);
        Assert.Contains(nameof(StandardiseNameRow.Status), raised);
        Assert.Contains(nameof(StandardiseNameRow.Current), raised);
    }
}
