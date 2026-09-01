using OrdoSort.Wpf.Services;
using OrdoSort.Wpf.ViewModels;

namespace OrdoSort.Wpf.Tests;

/// <summary>StandardiseNamesViewModel: drop -> prompt for the date -> rename
/// immediately, through the real BulkRename.PlanTidy/Execute/Revert — no
/// fakes standing in for the file-system work itself, only for the date
/// prompt (IDialogService.AskDate), which cannot be driven headlessly any
/// more than AskPassword can.</summary>
public class StandardiseNamesViewModelTests : IDisposable
{
    private readonly TempDir _dir = new();
    public void Dispose() => _dir.Dispose();

    [Fact]
    public async Task DroppingAMessyFilePromptsWithTodayAsTheDefaultAndRenamesImmediately()
    {
        var src = _dir.File("smith, john_A12345.pdf");
        var dialogs = new FakeDialogs();
        dialogs.DateAnswers.Enqueue("20260115");
        var vm = new StandardiseNamesViewModel(dialogs, new InlineWorkScheduler());

        await vm.AddFilesAsync(new[] { src });

        var today = DateTime.Today.ToString("yyyyMMdd");
        var request = Assert.Single(dialogs.DateRequests);
        Assert.Equal(today, request.DefaultDate);
        Assert.Equal(1, request.FileCount);

        Assert.False(File.Exists(src));
        Assert.True(File.Exists(Path.Combine(_dir.Path, "20260115-SMITH-JOHN-A12345.pdf")));

        var row = Assert.Single(vm.Results);
        Assert.Equal("smith, john_A12345.pdf", row.Current);
        Assert.Equal("20260115-SMITH-JOHN-A12345.pdf", row.Result);
        Assert.Equal(StandardiseRowStatus.Renamed, row.Status);
        Assert.Equal("Renamed 1 file.", vm.Status);
    }

    /// <summary>"the owner files batches from one day at a time" — the brief's
    /// own reasoning for why the SECOND prompt in a session should default to
    /// whatever was accepted last, not to today again.</summary>
    [Fact]
    public async Task TheSecondPromptInASessionDefaultsToWhatWasAcceptedLastNotToday()
    {
        var first = _dir.File("smith.pdf");
        var second = _dir.File("jones.pdf");
        var dialogs = new FakeDialogs();
        dialogs.DateAnswers.Enqueue("20251201");
        dialogs.DateAnswers.Enqueue("20251202");
        var vm = new StandardiseNamesViewModel(dialogs, new InlineWorkScheduler());

        await vm.AddFilesAsync(new[] { first });
        await vm.AddFilesAsync(new[] { second });

        Assert.Equal(2, dialogs.DateRequests.Count);
        Assert.Equal("20251201", dialogs.DateRequests[1].DefaultDate);   // last ACCEPTED answer, not today
    }

    [Fact]
    public async Task CancellingThePromptAddsNothingAndRenamesNothing()
    {
        var src = _dir.File("smith, john.pdf");
        var dialogs = new FakeDialogs();   // DateAnswers left empty: AskDate answers null
        var vm = new StandardiseNamesViewModel(dialogs, new InlineWorkScheduler());

        await vm.AddFilesAsync(new[] { src });

        Assert.True(File.Exists(src));              // untouched on disk
        Assert.Equal("smith, john.pdf", Path.GetFileName(Directory.GetFiles(_dir.Path).Single()));
        Assert.Empty(vm.Results);                    // nothing added to the grid
        Assert.Equal("", vm.Status);                 // no batch ever ran
        Assert.False(vm.UndoCommand.CanExecute(null));
    }

    [Fact]
    public async Task AnAlreadyStandardisedFileIsReportedAsUnchangedAndLeftAlone()
    {
        var src = _dir.File("20260115-SMITH-JOHN-A12345.pdf");
        var dialogs = new FakeDialogs();
        dialogs.DateAnswers.Enqueue("20260115");
        var vm = new StandardiseNamesViewModel(dialogs, new InlineWorkScheduler());

        await vm.AddFilesAsync(new[] { src });

        Assert.True(File.Exists(src));   // never touched — Execute skips an unchanged plan
        var row = Assert.Single(vm.Results);
        Assert.Equal(StandardiseRowStatus.Unchanged, row.Status);
        Assert.Equal("already standardised", row.Result);
        Assert.Equal("Already standardised.", vm.Status);
        Assert.False(vm.UndoCommand.CanExecute(null));   // nothing was renamed, so nothing to undo
    }

    /// <summary>The brief's own wrinkle, proven through the FULL view model —
    /// not just Core's PlanTidyFsTests — so the wiring that passes
    /// CollisionSuffixStyle.Dashed into Execute is what's actually under
    /// test here, not just Execute's own logic given that style.</summary>
    [Fact]
    public async Task TwoFilesThatTidyToTheSameNameBothLandWithADashedSuffix()
    {
        var a = _dir.File("smith, john.pdf");
        var b = _dir.File("SMITH_JOHN.pdf");
        var dialogs = new FakeDialogs();
        dialogs.DateAnswers.Enqueue("20260115");
        var vm = new StandardiseNamesViewModel(dialogs, new InlineWorkScheduler());

        await vm.AddFilesAsync(new[] { a, b });

        var results = vm.Results.Select(r => r.Result).ToList();
        Assert.Contains("20260115-SMITH-JOHN.pdf", results);
        Assert.Contains("20260115-SMITH-JOHN-2.pdf", results);
        Assert.All(results, r => Assert.DoesNotContain(" ", r));
        Assert.All(results, r => Assert.DoesNotContain("(", r));
        Assert.True(File.Exists(Path.Combine(_dir.Path, "20260115-SMITH-JOHN.pdf")));
        Assert.True(File.Exists(Path.Combine(_dir.Path, "20260115-SMITH-JOHN-2.pdf")));
    }

    [Fact]
    public async Task UndoRestoresTheOriginalNameAndRemovesTheRowFromTheGrid()
    {
        var src = _dir.File("smith, john.pdf");
        var dialogs = new FakeDialogs();
        dialogs.DateAnswers.Enqueue("20260115");
        var vm = new StandardiseNamesViewModel(dialogs, new InlineWorkScheduler());
        await vm.AddFilesAsync(new[] { src });
        var renamed = Path.Combine(_dir.Path, "20260115-SMITH-JOHN.pdf");
        Assert.True(File.Exists(renamed));
        Assert.True(vm.UndoCommand.CanExecute(null));

        await vm.UndoLastBatchAsync();

        Assert.True(File.Exists(src));
        Assert.False(File.Exists(renamed));
        Assert.Empty(vm.Results);
        Assert.Equal("Original names restored.", vm.Status);
        Assert.False(vm.UndoCommand.CanExecute(null));   // one batch undo, same limit as Bulk rename
    }

    [Fact]
    public void UndoCommandCannotExecuteWithNothingToUndo()
    {
        var vm = new StandardiseNamesViewModel(new FakeDialogs(), new InlineWorkScheduler());
        Assert.False(vm.UndoCommand.CanExecute(null));
    }

    /// <summary>Execute is already per-file fail-soft (BulkRename.Execute's
    /// own IOException/UnauthorizedAccessException catch) — this proves the
    /// view model surfaces that failure in the grid rather than swallowing
    /// it or crashing the batch for every OTHER file in it. Locks the file
    /// with FileShare.Read (no delete/rename share), which is what actually
    /// makes File.Move throw on Windows — the same real mechanism "in use by
    /// another program" describes, not a simulated error.</summary>
    [Fact]
    public async Task AFileInUseIsReportedAsFailedRatherThanVanishing()
    {
        var locked = _dir.File("smith, john.pdf");
        var ok = _dir.File("jones, mary.pdf");
        var dialogs = new FakeDialogs();
        dialogs.DateAnswers.Enqueue("20260115");
        var vm = new StandardiseNamesViewModel(dialogs, new InlineWorkScheduler());

        using (File.Open(locked, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            await vm.AddFilesAsync(new[] { locked, ok });
        }

        var lockedRow = vm.Results.Single(r => r.Current == "smith, john.pdf");
        Assert.Equal(StandardiseRowStatus.Failed, lockedRow.Status);
        Assert.NotEqual("", lockedRow.Result);

        var okRow = vm.Results.Single(r => r.Current == "jones, mary.pdf");
        Assert.Equal(StandardiseRowStatus.Renamed, okRow.Status);
        Assert.True(File.Exists(Path.Combine(_dir.Path, "20260115-JONES-MARY.pdf")));

        Assert.Contains("failed", vm.Status);
        // The one genuine failure did not become undoable — only what was
        // actually renamed did.
        Assert.True(vm.UndoCommand.CanExecute(null));
        await vm.UndoLastBatchAsync();
        Assert.True(File.Exists(locked));   // never moved, so still exactly where it was
        Assert.True(File.Exists(ok));       // restored by the undo
    }

    /// <summary>A gate around every IWorkScheduler.Run call: the FIRST
    /// dispatch (Intake's File.Exists check) proves this tool never runs its
    /// disk work synchronously on the calling thread, the same concern every
    /// sibling tool in this app already guards. Distinct from
    /// BulkRenameBatchTests' own QueuedWorkScheduler: that one needs
    /// per-item release granularity for a cancellable, progressive batch;
    /// this tool has neither, so one shared gate is the whole proof.</summary>
    private sealed class GatedScheduler : IWorkScheduler
    {
        private readonly ManualResetEventSlim _gate = new(false);
        private int _dispatchCount;
        public int DispatchCount => _dispatchCount;

        public Task<T> Run<T>(Func<T> work) => Task.Run(() =>
        {
            Interlocked.Increment(ref _dispatchCount);
            _gate.Wait();
            return work();
        });

        public Task Run(Action work) => Run(() => { work(); return true; });

        public void Release() => _gate.Set();
    }

    [Fact]
    public async Task TheRenameIsDispatchedToTheSchedulerRatherThanRunningOnTheCallingThread()
    {
        var src = _dir.File("smith, john.pdf");
        var scheduler = new GatedScheduler();
        var dialogs = new FakeDialogs();
        dialogs.DateAnswers.Enqueue("20260115");
        var vm = new StandardiseNamesViewModel(dialogs, scheduler);

        var task = vm.AddFilesAsync(new[] { src });

        // Task.Run's own scheduling is not synchronous with the call above,
        // so wait for the dispatch to actually land rather than racing it.
        var deadline = DateTime.UtcNow.AddSeconds(3);
        while (scheduler.DispatchCount == 0 && DateTime.UtcNow < deadline) await Task.Delay(5);
        Assert.True(scheduler.DispatchCount > 0, "the intake work should have been dispatched to the scheduler");

        Assert.True(File.Exists(src), "nothing should be renamed until the scheduled work is released");
        Assert.True(vm.IsBusy);

        scheduler.Release();
        await task;

        Assert.False(File.Exists(src));
        Assert.False(vm.IsBusy);
    }

    /// <summary>The window disables Add files… while IsBusy (IsIdle), but
    /// the guard lives in the view model too — a second drop that slips
    /// through anyway (e.g. a queued drag-drop event) must not start a
    /// second overlapping batch while the first is still mid-flight.</summary>
    [Fact]
    public async Task ASecondAddWhileABatchIsRunningIsIgnored()
    {
        var a = _dir.File("smith.pdf");
        var b = _dir.File("jones.pdf");
        var scheduler = new GatedScheduler();
        var dialogs = new FakeDialogs();
        dialogs.DateAnswers.Enqueue("20260115");
        var vm = new StandardiseNamesViewModel(dialogs, scheduler);

        var firstTask = vm.AddFilesAsync(new[] { a });
        var deadline = DateTime.UtcNow.AddSeconds(3);
        while (scheduler.DispatchCount == 0 && DateTime.UtcNow < deadline) await Task.Delay(5);

        // Fired without a direct await: if the re-entrancy guard were ever
        // missing, this call would itself try to dispatch to the same gated
        // scheduler and block on the gate this test only releases below —
        // a direct `await` here would then hang the whole test process
        // rather than fail it. Observed instead through a bounded poll, so
        // a missing guard is a clean, reported failure either way.
        var secondTask = vm.AddFilesAsync(new[] { b });
        var secondDeadline = DateTime.UtcNow.AddMilliseconds(500);
        while (!secondTask.IsCompleted && DateTime.UtcNow < secondDeadline) await Task.Delay(5);
        Assert.True(secondTask.IsCompleted,
            "a second add while a batch is already running must return immediately, not queue more work");

        Assert.Empty(dialogs.DateRequests);   // the second call never even asked

        scheduler.Release();
        await firstTask;

        Assert.Single(dialogs.DateRequests);   // only the first batch ever prompted
        Assert.True(File.Exists(b));           // the second file was never touched
    }
}
