using System.Diagnostics;
using OrdoSort.Wpf.Services;
using OrdoSort.Wpf.ViewModels;
using static OrdoSort.Core.BulkRename;

namespace OrdoSort.Wpf.Tests;

/// <summary>Task 2 (2026-08-05 debounce pair, audit finding 5.2[A]):
/// BulkRenameViewModel.Refresh used to call BulkRename.Plan — a File.Exists
/// per file, more per collision (BulkRename.cs:159-161) — synchronously on
/// the UI thread from every op setter (Find/Replace/Prefix/Suffix and the
/// discrete toggles), so a typed keystroke on an SMB destination paid a
/// network round trip per file in the tool built for batches. This file pins
/// both halves of the fix: the plan now computes off the UI thread through
/// DebouncedProbe (mirroring the probes already proven in
/// RouteEditVm/WatchEditVm/SettingsViewModel/TilePreviewProbeTests), and a
/// burst of keystrokes coalesces into one Plan() call rather than one per
/// character. See ToolViewModelTests.BulkRenameViewModelTests for the
/// pre-existing behavioral tests, now polling for the same reason
/// SettingsViewModelTests' probes do.</summary>
public class BulkRenameProbeTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "ordobulkprobe_" + Guid.NewGuid());

    public BulkRenameProbeTests() => Directory.CreateDirectory(_dir);

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

    /// <summary>Same shape as SettingsViewModelTests.WaitFor /
    /// TilePreviewProbeTests.WaitFor: the plan is debounced and off the UI
    /// thread, so "eventually correct" has to be polled for rather than
    /// asserted the instant a call returns.</summary>
    private static void WaitFor(Func<bool> condition, string because, int timeoutMs = 3000)
    {
        var sw = Stopwatch.StartNew();
        while (!condition())
        {
            if (sw.ElapsedMilliseconds > timeoutMs)
                Assert.Fail($"condition never became true within {timeoutMs}ms: {because}");
            Thread.Sleep(5);
        }
    }

    /// <summary>A deliberately slow stand-in for BulkRenameViewModel's `plan`
    /// seam — same Thread.Sleep-wrapped-dependency technique as
    /// SettingsViewModelTests' directoryExists/validateRoute stand-ins and
    /// TilePreviewProbeTests' slow folderStatus stand-in, but pointed at the
    /// COMPUTE itself (BulkRename.Plan) rather than the scheduler that
    /// dispatches it. This matters: injecting latency at the scheduler only
    /// proves the scheduler is exercised asynchronously — it does NOT prove a
    /// regression to a synchronous Plan() call in the setter would be caught,
    /// because a synchronous call bypasses the scheduler (and any latency
    /// hung on it) entirely. Sleeping inside the compute itself stands in for
    /// the real File.Exists cost finding 5.2 is about, so a setter that
    /// regresses to calling this synchronously WILL block for real.</summary>
    private static List<PlannedRename> SlowPlan(int delayMs,
        IEnumerable<string> paths, RenameOp op, IReadOnlyDictionary<string, string>? overrides)
    {
        Thread.Sleep(delayMs);
        return Plan(paths, op, overrides);
    }

    /// <summary>Counts how many times the scheduler is actually asked to run
    /// work — i.e. how many times the debounce timer fired and a real Plan()
    /// call happened — while still doing the work for real via
    /// TaskWorkScheduler underneath.</summary>
    private sealed class CountingWorkScheduler : IWorkScheduler
    {
        private readonly Action _onRun;
        private readonly IWorkScheduler _inner = new TaskWorkScheduler();
        public CountingWorkScheduler(Action onRun) => _onRun = onRun;
        public Task<T> Run<T>(Func<T> work) { _onRun(); return _inner.Run(work); }
        public Task Run(Action work) { _onRun(); return _inner.Run(work); }
    }

    // ---- 1. the UI thread does not block on the plan itself ---------------

    [Fact]
    public void SettingFindReturnsPromptlyEvenWhilePlanItselfIsSlow()
    {
        var a = Touch("scan_001.pdf");
        var vm = new BulkRenameViewModel(plan: (paths, op, overrides) => SlowPlan(300, paths, op, overrides));
        vm.AddFiles(new[] { a });
        WaitFor(() => vm.Preview.Count == 1, "the initial add should settle before the timing measurement");

        var sw = Stopwatch.StartNew();
        vm.Find = "scan";
        sw.Stop();

        Assert.True(sw.ElapsedMilliseconds < 50,
            $"setting Find blocked for {sw.ElapsedMilliseconds}ms on the UI thread");
    }

    // ---- 2. the preview still becomes correct — this is not "never compute"

    [Fact]
    public void ThePreviewEventuallyReflectsTheSlowPlansResult()
    {
        var a = Touch("scan_001.pdf");
        var vm = new BulkRenameViewModel(plan: (paths, op, overrides) => SlowPlan(300, paths, op, overrides));
        vm.AddFiles(new[] { a });
        WaitFor(() => vm.Preview.Count == 1, "the initial add should settle first");

        vm.Find = "scan";
        vm.Replace = "fax";

        WaitFor(() => vm.Preview.Count == 1 && vm.Preview[0].NewName == "fax_001.pdf",
            "the preview should eventually reflect Find/Replace once the slow plan completes");
    }

    // ---- 3. a burst of keystrokes runs Plan once, not once per character --

    [Fact]
    public void TypingABurstRunsThePlanOnceNotPerKeystroke()
    {
        var calls = 0;
        var a = Touch("scan_001.pdf");
        var vm = new BulkRenameViewModel(
            scheduler: new CountingWorkScheduler(() => Interlocked.Increment(ref calls)));
        vm.AddFiles(new[] { a });
        WaitFor(() => vm.Preview.Count == 1, "the initial add should settle before the keystroke burst starts");
        var callsBeforeTyping = calls;

        // simulate typing "scan" character by character, faster than the
        // debounce window — exactly the keystroke burst finding 5.2 named
        var target = "scan";
        for (var i = 1; i <= target.Length; i++)
            vm.Find = target.Substring(0, i);

        WaitFor(() => vm.Preview.Count == 1 && vm.Preview[0].Changed,
            "the preview should eventually reflect the finished Find text");
        Thread.Sleep(350);   // no more keystrokes coming; let the debounce fully settle

        Assert.Equal(callsBeforeTyping + 1, calls);   // one Plan() call for the whole burst, not one per character
    }

    // ---- 4. a discrete toggle resolves immediately, pinning the Step 4
    // classification rather than assuming it -------------------------------

    [Fact]
    public void ADiscreteToggleResolvesWithoutWaitingTheFullDebounceWindow()
    {
        var a = Touch("A-B-C.pdf");
        // an artificially huge debounce window — if the discrete toggle
        // waited it out like a typed field, the WaitFor below (timeout well
        // under this) would fail
        var vm = new BulkRenameViewModel(probeDelayMs: 5000);
        vm.AddFiles(new[] { a });
        WaitFor(() => vm.Preview.Count == 1, "the initial add should settle before the timing measurement");

        vm.DeleteSeg2 = true;   // one of the five DeleteSeg* flags — a single click, not typed text

        WaitFor(() => vm.Preview[0].NewName == "A-C.pdf",
            "a discrete toggle should resolve promptly, not after the full debounce window",
            timeoutMs: 1000);
    }
}
