using OrdoSort.Wpf.ViewModels;
using static OrdoSort.Core.BulkRename;

namespace OrdoSort.Wpf.Tests;

/// <summary>Bulk rename's preview plan (a File.Exists per file, more per
/// collision) must not run inside a typed keystroke: a batch on an SMB
/// destination would pay a network round trip per file per character. The
/// plan runs once the typing pauses, once per burst. Every test owns the
/// clock, so none of this depends on how busy the machine is.</summary>
public class BulkRenameProbeTests : IDisposable
{
    private static readonly TimeSpan PastTheDebounce = TimeSpan.FromSeconds(1);

    private readonly TempDir _dir = new();

    public void Dispose() => _dir.Dispose();

    /// <summary>A view model whose plan runs on this thread, counted, on a
    /// clock the test moves; the file is added and ticked (every control
    /// changes only ticked files) and its first preview is built.</summary>
    private (BulkRenameViewModel Vm, ManualTimeProvider Time, Func<int> PlanCalls) Build(string fileName,
        int probeDelayMs = 300)
    {
        var calls = 0;
        var time = new ManualTimeProvider();
        var vm = new BulkRenameViewModel(
            plan: (paths, op, overrides, dropped, ticked) => { calls++; return Plan(paths, op, overrides, dropped, ticked); },
            scheduler: new InlineWorkScheduler(), probeDelayMs: probeDelayMs, time: time);
        var file = _dir.File(fileName);
        InlineWorkScheduler.Finished(vm.AddFilesAsync(new[] { file }));
        vm.SelectedSources = new[] { file };
        time.Advance(TimeSpan.FromMilliseconds(probeDelayMs) + PastTheDebounce);
        Assert.Single(vm.Preview);
        return (vm, time, () => calls);
    }

    [Fact]
    public void SettingFindLeavesThePlanUntilTheTypingPauses()
    {
        var (vm, time, planCalls) = Build("scan_001.pdf");
        var before = planCalls();

        vm.Find = "scan";
        Assert.Equal(before, planCalls());

        time.Advance(PastTheDebounce);
        Assert.Equal(before + 1, planCalls());
    }

    [Fact]
    public void ThePreviewReflectsFindAndReplaceOnceTheTypingPauses()
    {
        var (vm, time, _) = Build("scan_001.pdf");

        vm.Find = "scan";
        vm.Replace = "fax";
        time.Advance(PastTheDebounce);

        Assert.Equal("fax_001.pdf", Assert.Single(vm.Preview).NewName);
    }

    [Fact]
    public void TypingABurstRunsThePlanOnceNotPerKeystroke()
    {
        var (vm, time, planCalls) = Build("scan_001.pdf");
        var before = planCalls();

        // typing "scan", 100 ms between keystrokes: inside the 300 ms debounce
        var target = "scan";
        for (var i = 1; i <= target.Length; i++)
        {
            vm.Find = target.Substring(0, i);
            time.Advance(TimeSpan.FromMilliseconds(100));
        }
        Assert.Equal(before, planCalls());

        time.Advance(PastTheDebounce);   // the pause
        Assert.Equal(before + 1, planCalls());
        Assert.True(Assert.Single(vm.Preview).Changed);
    }

    [Fact]
    public void ADiscreteToggleResolvesWithoutWaitingForTheDebounce()
    {
        // A huge debounce window: a chip click that waited it out like typed
        // text would leave the preview unchanged below.
        var (vm, _, _) = Build("A-B-C.pdf", probeDelayMs: 60_000);

        vm.SetSegmentKept(2, kept: false);   // a segment chip click, not typed text

        Assert.Equal("A-C.pdf", Assert.Single(vm.Preview).NewName);
    }
}
