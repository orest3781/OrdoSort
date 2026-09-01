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

    /// <summary>Audit gap: AddNote's own content was asserted nowhere, though
    /// every sibling tool's view-model tests pin it. One real file and one
    /// path that doesn't exist, in a single add, exercises Intake.Added's
    /// own Missing-count wording end to end.</summary>
    [Fact]
    public async Task AddNotePinsWhatTheDropContained()
    {
        var real = _dir.File("smith.pdf");
        var missing = Path.Combine(_dir.Path, "does-not-exist.pdf");
        var dialogs = new FakeDialogs();
        dialogs.DateAnswers.Enqueue("20260115");
        var vm = new StandardiseNamesViewModel(dialogs, new InlineWorkScheduler());

        await vm.AddFilesAsync(new[] { real, missing });

        Assert.Equal("1 added · 1 ignored (1 doesn't exist)", vm.AddNote);
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

    /// <summary>Fix round 1, item 4, through the full view model: a file
    /// already named "20260115-smith.pdf" for today's date is NOT already
    /// standardised — TidyStem's own step 2 promises uppercase — so this
    /// must rename it on disk and report Renamed, not Unchanged. Verified
    /// against the TRUE on-disk name via Directory.GetFiles, since
    /// File.Exists is case-insensitive on Windows and could not tell
    /// "smith.pdf" from "SMITH.pdf" at the same path.</summary>
    [Fact]
    public async Task ACaseOnlyDifferenceRenamesOnDiskAndReportsRenamedNotUnchanged()
    {
        var src = _dir.File("20260115-smith.pdf");
        var dialogs = new FakeDialogs();
        dialogs.DateAnswers.Enqueue("20260115");
        var vm = new StandardiseNamesViewModel(dialogs, new InlineWorkScheduler());

        await vm.AddFilesAsync(new[] { src });

        var row = Assert.Single(vm.Results);
        Assert.Equal(StandardiseRowStatus.Renamed, row.Status);
        Assert.Equal("20260115-SMITH.pdf", row.Result);

        var actualName = Path.GetFileName(Directory.GetFiles(_dir.Path).Single());
        Assert.Equal("20260115-SMITH.pdf", actualName);
    }

    /// <summary>Audit gap: ApplyBatchResult's Skipped branch was only ever
    /// reached at the Core level or through hand-built rows — never end to
    /// end. FakeDialogs.AskDate does not validate its scripted answer (it
    /// just returns it), so an illegal date reaches PlanTidy exactly as an
    /// unvalidated caller's would, and Naming.RejectIllegal turns it away
    /// readably inside PlanTidy.</summary>
    [Fact]
    public async Task AnIllegalDateProducesASkippedRowDrivenEndToEnd()
    {
        var src = _dir.File("smith.pdf");
        var dialogs = new FakeDialogs();
        dialogs.DateAnswers.Enqueue("2026:01:15");   // not validated by the fake — Core's own guard must catch it
        var vm = new StandardiseNamesViewModel(dialogs, new InlineWorkScheduler());

        await vm.AddFilesAsync(new[] { src });

        Assert.True(File.Exists(src));   // never touched
        var row = Assert.Single(vm.Results);
        Assert.Equal(StandardiseRowStatus.Skipped, row.Status);
        Assert.Contains(":", row.Result);
        Assert.Contains("skipped", vm.Status);
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

    /// <summary>Fix round 1, item 1 — the review's HIGH finding: before this
    /// fix, UndoLastBatchAsync reset _lastOutcomes/_lastRenamedRows
    /// unconditionally after ANY Revert call, so a single locked file among
    /// several renamed ones made the WHOLE batch's undo record vanish —
    /// the healthy rows' Result names went stale in the grid forever, and
    /// the locked file could never be retried from the UI, since
    /// UndoCommand.CanExecute depends on _lastOutcomes being non-empty.
    /// Renames two files, locks ONE of the two RENAMED files (FileShare.Read
    /// — no delete/rename share, the real mechanism, not a simulated
    /// error), then Undoes: the healthy row must be gone, the locked row
    /// must remain, Undo must stay armed, and a SECOND Undo (after the lock
    /// is released) must finish the job.</summary>
    [Fact]
    public async Task UndoOfAPartlyRevertedBatchKeepsTheUnrestoredFileArmedForRetry()
    {
        var a = _dir.File("smith.pdf");
        var b = _dir.File("jones.pdf");
        var dialogs = new FakeDialogs();
        dialogs.DateAnswers.Enqueue("20260115");
        var vm = new StandardiseNamesViewModel(dialogs, new InlineWorkScheduler());
        await vm.AddFilesAsync(new[] { a, b });

        var renamedA = Path.Combine(_dir.Path, "20260115-SMITH.pdf");
        var renamedB = Path.Combine(_dir.Path, "20260115-JONES.pdf");
        Assert.True(File.Exists(renamedA));
        Assert.True(File.Exists(renamedB));

        using (File.Open(renamedA, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            await vm.UndoLastBatchAsync();
        }

        // The healthy one (jones) restored and its row is gone.
        Assert.True(File.Exists(b));
        Assert.False(File.Exists(renamedB));
        Assert.DoesNotContain(vm.Results, r => r.Current == "jones.pdf");

        // The locked one (smith) is still renamed, its row still shows it,
        // and Undo is still armed for a retry.
        Assert.True(File.Exists(renamedA));
        Assert.False(File.Exists(a));
        Assert.Contains(vm.Results, r => r.Current == "smith.pdf");
        Assert.True(vm.UndoCommand.CanExecute(null));
        Assert.Contains("Couldn't restore", vm.Status);

        // Retry, now that the lock is released (the using block above).
        await vm.UndoLastBatchAsync();

        Assert.True(File.Exists(a));
        Assert.False(File.Exists(renamedA));
        Assert.DoesNotContain(vm.Results, r => r.Current == "smith.pdf");
        Assert.False(vm.UndoCommand.CanExecute(null));
        Assert.Equal("Original names restored.", vm.Status);
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

    /// <summary>Fix round 1, item 5: throws on a specific call number rather
    /// than the queue-and-fail-next shape BulkRenameBatchTests.QueuedWorkScheduler
    /// uses, since this test needs AddFilesAsync's own two dispatches
    /// (intake, then Execute) to succeed normally and only Undo's dispatch
    /// to fail.</summary>
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

    /// <summary>Fix round 1, item 5: the constructor wired UndoCommand but
    /// never subscribed OnError, so AsyncRelayCommand routed a faulted
    /// UndoLastBatchAsync run to a no-op subscriber — IsBusy cleared and
    /// nothing was said. Revert itself is already per-outcome fail-soft (it
    /// reports through Status on its own), so this fires only on something
    /// Revert does not catch — here, the scheduler dispatch itself
    /// failing.</summary>
    [Fact]
    public async Task UndoCommandReportsAnUnexpectedSchedulerFailureInsteadOfGoingSilent()
    {
        var src = _dir.File("smith, john.pdf");
        var dialogs = new FakeDialogs();
        dialogs.DateAnswers.Enqueue("20260115");
        // Call 1: AddFilesAsync's own intake dispatch. Call 2: its Execute
        // dispatch. Call 3: UndoLastBatchAsync's own Revert dispatch — the
        // one this fact needs to fail.
        var scheduler = new FailsOnNthCallScheduler(failOnCall: 3);
        var vm = new StandardiseNamesViewModel(dialogs, scheduler);
        await vm.AddFilesAsync(new[] { src });
        Assert.True(vm.UndoCommand.CanExecute(null));

        vm.UndoCommand.Execute(null);
        await vm.UndoCommand.Completion;

        Assert.Contains("unexpectedly", vm.Status);
        Assert.False(vm.IsBusy);
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
