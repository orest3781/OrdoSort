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

    /// <summary>Fix round 2, item 2: "restored" was first decided by
    /// "Final no longer exists" alone, which is too loose. If the RENAMED
    /// file is deleted (or moved away) by something else before Undo runs,
    /// Final is gone for a reason that is not a successful revert — Revert
    /// itself reports a real problem (File.Move's source is gone, so it
    /// throws FileNotFoundException, an IOException Revert's own catch
    /// already turns into a message), but the old heuristic still called it
    /// restored and silently dropped the row — Status naming a failure
    /// while the grid quietly disagreed with it. Restored now also requires
    /// File.Exists(o.Source): the file has to actually BE back, not merely
    /// be gone from where it was.</summary>
    [Fact]
    public async Task UndoOfARenamedFileDeletedOutFromUnderItKeepsTheRowAndReportsTheFailure()
    {
        var src = _dir.File("smith, john.pdf");
        var dialogs = new FakeDialogs();
        dialogs.DateAnswers.Enqueue("20260115");
        var vm = new StandardiseNamesViewModel(dialogs, new InlineWorkScheduler());
        await vm.AddFilesAsync(new[] { src });

        var renamed = Path.Combine(_dir.Path, "20260115-SMITH-JOHN.pdf");
        Assert.True(File.Exists(renamed));

        File.Delete(renamed);   // something else removes the renamed file before Undo runs

        await vm.UndoLastBatchAsync();

        Assert.False(File.Exists(src));   // never came back — there was nothing left to move
        Assert.Contains(vm.Results, r => r.Current == "smith, john.pdf");   // row stays: honest record
        Assert.True(vm.UndoCommand.CanExecute(null));   // still armed for a retry
        Assert.Contains("Couldn't restore", vm.Status);   // names the real problem
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

    // ------------------------------------------------------- Remove last segment

    [Fact]
    public async Task PeelCommandIsDisabledWithNoSelectionAndEnabledOnceARowIsSelected()
    {
        var src = _dir.File("A-B-C-D-E.pdf");
        var dialogs = new FakeDialogs();
        dialogs.DateAnswers.Enqueue("20260115");
        var vm = new StandardiseNamesViewModel(dialogs, new InlineWorkScheduler());
        await vm.AddFilesAsync(new[] { src });

        Assert.False(vm.PeelCommand.CanExecute(null));   // nothing selected yet

        vm.SelectedRows = new[] { vm.Results.Single() };
        Assert.True(vm.PeelCommand.CanExecute(null));

        vm.SelectedRows = Array.Empty<StandardiseNameRow>();
        Assert.False(vm.PeelCommand.CanExecute(null));
    }

    /// <summary>A WPF Button bound to a Command relies on CanExecuteChanged
    /// firing to know when to re-enable itself — CanExecute alone (proven
    /// above) is not enough for the real button, only for a test calling it
    /// directly. This is what makes the SelectedRows setter's own
    /// PeelCommand.RaiseCanExecuteChanged() call load-bearing rather than
    /// redundant with the CanExecute check itself.</summary>
    [Fact]
    public void SettingSelectedRowsRaisesPeelCommandCanExecuteChanged()
    {
        var vm = new StandardiseNamesViewModel(new FakeDialogs());
        var raised = 0;
        vm.PeelCommand.CanExecuteChanged += (_, _) => raised++;

        vm.SelectedRows = new[]
        {
            new StandardiseNameRow("a.pdf", "a.pdf", @"C:\a.pdf", StandardiseRowStatus.Unchanged),
        };

        Assert.True(raised > 0);
    }

    /// <summary>IsBusy is the one flag both Add files… and Remove last
    /// segment share (see IsBusy's own doc comment) — proven here the same
    /// way ASecondAddWhileABatchIsRunningIsIgnored above proves the Add side,
    /// reusing the same GatedScheduler: while an add is genuinely in flight,
    /// PeelCommand must already read as disabled, and a direct call must be
    /// the same defensive no-op AddFilesAsync's own IsBusy check is.</summary>
    [Fact]
    public async Task PeelIsIgnoredWhileAnAddIsStillRunning()
    {
        var a = _dir.File("A-B-C-D-ONE.pdf");
        var scheduler = new GatedScheduler();
        var dialogs = new FakeDialogs();
        dialogs.DateAnswers.Enqueue("20260115");
        var vm = new StandardiseNamesViewModel(dialogs, scheduler);
        // A selection already armed BEFORE the add starts, so the guard
        // under test is genuinely IsBusy alone — with an empty selection,
        // PeelSelectedAsync's OWN "nothing selected" guard would return
        // early too, and this fact would keep passing even if the IsBusy
        // guard were ever deleted.
        vm.SelectedRows = new[]
        {
            new StandardiseNameRow("x.pdf", "x.pdf", _dir.File("x-y-z-w-v.pdf"), StandardiseRowStatus.Unchanged),
        };
        // Subscribed only AFTER SelectedRows above (which itself raises
        // CanExecuteChanged) — otherwise that earlier raise alone would
        // satisfy the "raised > 0" check below even if IsBusy's OWN raise
        // call were ever deleted, exactly the trap a shared counter set up
        // too early falls into.
        var raised = 0;
        vm.PeelCommand.CanExecuteChanged += (_, _) => raised++;

        var addTask = vm.AddFilesAsync(new[] { a });
        var deadline = DateTime.UtcNow.AddSeconds(3);
        while (scheduler.DispatchCount == 0 && DateTime.UtcNow < deadline) await Task.Delay(5);
        Assert.True(vm.IsBusy);
        Assert.True(raised > 0, "IsBusy becoming true should have raised PeelCommand.CanExecuteChanged");
        Assert.False(vm.PeelCommand.CanExecute(null));

        // Fired without a direct await, same reasoning and same bounded-poll
        // shape as ASecondAddWhileABatchIsRunningIsIgnored above: if the
        // IsBusy guard were ever missing, this call would itself dispatch to
        // the same gated scheduler and block on the very gate this test only
        // releases below — a direct `await` would hang the whole test
        // process rather than fail it.
        var dispatchCountBeforePeelAttempt = scheduler.DispatchCount;
        var peelTask = vm.PeelSelectedAsync();
        var peelDeadline = DateTime.UtcNow.AddMilliseconds(500);
        while (!peelTask.IsCompleted && DateTime.UtcNow < peelDeadline) await Task.Delay(5);
        Assert.True(peelTask.IsCompleted,
            "a peel attempted while an add is already running must return immediately, not queue more work");
        // Never even tried to plan: the dispatch count the gated add itself
        // caused did not grow.
        Assert.Equal(dispatchCountBeforePeelAttempt, scheduler.DispatchCount);

        scheduler.Release();
        await addTask;
    }

    /// <summary>The owner's own worked example, verbatim, driven through the
    /// full view model rather than PlanPeel alone. The three files are
    /// dropped ALREADY in their final tidy form with a date that matches
    /// their own leading date, so PlanTidy's idempotence (TidyStemTests.
    /// ReapplyingWithTheSameDateIsANoOp) lands them as Unchanged rows,
    /// untouched on disk, with CurrentPath equal to their own path — the
    /// cleanest way to seed exact, known filenames for peeling without
    /// reasoning about what TidyStem itself would produce.</summary>
    [Fact]
    public async Task TheOwnersWorkedExampleBothClicksThroughTheFullViewModel()
    {
        var a = _dir.File("20260115-SMITH-JOHN-A12345-SCAN-001.pdf");
        var b = _dir.File("20260115-DOE-JANE-B9-COPY.pdf");
        var c = _dir.File("20260115-LEE-SAM-C77-SCAN-002-A.pdf");
        var dialogs = new FakeDialogs();
        dialogs.DateAnswers.Enqueue("20260115");
        var vm = new StandardiseNamesViewModel(dialogs, new InlineWorkScheduler());
        await vm.AddFilesAsync(new[] { a, b, c });
        Assert.Equal(3, vm.Results.Count);
        Assert.All(vm.Results, r => Assert.Equal(StandardiseRowStatus.Unchanged, r.Status));

        var rowA = vm.Results.Single(r => r.Current == "20260115-SMITH-JOHN-A12345-SCAN-001.pdf");
        var rowB = vm.Results.Single(r => r.Current == "20260115-DOE-JANE-B9-COPY.pdf");
        var rowC = vm.Results.Single(r => r.Current == "20260115-LEE-SAM-C77-SCAN-002-A.pdf");

        vm.SelectedRows = new[] { rowA, rowB, rowC };
        await vm.PeelSelectedAsync();

        Assert.Equal("20260115-SMITH-JOHN-A12345-SCAN.pdf", rowA.Result);
        Assert.Equal("20260115-DOE-JANE-B9.pdf", rowB.Result);
        Assert.Equal("20260115-LEE-SAM-C77-SCAN-002.pdf", rowC.Result);
        // all three were actually renamed by this click, including rowB and
        // rowC, which started life Unchanged — a peel that renames a row
        // sets Renamed regardless of what it was before (StandardiseRowStatus's
        // own doc comment).
        Assert.All(new[] { rowA, rowB, rowC }, r => Assert.Equal(StandardiseRowStatus.Renamed, r.Status));
        Assert.Equal("Removed the last segment from 3 files.", vm.Status);

        // click 2: same three selected again
        vm.SelectedRows = new[] { rowA, rowB, rowC };
        await vm.PeelSelectedAsync();

        Assert.Equal("20260115-SMITH-JOHN-A12345.pdf", rowA.Result);
        Assert.Equal("20260115-LEE-SAM-C77-SCAN.pdf", rowC.Result);

        // rowB is held at exactly four segments — completely untouched by
        // click 2, so it keeps click 1's own Result/Status, not reset to
        // anything new.
        Assert.Equal("20260115-DOE-JANE-B9.pdf", rowB.Result);
        Assert.Equal(StandardiseRowStatus.Renamed, rowB.Status);

        Assert.Contains("Removed the last segment from 2", vm.Status);
        Assert.Contains("1 already at four segments", vm.Status);

        Assert.True(File.Exists(Path.Combine(_dir.Path, "20260115-SMITH-JOHN-A12345.pdf")));
        Assert.True(File.Exists(Path.Combine(_dir.Path, "20260115-DOE-JANE-B9.pdf")));
        Assert.True(File.Exists(Path.Combine(_dir.Path, "20260115-LEE-SAM-C77-SCAN.pdf")));
    }

    [Fact]
    public async Task ARowAtTheFourSegmentFloorIsHeldAndReportedSeparately()
    {
        var src = _dir.File("20260115-SMITH-JOHN-A12.pdf");   // already 4 segments, matches the date
        var dialogs = new FakeDialogs();
        dialogs.DateAnswers.Enqueue("20260115");
        var vm = new StandardiseNamesViewModel(dialogs, new InlineWorkScheduler());
        await vm.AddFilesAsync(new[] { src });
        var row = vm.Results.Single();
        Assert.Equal(StandardiseRowStatus.Unchanged, row.Status);
        var resultBefore = row.Result;
        var pathBefore = row.CurrentPath;

        vm.SelectedRows = new[] { row };
        await vm.PeelSelectedAsync();

        Assert.Equal(resultBefore, row.Result);       // completely untouched
        Assert.Equal(pathBefore, row.CurrentPath);
        Assert.Equal(StandardiseRowStatus.Unchanged, row.Status);
        Assert.True(File.Exists(src));                 // file never moved
        Assert.Equal("Already at four segments.", vm.Status);
        // Fix round 1, item 10: a trailing CanExecute(null) == false here
        // used to be asserted too, but it is true BEFORE this act as well
        // (the only row ever added was this one, never renamed) — proves
        // nothing about the peel itself. ANoOpPeelClickDoesNotDisarmUndoForAnEarlierRealPeel
        // below is the real, non-vacuous fact for "a no-op peel leaves Undo
        // alone," seeded so CanExecute starts true.
    }

    /// <summary>The second rule: a collision is refused, never countered —
    /// through the full view model this time, not just PlanPeel in
    /// isolation. Two files whose ADD already gave them distinct names both
    /// peel down to the SAME shorter name; the first (in selection order)
    /// claims it, the second is refused outright — no "-2".</summary>
    [Fact]
    public async Task ACollisionIsRefusedNotCounteredAndReportedInTheStatusLine()
    {
        var a = _dir.File("A-B-C-D-ONE.pdf");
        var b = _dir.File("A-B-C-D-TWO.pdf");
        var dialogs = new FakeDialogs();
        dialogs.DateAnswers.Enqueue("20260115");
        var vm = new StandardiseNamesViewModel(dialogs, new InlineWorkScheduler());
        await vm.AddFilesAsync(new[] { a, b });
        var rowOne = vm.Results.Single(r => r.Current == "A-B-C-D-ONE.pdf");
        var rowTwo = vm.Results.Single(r => r.Current == "A-B-C-D-TWO.pdf");
        var twosPathBeforePeel = rowTwo.CurrentPath;
        var twosResultBeforePeel = rowTwo.Result;

        vm.SelectedRows = new[] { rowOne, rowTwo };
        await vm.PeelSelectedAsync();

        Assert.Equal("20260115-A-B-C-D.pdf", rowOne.Result);
        Assert.True(File.Exists(Path.Combine(_dir.Path, "20260115-A-B-C-D.pdf")));

        // rowTwo was refused: left exactly as it was, file untouched.
        Assert.Equal(twosResultBeforePeel, rowTwo.Result);
        Assert.Equal(twosPathBeforePeel, rowTwo.CurrentPath);
        Assert.True(File.Exists(twosPathBeforePeel));

        Assert.Contains("Removed the last segment from 1", vm.Status);
        Assert.Contains("already taken", vm.Status);
    }

    [Fact]
    public async Task UndoOfAPeelKeepsTheRowAndRestoresResultAndCurrentPath()
    {
        var src = _dir.File("A-B-C-D-EXTRA.pdf");
        var dialogs = new FakeDialogs();
        dialogs.DateAnswers.Enqueue("20260115");
        var vm = new StandardiseNamesViewModel(dialogs, new InlineWorkScheduler());
        await vm.AddFilesAsync(new[] { src });
        var row = vm.Results.Single();
        var resultBeforePeel = row.Result;
        var pathBeforePeel = row.CurrentPath;

        vm.SelectedRows = new[] { row };
        await vm.PeelSelectedAsync();
        Assert.NotEqual(resultBeforePeel, row.Result);
        Assert.True(vm.UndoCommand.CanExecute(null));
        var peeledPath = row.CurrentPath;   // captured before Undo puts it back

        await vm.UndoLastBatchAsync();

        Assert.Equal(resultBeforePeel, row.Result);
        Assert.Equal(pathBeforePeel, row.CurrentPath);
        Assert.Contains(vm.Results, r => ReferenceEquals(r, row));   // KEPT — not removed like an add-undo
        Assert.True(File.Exists(pathBeforePeel));
        Assert.False(File.Exists(peeledPath));   // the peeled name is gone, not just present under a new one
        Assert.Equal("Last segment restored.", vm.Status);
        Assert.False(vm.UndoCommand.CanExecute(null));
    }

    /// <summary>The peel-side mirror of UndoOfAPartlyRevertedBatchKeepsTheUnrestoredFileArmedForRetry
    /// above: a real FileShare.Read lock (no delete/rename share) on the
    /// PEELED file makes File.Move throw, so the row cannot be put back on
    /// the first try. It must stay exactly as the peel left it, stay in the
    /// grid, and stay armed for a retry — then a second Undo, once the lock
    /// is released, finishes the job.</summary>
    [Fact]
    public async Task UndoOfAPeelThatCannotBeRestoredKeepsTheRowArmedForRetry()
    {
        var src = _dir.File("A-B-C-D-EXTRA.pdf");
        var dialogs = new FakeDialogs();
        dialogs.DateAnswers.Enqueue("20260115");
        var vm = new StandardiseNamesViewModel(dialogs, new InlineWorkScheduler());
        await vm.AddFilesAsync(new[] { src });
        var row = vm.Results.Single();
        var resultBeforePeel = row.Result;

        vm.SelectedRows = new[] { row };
        await vm.PeelSelectedAsync();
        var peeledPath = row.CurrentPath;
        Assert.NotEqual(resultBeforePeel, row.Result);

        using (File.Open(peeledPath, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            await vm.UndoLastBatchAsync();
        }

        Assert.NotEqual(resultBeforePeel, row.Result);          // still the post-peel name
        Assert.Equal(peeledPath, row.CurrentPath);
        Assert.Contains(vm.Results, r => ReferenceEquals(r, row));
        Assert.True(vm.UndoCommand.CanExecute(null));
        Assert.Contains("Couldn't restore", vm.Status);

        // retry, now that the lock is released (the using block above)
        await vm.UndoLastBatchAsync();

        Assert.Equal(resultBeforePeel, row.Result);
        Assert.False(vm.UndoCommand.CanExecute(null));
        Assert.Equal("Last segment restored.", vm.Status);
    }

    /// <summary>Execute is already per-file fail-soft for a peel, the same
    /// as it is for an add (BulkRename.Execute's own IOException/
    /// UnauthorizedAccessException catch) — this proves ApplyPeelResult
    /// surfaces that failure on the row rather than silently leaving it
    /// looking untouched. A real FileShare.Read lock (no delete/rename
    /// share) on the file BEFORE the peel is what actually makes File.Move
    /// throw — the same mechanism AFileInUseIsReportedAsFailedRatherThanVanishing
    /// uses for the add side.</summary>
    [Fact]
    public async Task AFileLockedDuringThePeelItselfIsReportedAsFailedNotSilentlyDropped()
    {
        var src = _dir.File("A-B-C-D-EXTRA.pdf");
        var dialogs = new FakeDialogs();
        dialogs.DateAnswers.Enqueue("20260115");
        var vm = new StandardiseNamesViewModel(dialogs, new InlineWorkScheduler());
        await vm.AddFilesAsync(new[] { src });
        var row = vm.Results.Single();
        var pathBeforePeel = row.CurrentPath;
        var resultBeforePeel = row.Result;   // fix round 1, item 10: captured so the
                                              // assertion below proves Result CHANGED,
                                              // not merely that it is non-empty (the
                                              // pre-peel filename is also non-empty,
                                              // so that check passed even with the
                                              // failure branch deleted)

        using (File.Open(pathBeforePeel, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            vm.SelectedRows = new[] { row };
            await vm.PeelSelectedAsync();
        }

        Assert.Equal(StandardiseRowStatus.Failed, row.Status);
        Assert.NotEqual(resultBeforePeel, row.Result);   // the error text, not the untouched pre-peel name
        Assert.Equal(pathBeforePeel, row.CurrentPath);   // never moved
        Assert.True(File.Exists(pathBeforePeel));
        Assert.Contains("failed", vm.Status);
        // Fix round 1, item 1's own guard reads "did THIS click rename
        // anything" (peeledOutcomes.Count > 0), which is false here whether
        // every row was held/refused OR — this test's own case — every row's
        // Execute attempt failed: none of those renamed anything either. So
        // this failed peel must not wipe the ADD's own real undo record —
        // Undo is still armed, and would reverse the ADD (which genuinely
        // renamed this file), not this peel (which moved nothing).
        Assert.True(vm.UndoCommand.CanExecute(null));
    }

    /// <summary>PlanPeel's own doc comment names the one gap its plan-time
    /// refusal cannot see: a target claimed between planning and the actual
    /// move. This forces exactly that race — a file appears at the peeled
    /// target in the instant between the peel's PlanPeel dispatch and its
    /// Execute dispatch — and proves two things at once: Execute's own
    /// counter really does catch what PlanPeel could not, and PeelSelectedAsync
    /// really does pass CollisionSuffixStyle.Dashed to it (a Parenthesized
    /// fallback would land "A-B-C-D (2).pdf", not "A-B-C-D-2.pdf").</summary>
    private sealed class CreatesFileBeforeNthCallScheduler : IWorkScheduler
    {
        private readonly int _triggerOnCall;
        private readonly Action _createRaceFile;
        private int _callCount;

        public CreatesFileBeforeNthCallScheduler(int triggerOnCall, Action createRaceFile)
        {
            _triggerOnCall = triggerOnCall;
            _createRaceFile = createRaceFile;
        }

        public Task<T> Run<T>(Func<T> work)
        {
            _callCount++;
            if (_callCount == _triggerOnCall) _createRaceFile();
            return Task.FromResult(work());
        }

        public Task Run(Action work) => Run(() => { work(); return true; });
    }

    [Fact]
    public async Task ARaceBetweenPlanningAndMovingStillGetsADashedCounterNotParenthesized()
    {
        // AddFilesAsync's own PlanTidy prepends the batch date first (this
        // source has no leading date of its own), so the row this peel acts
        // on is "20260115-A-B-C-D-EXTRA.pdf" — the peeled TARGET is
        // therefore "20260115-A-B-C-D.pdf", not the bare "A-B-C-D.pdf" the
        // source's own name might suggest.
        var src = _dir.File("A-B-C-D-EXTRA.pdf");
        var racePath = Path.Combine(_dir.Path, "20260115-A-B-C-D.pdf");
        var dialogs = new FakeDialogs();
        dialogs.DateAnswers.Enqueue("20260115");
        // Call 1: AddFilesAsync's own intake dispatch. Call 2: its Execute
        // dispatch. Call 3: the peel's own PlanPeel dispatch (must see the
        // target as free). Call 4: the peel's own Execute dispatch — the
        // race file appears immediately before this one runs, after
        // PlanPeel has already returned its (correct, at the time) plan.
        var scheduler = new CreatesFileBeforeNthCallScheduler(
            triggerOnCall: 4, () => File.WriteAllText(racePath, "x"));
        var vm = new StandardiseNamesViewModel(dialogs, scheduler);
        await vm.AddFilesAsync(new[] { src });
        var row = vm.Results.Single();

        vm.SelectedRows = new[] { row };
        await vm.PeelSelectedAsync();

        Assert.Equal("20260115-A-B-C-D-2.pdf", row.Result);
        Assert.True(File.Exists(Path.Combine(_dir.Path, "20260115-A-B-C-D-2.pdf")));
    }

    // -------------------------------------------------- Fix round 1 additions

    /// <summary>Fix round 1, item 1(a) — the HIGH finding's own first fact: a
    /// click that peels NOTHING must not wipe a real earlier peel's undo
    /// record. Two rows: one already at the floor, one peelable. Peel the
    /// peelable row (arms Undo), then select and peel ONLY the at-floor row
    /// (a genuine no-op click) — Undo must still be armed, and must still
    /// reverse the FIRST peel, not have been silently disarmed by the
    /// second, empty one.</summary>
    [Fact]
    public async Task ANoOpPeelClickDoesNotDisarmUndoForAnEarlierRealPeel()
    {
        var atFloor = _dir.File("20260115-SMITH-JOHN-A12.pdf");   // already 4 segments
        var peelable = _dir.File("A-B-C-D-EXTRA.pdf");
        var dialogs = new FakeDialogs();
        dialogs.DateAnswers.Enqueue("20260115");
        var vm = new StandardiseNamesViewModel(dialogs, new InlineWorkScheduler());
        await vm.AddFilesAsync(new[] { atFloor, peelable });
        var atFloorRow = vm.Results.Single(r => r.Current == "20260115-SMITH-JOHN-A12.pdf");
        var peelableRow = vm.Results.Single(r => r.Current == "A-B-C-D-EXTRA.pdf");

        vm.SelectedRows = new[] { peelableRow };
        await vm.PeelSelectedAsync();
        var peeledPath = peelableRow.CurrentPath;
        Assert.Equal("20260115-A-B-C-D.pdf", peelableRow.Result);
        Assert.True(vm.UndoCommand.CanExecute(null));

        // The no-op click: only the at-floor row selected, held, nothing renamed.
        vm.SelectedRows = new[] { atFloorRow };
        await vm.PeelSelectedAsync();
        Assert.Equal("Already at four segments.", vm.Status);

        // Undo must still be armed, and must still reverse the FIRST peel.
        Assert.True(vm.UndoCommand.CanExecute(null));
        await vm.UndoLastBatchAsync();

        Assert.Equal("20260115-A-B-C-D-EXTRA.pdf", peelableRow.Result);
        Assert.True(File.Exists(Path.Combine(_dir.Path, "20260115-A-B-C-D-EXTRA.pdf")));
        Assert.False(File.Exists(peeledPath));
        Assert.Equal("Last segment restored.", vm.Status);
    }

    /// <summary>Fix round 1, item 1(b): the same guard, the other direction —
    /// a no-op ADD (every dropped file already standardised) must not wipe a
    /// real earlier PEEL's undo record either.</summary>
    [Fact]
    public async Task ANoOpAddAfterARealPeelLeavesThePeelUndoable()
    {
        var peelable = _dir.File("A-B-C-D-EXTRA.pdf");
        var dialogs = new FakeDialogs();
        dialogs.DateAnswers.Enqueue("20260115");
        var vm = new StandardiseNamesViewModel(dialogs, new InlineWorkScheduler());
        await vm.AddFilesAsync(new[] { peelable });
        var row = vm.Results.Single();

        vm.SelectedRows = new[] { row };
        await vm.PeelSelectedAsync();
        Assert.True(vm.UndoCommand.CanExecute(null));

        // The no-op add: already exactly standardised for this date, so
        // PlanTidy's own idempotence (TidyStemTests.ReapplyingWithTheSameDateIsANoOp)
        // means this renames nothing.
        var alreadyTidy = _dir.File("20260115-DOE-JANE.pdf");
        dialogs.DateAnswers.Enqueue("20260115");
        await vm.AddFilesAsync(new[] { alreadyTidy });
        Assert.Contains(vm.Results,
            r => r.Current == "20260115-DOE-JANE.pdf" && r.Status == StandardiseRowStatus.Unchanged);

        // The earlier peel is still undoable.
        Assert.True(vm.UndoCommand.CanExecute(null));
        await vm.UndoLastBatchAsync();

        Assert.Equal("20260115-A-B-C-D-EXTRA.pdf", row.Result);
        Assert.True(File.Exists(Path.Combine(_dir.Path, "20260115-A-B-C-D-EXTRA.pdf")));
        Assert.Equal("Last segment restored.", vm.Status);
    }

    /// <summary>Fix round 1, item 3 — the most valuable coverage gap: making
    /// the row observable was the entire structural change this feature
    /// made, and the grid binds Result (text) and Status (the Foreground
    /// triggers) live. Replacing either internal setter's Set(...) call with
    /// a plain field assignment would pass every value-only assertion
    /// elsewhere in this file while silently freezing the grid. Same
    /// convention as ListReformatViewModelTests.IsCustomDelimiterRaisesPropertyChangedWhenTheShapeChanges
    /// and FilenameListViewModelTests.TheSaveLabelNamesTheFormatSaveWillActuallyWrite.
    /// Covers both directions: the peel itself, and the undo-of-a-peel
    /// restore. The row starts Unchanged (not Renamed) deliberately —
    /// ObservableObject.Set suppresses a no-op re-assignment of the SAME
    /// enum value, so peeling a row that started life Renamed would never
    /// actually exercise Status's own raise.</summary>
    [Fact]
    public async Task PropertyChangedFiresForResultAndStatusOnBothThePeelAndItsUndo()
    {
        var src = _dir.File("20260115-SMITH-JOHN-A12345-SCAN.pdf");   // already tidy: 5 segments, Unchanged
        var dialogs = new FakeDialogs();
        dialogs.DateAnswers.Enqueue("20260115");
        var vm = new StandardiseNamesViewModel(dialogs, new InlineWorkScheduler());
        await vm.AddFilesAsync(new[] { src });
        var row = vm.Results.Single();
        Assert.Equal(StandardiseRowStatus.Unchanged, row.Status);
        Assert.Equal("already standardised", row.Result);   // ApplyBatchResult's own literal for Unchanged, not a filename

        var raisedByPeel = new List<string?>();
        row.PropertyChanged += (_, e) => raisedByPeel.Add(e.PropertyName);

        vm.SelectedRows = new[] { row };
        await vm.PeelSelectedAsync();

        Assert.Equal("20260115-SMITH-JOHN-A12345.pdf", row.Result);
        Assert.Equal(StandardiseRowStatus.Renamed, row.Status);   // an actual value change
        Assert.Contains(nameof(row.Result), raisedByPeel);
        Assert.Contains(nameof(row.Status), raisedByPeel);

        var raisedByUndo = new List<string?>();
        row.PropertyChanged += (_, e) => raisedByUndo.Add(e.PropertyName);

        await vm.UndoLastBatchAsync();

        // Restored to the exact PRE-peel Result PeelUndoEntry captured —
        // "already standardised" again, not a re-derived filename.
        Assert.Equal("already standardised", row.Result);
        Assert.Equal(StandardiseRowStatus.Unchanged, row.Status);   // back to where it started: another actual change
        Assert.Contains(nameof(row.Result), raisedByUndo);
        Assert.Contains(nameof(row.Status), raisedByUndo);
    }

    /// <summary>Fix round 1, item 4: every OTHER peel test selects every row
    /// in the grid, so "acts on the selection" was indistinguishable from
    /// "acts on everything." Two rows with NON-colliding peeled targets, only
    /// one selected — if a future change ever swapped SelectedRows for
    /// Results.ToList(), the unselected row's Result would visibly change
    /// too (there is no collision to coincidentally leave it looking
    /// untouched), so this actually catches that regression rather than
    /// merely gesturing at it.</summary>
    [Fact]
    public async Task OnlyTheSelectedRowIsPeeledTheOtherIsCompletelyUntouched()
    {
        var a = _dir.File("A-B-C-D-ONE.pdf");
        var b = _dir.File("W-X-Y-Z-TWO.pdf");
        var dialogs = new FakeDialogs();
        dialogs.DateAnswers.Enqueue("20260115");
        var vm = new StandardiseNamesViewModel(dialogs, new InlineWorkScheduler());
        await vm.AddFilesAsync(new[] { a, b });
        var rowOne = vm.Results.Single(r => r.Current == "A-B-C-D-ONE.pdf");
        var rowTwo = vm.Results.Single(r => r.Current == "W-X-Y-Z-TWO.pdf");
        var twosResultBefore = rowTwo.Result;
        var twosPathBefore = rowTwo.CurrentPath;
        var twosStatusBefore = rowTwo.Status;

        vm.SelectedRows = new[] { rowOne };   // only ONE of the two rows in the grid
        await vm.PeelSelectedAsync();

        Assert.Equal("20260115-A-B-C-D.pdf", rowOne.Result);

        Assert.Equal(twosResultBefore, rowTwo.Result);
        Assert.Equal(twosPathBefore, rowTwo.CurrentPath);
        Assert.Equal(twosStatusBefore, rowTwo.Status);
        Assert.True(File.Exists(twosPathBefore));

        // Exact match, not Contains: acting on everything would ALSO attempt
        // rowTwo and report it somehow (even a no-op still isn't THIS exact
        // single-row message), so this string is itself part of the proof.
        Assert.Equal("Removed the last segment from 1 file.", vm.Status);
    }

    /// <summary>Fix round 1, item 5: PeelCommand's own OnError, the sibling
    /// fact to UndoCommandReportsAnUnexpectedSchedulerFailureInsteadOfGoingSilent
    /// above. Driven through PeelCommand.Execute itself, not a direct
    /// PeelSelectedAsync call — a direct call bypasses the very
    /// AsyncRelayCommand catch-and-route this guards.</summary>
    [Fact]
    public async Task PeelCommandReportsAnUnexpectedSchedulerFailureInsteadOfGoingSilent()
    {
        var src = _dir.File("A-B-C-D-EXTRA.pdf");
        var dialogs = new FakeDialogs();
        dialogs.DateAnswers.Enqueue("20260115");
        // Call 1: AddFilesAsync's own intake dispatch. Call 2: its Execute
        // dispatch. Call 3: the peel's own PlanPeel dispatch — the one this
        // fact needs to fail.
        var scheduler = new FailsOnNthCallScheduler(failOnCall: 3);
        var vm = new StandardiseNamesViewModel(dialogs, scheduler);
        await vm.AddFilesAsync(new[] { src });
        var row = vm.Results.Single();
        vm.SelectedRows = new[] { row };
        Assert.True(vm.PeelCommand.CanExecute(null));

        vm.PeelCommand.Execute(null);
        await vm.PeelCommand.Completion;

        Assert.Contains("unexpectedly", vm.Status);
        Assert.False(vm.IsBusy);
    }

    /// <summary>Fix round 1, item 6: the undo slot must switch back to Add
    /// after a peel. Add -> Peel -> Add -> Undo: the SECOND add is what
    /// Undo reverses (its own row removed), and the peeled row from the
    /// FIRST operation is left completely alone — proving _lastBatchKind
    /// tracks the MOST RECENT operation, not just "whichever kind ran
    /// first."</summary>
    [Fact]
    public async Task TheUndoSlotSwitchesBackFromPeelToAdd()
    {
        var a = _dir.File("A-B-C-D-EXTRA.pdf");
        var dialogs = new FakeDialogs();
        dialogs.DateAnswers.Enqueue("20260115");
        var vm = new StandardiseNamesViewModel(dialogs, new InlineWorkScheduler());
        await vm.AddFilesAsync(new[] { a });   // Add
        var rowA = vm.Results.Single();

        vm.SelectedRows = new[] { rowA };
        await vm.PeelSelectedAsync();   // Peel
        var peeledResult = rowA.Result;
        var peeledPath = rowA.CurrentPath;

        var b = _dir.File("smith, john.pdf");
        dialogs.DateAnswers.Enqueue("20260115");
        await vm.AddFilesAsync(new[] { b });   // Add again
        var renamedB = Path.Combine(_dir.Path, "20260115-SMITH-JOHN.pdf");
        Assert.True(File.Exists(renamedB));

        await vm.UndoLastBatchAsync();   // Undo

        // The SECOND add is what got reversed.
        Assert.True(File.Exists(b));
        Assert.False(File.Exists(renamedB));
        Assert.DoesNotContain(vm.Results, r => r.Current == "smith, john.pdf");
        Assert.False(vm.UndoCommand.CanExecute(null));

        // The peeled row from the FIRST operation is left completely alone.
        Assert.Equal(peeledResult, rowA.Result);
        Assert.Equal(peeledPath, rowA.CurrentPath);
        Assert.Contains(vm.Results, r => ReferenceEquals(r, rowA));
        Assert.True(File.Exists(peeledPath));
    }

    /// <summary>Fix round 1, item 7: nothing proved Undo reverses only the
    /// LAST peel rather than cascading back to the very first name. Peel 6
    /// segments down to 5, then 5 down to 4 (two separate clicks), then Undo
    /// once: must land back at the post-click-1 (5-segment) name, not the
    /// original.</summary>
    [Fact]
    public async Task UndoAfterTwoPeelsLandsAtTheClickOneStateNotTheOriginal()
    {
        var src = _dir.File("A-B-C-D-E.pdf");   // tidies to 6 segments
        var dialogs = new FakeDialogs();
        dialogs.DateAnswers.Enqueue("20260115");
        var vm = new StandardiseNamesViewModel(dialogs, new InlineWorkScheduler());
        await vm.AddFilesAsync(new[] { src });
        var row = vm.Results.Single();
        var originalResult = row.Result;
        Assert.Equal("20260115-A-B-C-D-E.pdf", originalResult);

        vm.SelectedRows = new[] { row };
        await vm.PeelSelectedAsync();   // click 1: 6 -> 5 segments
        var afterClick1 = row.Result;
        Assert.Equal("20260115-A-B-C-D.pdf", afterClick1);

        await vm.PeelSelectedAsync();   // click 2: 5 -> 4 segments
        Assert.Equal("20260115-A-B-C.pdf", row.Result);

        await vm.UndoLastBatchAsync();   // undoes ONLY click 2

        Assert.Equal(afterClick1, row.Result);        // the click-1 state
        Assert.NotEqual(originalResult, row.Result);   // NOT cascaded all the way back
        Assert.True(File.Exists(Path.Combine(_dir.Path, "20260115-A-B-C-D.pdf")));
        Assert.False(File.Exists(Path.Combine(_dir.Path, "20260115-A-B-C.pdf")));
        Assert.False(vm.UndoCommand.CanExecute(null));   // one step of undo only
    }

    /// <summary>Fix round 1, item 8: all four outcome categories in ONE
    /// click — held, collision-refused, succeeded, failed — interleaved so a
    /// held row sits between the collision pair and the collision LOSER sits
    /// between the collision winner and the locked row. This is exactly the
    /// shape that would expose an outcomeIndex off-by-one (ApplyPeelResult
    /// only advances it for a Changed plan, so a held/refused row sitting
    /// between two Changed ones is what actually proves the skip is correct,
    /// rather than merely untested) — its symptom would be a name, or an
    /// error message, landing on the wrong row.</summary>
    [Fact]
    public async Task AMixedOutcomeBatchInOneClickAssignsEachResultToTheRightRow()
    {
        var held = _dir.File("20260115-SMITH-JOHN-A12.pdf");   // already 4 segments
        var winner = _dir.File("P-Q-R-S-ALPHA.pdf");
        var loser = _dir.File("P-Q-R-S-BETA.pdf");              // collides with winner's peeled target
        var locked = _dir.File("M-N-O-P-Q.pdf");
        var dialogs = new FakeDialogs();
        dialogs.DateAnswers.Enqueue("20260115");
        var vm = new StandardiseNamesViewModel(dialogs, new InlineWorkScheduler());
        await vm.AddFilesAsync(new[] { held, winner, loser, locked });

        var heldRow = vm.Results.Single(r => r.Current == "20260115-SMITH-JOHN-A12.pdf");
        var winnerRow = vm.Results.Single(r => r.Current == "P-Q-R-S-ALPHA.pdf");
        var loserRow = vm.Results.Single(r => r.Current == "P-Q-R-S-BETA.pdf");
        var lockedRow = vm.Results.Single(r => r.Current == "M-N-O-P-Q.pdf");

        var heldResultBefore = heldRow.Result;
        var loserResultBefore = loserRow.Result;
        var loserPathBefore = loserRow.CurrentPath;

        // Interleaved deliberately: held, winner, loser, locked — a
        // held/refused row on each side of a Changed one.
        vm.SelectedRows = new[] { heldRow, winnerRow, loserRow, lockedRow };

        using (File.Open(lockedRow.CurrentPath, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            await vm.PeelSelectedAsync();
        }

        // held: completely untouched.
        Assert.Equal(heldResultBefore, heldRow.Result);
        Assert.Equal(StandardiseRowStatus.Unchanged, heldRow.Status);

        // winner: peeled successfully to its OWN, correct target.
        Assert.Equal("20260115-P-Q-R-S.pdf", winnerRow.Result);
        Assert.Equal(StandardiseRowStatus.Renamed, winnerRow.Status);
        Assert.True(File.Exists(Path.Combine(_dir.Path, "20260115-P-Q-R-S.pdf")));

        // loser: refused, completely untouched (not "-2", not winner's name).
        Assert.Equal(loserResultBefore, loserRow.Result);
        Assert.Equal(loserPathBefore, loserRow.CurrentPath);
        Assert.True(File.Exists(loserPathBefore));

        // locked: attempted, Execute itself failed — its OWN error, not the
        // winner's name or anything belonging to another row.
        Assert.Equal(StandardiseRowStatus.Failed, lockedRow.Status);
        Assert.NotEqual("20260115-P-Q-R-S.pdf", lockedRow.Result);
        Assert.NotEqual(loserResultBefore, lockedRow.Result);

        Assert.Contains("Removed the last segment from 1", vm.Status);
        Assert.Contains("1 already at four segments", vm.Status);
        Assert.Contains("1 name already taken", vm.Status);
        Assert.Contains("1 failed", vm.Status);
    }

    /// <summary>Fix round 1, item 9 (correctness review's own finding 3): a
    /// peel that partly failed, then Undo. Two selected rows in one click —
    /// one succeeds, one is locked and fails — then Undo: only the succeeded
    /// row's peel is undoable (a Failed row was never moved, so Revert has
    /// nothing to reverse for it), and it must keep its Failed status and
    /// error text straight through the Undo, not be silently reset.</summary>
    [Fact]
    public async Task APeelThatPartlyFailedThenUndoOnlyRestoresTheSucceededRow()
    {
        var ok = _dir.File("A-B-C-D-ONE.pdf");
        var locked = _dir.File("W-X-Y-Z-TWO.pdf");
        var dialogs = new FakeDialogs();
        dialogs.DateAnswers.Enqueue("20260115");
        var vm = new StandardiseNamesViewModel(dialogs, new InlineWorkScheduler());
        await vm.AddFilesAsync(new[] { ok, locked });
        var okRow = vm.Results.Single(r => r.Current == "A-B-C-D-ONE.pdf");
        var lockedRow = vm.Results.Single(r => r.Current == "W-X-Y-Z-TWO.pdf");
        var okResultBeforePeel = okRow.Result;

        vm.SelectedRows = new[] { okRow, lockedRow };
        using (File.Open(lockedRow.CurrentPath, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            await vm.PeelSelectedAsync();
        }

        Assert.Equal("20260115-A-B-C-D.pdf", okRow.Result);
        Assert.Equal(StandardiseRowStatus.Failed, lockedRow.Status);
        var lockedErrorAfterPeel = lockedRow.Result;
        Assert.NotEqual("", lockedErrorAfterPeel);
        Assert.True(vm.UndoCommand.CanExecute(null));

        await vm.UndoLastBatchAsync();

        // Only the succeeded row restores.
        Assert.Equal(okResultBeforePeel, okRow.Result);
        Assert.True(File.Exists(Path.Combine(_dir.Path, "20260115-A-B-C-D-ONE.pdf")));

        // The failed row keeps its Failed status and error text straight
        // through the Undo.
        Assert.Equal(StandardiseRowStatus.Failed, lockedRow.Status);
        Assert.Equal(lockedErrorAfterPeel, lockedRow.Result);

        Assert.Equal("Last segment restored.", vm.Status);
        Assert.False(vm.UndoCommand.CanExecute(null));
    }
}
