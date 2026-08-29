using OrdoSort.Core;
using OrdoSort.Wpf.Services;
using OrdoSort.Wpf.ViewModels;

namespace OrdoSort.Wpf.Tests;

/// <summary>QC-05: ZipListViewModel.Cancel() is reachable from exactly one
/// place in the app — ZipToolsWindow.OnClosed — and neither ClearCommand nor
/// RemoveSelected ever touched _cts, nor was gated on a busy flag. Clearing
/// the list mid-Extract/Merge left the batch running: it went on applying
/// results to rows the list no longer showed, then overwrote the "" Clear
/// had just set with a stale partial count once the loop noticed.
///
/// Exercised through ZipExtractViewModel — ZipListViewModel itself is
/// abstract — but nothing here is Extract-specific: MergePdfsViewModel
/// shares the exact same RunBatchAsync/ClearCommand/RemoveSelected code from
/// the base class, so a fix here is a fix for both tabs.
///
/// Every test drives the InlineWorkScheduler seam by calling BACK INTO the
/// view model from inside the scripted extractor — the same deterministic
/// stand-in ZipExtractViewModelTests.CancelBetweenZipsStopsRowsNotYetStarted
/// already uses for "the window closed mid-batch". InlineWorkScheduler runs
/// Scheduler.Run synchronously, so an embedded callback is the only way to
/// observe state truly mid-loop without a queued scheduler double.</summary>
public class ZipListClearAndRemoveTests
{
    private static ZipExtractViewModel MakeVm(Func<string, Zipper.UnzipResult>? extractor = null) =>
        new(new FakeDialogs(), Array.Empty<string>(), new InlineWorkScheduler(), uiContext: null,
            extractor: extractor is null ? null : (p, _, _) => extractor(p),
            zipProbe: (p, _) => new Zipper.ZipProbeResult(p, "not_encrypted"));

    [Fact]
    public async Task ClearDuringARunCancelsTheBatchAndTheWipedStatusIsNotRepopulated()
    {
        using var dir = new TempDir();
        var a = dir.File("a.zip");
        var b = dir.File("b.zip");
        var calls = new List<string>();
        ZipExtractViewModel vm = null!;
        vm = MakeVm(extractor: path =>
        {
            calls.Add(path);
            // Fired from inside the scripted extractor for the FIRST zip —
            // the row it belongs to still finishes (it already started),
            // but the loop must not go on to a second zip, and the wipe
            // Clear just did must not be overwritten once the loop notices
            // the cancellation.
            if (path == a) vm.ClearCommand.Execute(null);
            return new Zipper.UnzipResult(path, "ok", path + ".out");
        });

        await vm.AddPaths(new[] { a, b });
        await vm.ExtractAsync();

        Assert.Equal(new[] { a }, calls);   // b was never started
        Assert.Empty(vm.Rows);
        Assert.Equal("", vm.Status);        // the wipe, not a stale "1 extracted"
        Assert.True(vm.IsIdle);             // normal service resumes

        // The fresh-token half of QC-05: a run started AFTER a mid-batch
        // Clear must not be born cancelled.
        var c = dir.File("c.zip");
        calls.Clear();
        await vm.AddPaths(new[] { c });
        await vm.ExtractAsync();

        Assert.Equal(new[] { c }, calls);
        Assert.Contains("1 extracted", vm.Status);
    }

    [Fact]
    public async Task RemoveSelectedIsRefusedWhileABatchRunsSoTheQueuedRowStillExtracts()
    {
        using var dir = new TempDir();
        var a = dir.File("a.zip");
        var b = dir.File("b.zip");
        ZipExtractViewModel vm = null!;
        var attempted = false;
        vm = MakeVm(extractor: path =>
        {
            if (path == a)
            {
                Assert.False(vm.IsIdle, "IsIdle should be false — this is what disables Remove selected");
                var rowB = vm.Rows.Single(r => r.Path == b);
                vm.RemoveSelected(new[] { rowB });   // the click that slips through anyway
                attempted = true;
            }
            return new Zipper.UnzipResult(path, "ok", path + ".out");
        });

        await vm.AddPaths(new[] { a, b });
        await vm.ExtractAsync();

        Assert.True(attempted);
        var rowB = Assert.Single(vm.Rows, r => r.Path == b);
        Assert.Equal(ZipItemRowStatus.Ok, rowB.StatusKind);   // still listed AND still processed
        Assert.True(vm.IsIdle);
    }

    [Fact]
    public async Task ClearWithNothingRunningStillWorksAndLeavesTheListEmpty()
    {
        using var dir = new TempDir();
        var a = dir.File("a.zip");
        var vm = MakeVm(extractor: path => new Zipper.UnzipResult(path, "ok", path + ".out"));
        await vm.AddPaths(new[] { a });
        await vm.ExtractAsync();
        Assert.NotEqual("", vm.Status);

        vm.ClearCommand.Execute(null);

        Assert.Empty(vm.Rows);
        Assert.Equal("", vm.Status);
        Assert.True(vm.IsIdle);
    }
}
