using System.Diagnostics;
using OrdoSort.Wpf.Services;
using OrdoSort.Wpf.ViewModels;

namespace OrdoSort.Wpf.Tests;

/// <summary>Audit QC-04: the rename batch itself — not the preview, which
/// BulkRenameProbeTests already covers.
///
/// Apply()/UndoBatch() were wired to a plain RelayCommand and called
/// BulkRename.Execute/Revert — a foreach of File.Exists/File.Move — straight
/// from the click, on the UI thread. Two hundred files from a share froze the
/// whole app with no progress and no way out, and _lastOutcomes (the only
/// thing Undo reads) was assigned only AFTER the loop finished, so a batch
/// that never got to its last file left files renamed on disk with no undo
/// path at all.
///
/// Every test here drives the injected scheduler seam rather than a clock:
/// the audit flags the "returned in under 50ms" family as vacuous in one
/// direction (they pass just as well if the work is never armed at all), and
/// each test below pairs "the work was handed off" with "and it really
/// happened once the scheduler ran it".</summary>
public class BulkRenameBatchTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "ordobulkbatch_" + Guid.NewGuid());

    public BulkRenameBatchTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { /* best effort */ }
    }

    private string Touch(string name)
    {
        var p = Path.Combine(_dir, name);
        File.WriteAllText(p, "x");
        return p;
    }

    /// <summary>The same hold-the-work-in-flight double as the shared
    /// ControlledWorkScheduler, with a lock around the queue: this view
    /// model's preview probe dispatches from a System.Threading.Timer
    /// callback, so items arrive on a pool thread while the test thread is
    /// reading the queue, which the shared one's bare List cannot survive.
    ///
    /// Releasing runs the item on the CALLING thread, and these tests are
    /// synchronous (no xUnit async context), so the awaiting continuation —
    /// the next dispatch, the status line, the outcome record — has already
    /// landed by the time Release returns. That is what makes "one rename
    /// has happened, the next has not started" an observable state rather
    /// than a race.</summary>
    private sealed class QueuedWorkScheduler : IWorkScheduler
    {
        private readonly object _gate = new();
        private readonly List<Action> _queued = new();

        /// <summary>Dispatched-but-not-yet-run items.</summary>
        public int Queued { get { lock (_gate) return _queued.Count; } }

        public Task<T> Run<T>(Func<T> work)
        {
            var completion = new TaskCompletionSource<T>();
            lock (_gate)
                _queued.Add(() =>
                {
                    try { completion.SetResult(work()); }
                    catch (Exception ex) { completion.SetException(ex); }
                });
            return completion.Task;
        }

        public Task Run(Action work)
        {
            var completion = new TaskCompletionSource();
            lock (_gate)
                _queued.Add(() =>
                {
                    try { work(); completion.SetResult(); }
                    catch (Exception ex) { completion.SetException(ex); }
                });
            return completion.Task;
        }

        /// <summary>Run the oldest outstanding item, waiting for one to be
        /// dispatched if the timer hasn't got there yet.
        ///
        /// The item runs OUTSIDE the lock, deliberately: it is released on
        /// the test thread, and DebouncedProbe's timer thread takes its own
        /// gate before dispatching here. Holding this one across the call
        /// deadlocks the pair the first time a released continuation re-arms
        /// the probe — measured, not theoretical.</summary>
        public void ReleaseNext(string because, int timeoutMs = 3000)
        {
            var sw = Stopwatch.StartNew();
            while (true)
            {
                Action? item = null;
                lock (_gate)
                    if (_queued.Count > 0)
                    {
                        item = _queued[0];
                        _queued.RemoveAt(0);
                    }
                if (item is not null) { item(); return; }
                if (sw.ElapsedMilliseconds > timeoutMs)
                    Assert.Fail($"nothing was scheduled within {timeoutMs}ms: {because}");
                Thread.Sleep(5);
            }
        }

        /// <summary>Release stragglers until nothing has been dispatched for
        /// a short quiet spell, so "how many items are queued" is a stable
        /// number a test can compare against. The preview probe dispatches
        /// from a timer, so "the preview is right" does not by itself mean
        /// the last trigger has finished arriving.</summary>
        public void Quiesce(int quietMs = 150)
        {
            var sw = Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < quietMs)
            {
                if (Queued > 0) { ReleaseNext("quiescing"); sw.Restart(); }
                else Thread.Sleep(5);
            }
        }

        /// <summary>Release items as they are dispatched until the condition
        /// holds — how a test gets past the intake and preview work that
        /// share this scheduler with the batch under test.</summary>
        public void Settle(Func<bool> until, string because, int timeoutMs = 3000)
        {
            var sw = Stopwatch.StartNew();
            while (!until())
            {
                if (sw.ElapsedMilliseconds > timeoutMs)
                    Assert.Fail($"condition never became true within {timeoutMs}ms: {because}");
                if (Queued > 0) ReleaseNext(because, timeoutMs);
                else Thread.Sleep(5);
            }
        }
    }

    /// <summary>A batch of <paramref name="names"/> with a prefix rule that
    /// changes every one of them, settled so the preview really is showing
    /// what Rename would execute.</summary>
    private (BulkRenameViewModel Vm, QueuedWorkScheduler Scheduler) Batch(params string[] names)
    {
        var scheduler = new QueuedWorkScheduler();
        var vm = new BulkRenameViewModel(scheduler: scheduler, probeDelayMs: 0);
        foreach (var n in names) Touch(n);
        vm.AddFilesAsync(names.Select(n => Path.Combine(_dir, n)).ToList());
        vm.Prefix = "NEW-";
        scheduler.Settle(
            () => vm.Preview.Count == names.Length && vm.Preview.All(r => r.Changed),
            "the preview should settle on the prefix rule before the batch starts");
        scheduler.Quiesce();
        return (vm, scheduler);
    }

    // ---- 1. the renames themselves go through the scheduler ---------------

    /// <summary>The defect itself: clicking Rename used to run every
    /// File.Move on the calling thread before the call returned. Asserted
    /// through the seam rather than by timing — the file is still under its
    /// old name and one more item is waiting on the scheduler — and paired
    /// with the real effect, so "never armed at all" can't pass it either.</summary>
    [Fact]
    public void RenameHandsTheFileWorkToTheSchedulerInsteadOfDoingItOnTheClick()
    {
        var (vm, scheduler) = Batch("scan_001.pdf");
        var src = Path.Combine(_dir, "scan_001.pdf");
        var queuedBefore = scheduler.Queued;

        vm.RenameCommand.Execute(null);

        Assert.True(File.Exists(src), "Rename moved the file on the calling thread");
        Assert.Equal(queuedBefore + 1, scheduler.Queued);

        scheduler.Settle(() => !File.Exists(src), "the queued rename should happen once the scheduler runs it");
        Assert.True(File.Exists(Path.Combine(_dir, "NEW-scan_001.pdf")));
    }

    // ---- 2. undo survives a batch that doesn't reach its last file --------

    /// <summary>The durability half of QC-04, observed where it matters:
    /// PARTWAY through the batch. One file has moved, the next has not
    /// started, and the outcome for the one that moved is already recorded —
    /// so anything that ends the batch here (a cancel, a closed window, a
    /// killed process) still leaves Undo something to put back.
    ///
    /// A check of the finished state would pass against the defect this pins,
    /// which assigned _lastOutcomes only after the loop: at the end, both
    /// versions hold the same list. Only the mid-batch look tells them
    /// apart.</summary>
    [Fact]
    public void EachRenameIsRecordedForUndoAsItHappensNotAfterTheWholeBatch()
    {
        var (vm, scheduler) = Batch("a.pdf", "b.pdf", "c.pdf");
        var a = Path.Combine(_dir, "a.pdf");
        var queuedBefore = scheduler.Queued;

        vm.RenameCommand.Execute(null);
        Assert.Equal(queuedBefore + 1, scheduler.Queued);   // the first rename, dispatched

        scheduler.ReleaseNext("the first rename should be dispatched");

        // Mid-batch: the second rename is dispatched but not run, so exactly
        // one file has moved.
        Assert.Equal(queuedBefore + 1, scheduler.Queued);
        Assert.False(File.Exists(a));
        Assert.True(File.Exists(Path.Combine(_dir, "b.pdf")));
        Assert.True(File.Exists(Path.Combine(_dir, "c.pdf")));

        Assert.Single(vm.LastOutcomes);
        Assert.Equal(a, vm.LastOutcomes[0].Source);
        Assert.Equal(Path.Combine(_dir, "NEW-a.pdf"), vm.LastOutcomes[0].Final);

        // Let the rest run rather than leaving a half-finished batch behind:
        // all three end up recorded, so the mid-batch state above is a
        // stage of a normal run, not a broken one.
        scheduler.Settle(() => vm.LastOutcomes.Count == 3, "the remaining renames should finish");
        Assert.Equal("Renamed 3 files.", vm.Status);
    }

    // ---- 3. cancel ---------------------------------------------------------

    /// <summary>Cancel stops the files that haven't started and says what
    /// actually happened. Checked between files, so the one already in flight
    /// finishes — that is the guarantee, not a rounding error.</summary>
    [Fact]
    public void CancellingABatchLeavesTheRemainingFilesAloneAndSaysSo()
    {
        var (vm, scheduler) = Batch("a.pdf", "b.pdf", "c.pdf");
        var a = Path.Combine(_dir, "a.pdf");
        var b = Path.Combine(_dir, "b.pdf");
        var c = Path.Combine(_dir, "c.pdf");

        vm.RenameCommand.Execute(null);
        Assert.True(vm.IsBusy);
        Assert.True(vm.CancelCommand.CanExecute(null));

        // Pressed while the first file is still in flight — the check that
        // matters happens before the SECOND file is dispatched.
        vm.CancelCommand.Execute(null);
        scheduler.ReleaseNext("the in-flight rename should still be dispatched");

        Assert.False(File.Exists(a));                 // the one that started, finished
        Assert.True(File.Exists(b));                  // the rest were never touched
        Assert.True(File.Exists(c));
        Assert.False(vm.IsBusy);
        Assert.Equal("Cancelled — renamed 1 of 3.", vm.Status);
    }

    /// <summary>The user-facing half of durability: after a cancelled batch,
    /// Undo puts back exactly the files that were renamed and leaves the rest
    /// alone. This is what the old post-loop assignment cost — the batch
    /// ended early, _lastOutcomes was still the previous batch's (usually
    /// empty), and the renames that DID happen had no way back.</summary>
    [Fact]
    public void UndoAfterACancelledBatchPutsBackWhatTheBatchRenamed()
    {
        var (vm, scheduler) = Batch("a.pdf", "b.pdf", "c.pdf");
        var a = Path.Combine(_dir, "a.pdf");

        vm.RenameCommand.Execute(null);
        vm.CancelCommand.Execute(null);
        scheduler.ReleaseNext("the in-flight rename should still be dispatched");
        Assert.False(File.Exists(a));

        Assert.True(vm.UndoCommand.CanExecute(null));
        vm.UndoCommand.Execute(null);
        scheduler.Settle(() => File.Exists(a), "the cancelled batch's one rename should be undone");

        Assert.False(File.Exists(Path.Combine(_dir, "NEW-a.pdf")));
        Assert.Empty(vm.LastOutcomes);
        Assert.Equal("Original names restored.", vm.Status);
    }

    // ---- 4. undo goes through the scheduler too ---------------------------

    /// <summary>BulkRename.Revert is the same foreach of File.Moves Execute
    /// is, and it ran on the UI thread for the same reason. Same seam
    /// assertion, same pairing with the real effect.</summary>
    [Fact]
    public void UndoHandsTheFileWorkToTheSchedulerInsteadOfDoingItOnTheClick()
    {
        var (vm, scheduler) = Batch("scan_001.pdf");
        var src = Path.Combine(_dir, "scan_001.pdf");
        var renamed = Path.Combine(_dir, "NEW-scan_001.pdf");
        vm.RenameCommand.Execute(null);
        scheduler.Settle(() => File.Exists(renamed), "the rename should land before undo is tested");
        scheduler.Quiesce();
        var queuedBefore = scheduler.Queued;

        vm.UndoCommand.Execute(null);

        Assert.True(File.Exists(renamed), "Undo moved the file back on the calling thread");
        Assert.Equal(queuedBefore + 1, scheduler.Queued);

        scheduler.Settle(() => File.Exists(src), "the queued revert should happen once the scheduler runs it");
        Assert.False(File.Exists(renamed));
    }
}
