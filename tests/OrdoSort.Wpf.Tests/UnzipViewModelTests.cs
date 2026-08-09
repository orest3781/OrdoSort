using System.IO.Compression;
using OrdoSort.Core;
using OrdoSort.Wpf.ViewModels;

namespace OrdoSort.Wpf.Tests;

/// <summary>Task 6 (unzip tool). InlineWorkScheduler resolves every
/// _scheduler.Run call synchronously — same reasoning as
/// ZipMergeViewModelTests' own class doc, which this test class otherwise
/// mirrors closely since UnzipViewModel IS ZipMergeViewModel with the merge
/// step swapped for an extract step.</summary>
public class UnzipViewModelTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "ordounzipvm_" + Guid.NewGuid());

    public UnzipViewModelTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { /* best effort */ }
    }

    private string Touch(string name)
    {
        var path = Path.Combine(_dir, name);
        File.WriteAllText(path, "x");
        return path;
    }

    private static UnzipViewModel MakeVm(FakeDialogs dialogs,
        Func<string, Zipper.UnzipResult>? extractor = null) =>
        new(dialogs, new InlineWorkScheduler(), uiContext: null, extractor);

    [Fact]
    public async Task StatusesAndNotesAreAppliedPerRowAfterAnExtractRun()
    {
        var ok = Touch("ok.zip");
        var bad = Touch("bad.zip");
        var vm = MakeVm(new FakeDialogs(), path =>
            path == ok
                ? new Zipper.UnzipResult(path, "ok", Path.Combine(_dir, "ok"))
                : new Zipper.UnzipResult(path, "error", null, "not a valid zip"));

        await vm.AddFilesAsync(new[] { ok, bad });
        await vm.ExtractAsync();

        var okRow = Assert.Single(vm.Rows, r => r.Path == ok);
        Assert.Equal(UnzipRowStatus.Ok, okRow.StatusKind);
        Assert.Equal(Path.Combine(_dir, "ok"), okRow.OutputFolder);
        Assert.Contains("ok", okRow.Note);

        var badRow = Assert.Single(vm.Rows, r => r.Path == bad);
        Assert.Equal(UnzipRowStatus.Error, badRow.StatusKind);
        Assert.Equal("not a valid zip", badRow.Note);
        Assert.Null(badRow.OutputFolder);
    }

    [Fact]
    public async Task SummaryOmitsZeroPartsWhenEverythingExtracts()
    {
        var a = Touch("a.zip");
        var b = Touch("b.zip");
        var vm = MakeVm(new FakeDialogs(),
            path => new Zipper.UnzipResult(path, "ok", path + ".out"));

        await vm.AddFilesAsync(new[] { a, b });
        await vm.ExtractAsync();

        Assert.Equal("2 extracted", vm.Summary);
    }

    [Fact]
    public async Task SummaryOmitsZeroPartsWhenEverythingFails()
    {
        var a = Touch("a.zip");
        var vm = MakeVm(new FakeDialogs(),
            path => new Zipper.UnzipResult(path, "error", null, "nope"));

        await vm.AddFilesAsync(new[] { a });
        await vm.ExtractAsync();

        Assert.Equal("1 failed", vm.Summary);
    }

    [Fact]
    public async Task MixedResultsProduceASummaryWithBothClauses()
    {
        var ok = Touch("ok.zip");
        var bad = Touch("bad.zip");
        var vm = MakeVm(new FakeDialogs(), path =>
            path == ok
                ? new Zipper.UnzipResult(path, "ok", path + ".out")
                : new Zipper.UnzipResult(path, "error", null, "boom"));

        await vm.AddFilesAsync(new[] { ok, bad });
        await vm.ExtractAsync();

        Assert.Equal("1 extracted · 1 failed", vm.Summary);
    }

    [Fact]
    public async Task ExtractButtonTextReflectsRowCount()
    {
        var vm = MakeVm(new FakeDialogs());
        Assert.Equal("Extract", vm.ExtractButtonText);

        var a = Touch("a.zip");
        await vm.AddFilesAsync(new[] { a });
        Assert.Equal("Extract 1 zip", vm.ExtractButtonText);

        var b = Touch("b.zip");
        await vm.AddFilesAsync(new[] { b });
        Assert.Equal("Extract 2 zips", vm.ExtractButtonText);
    }

    [Fact]
    public async Task NonZipDropAddsANoteNotARow()
    {
        var txt = Touch("notes.txt");
        var vm = MakeVm(new FakeDialogs());

        await vm.AddFilesAsync(new[] { txt });

        Assert.Empty(vm.Rows);
        Assert.NotEqual("", vm.AddNote);
    }

    [Fact]
    public async Task DuplicateReAddSetsAddNoteWithoutAddingADuplicateRow()
    {
        var a = Touch("a.zip");
        var vm = MakeVm(new FakeDialogs());

        await vm.AddFilesAsync(new[] { a });
        Assert.Single(vm.Rows);
        Assert.Equal("", vm.AddNote);

        await vm.AddFilesAsync(new[] { a });   // same file again
        Assert.Single(vm.Rows);
        Assert.Contains("already listed", vm.AddNote);
    }

    [Fact]
    public async Task OnlyPendingRowsExtractOnASecondRun()
    {
        var a = Touch("a.zip");
        var b = Touch("b.zip");
        var calls = new List<string>();
        var vm = MakeVm(new FakeDialogs(), path =>
        {
            calls.Add(path);
            return new Zipper.UnzipResult(path, "ok", path + ".out");
        });

        await vm.AddFilesAsync(new[] { a, b });
        await vm.ExtractAsync();
        Assert.Equal(2, calls.Count);

        // a fresh Pending row joins two rows that already finished — only
        // the new one should extract on this second run
        var c = Touch("c.zip");
        await vm.AddFilesAsync(new[] { c });
        calls.Clear();
        await vm.ExtractAsync();

        Assert.Equal(new[] { c }, calls);
    }

    [Fact]
    public async Task CancelBetweenZipsStopsRowsNotYetStarted()
    {
        var a = Touch("a.zip");
        var b = Touch("b.zip");
        UnzipViewModel vm = null!;
        vm = MakeVm(new FakeDialogs(), path =>
        {
            // Deterministic stand-in for "the window closed mid-batch":
            // cancel from inside the scripted extractor for the FIRST zip,
            // so the row it belongs to still finishes (it already started)
            // but the loop must not begin a second zip afterward.
            if (path == a) vm.Cancel();
            return new Zipper.UnzipResult(path, "ok", path + ".out");
        });

        await vm.AddFilesAsync(new[] { a, b });
        await vm.ExtractAsync();

        var rowA = Assert.Single(vm.Rows, r => r.Path == a);
        var rowB = Assert.Single(vm.Rows, r => r.Path == b);
        Assert.Equal(UnzipRowStatus.Ok, rowA.StatusKind);        // started zip ran to completion
        Assert.Equal(UnzipRowStatus.Pending, rowB.StatusKind);   // never started
    }

    [Fact]
    public async Task ClearEmptiesRowsAndResetsSummaryAndAddNote()
    {
        var a = Touch("a.zip");
        var vm = MakeVm(new FakeDialogs(),
            path => new Zipper.UnzipResult(path, "ok", path + ".out"));
        await vm.AddFilesAsync(new[] { a });
        await vm.ExtractAsync();
        Assert.NotEqual("", vm.Summary);

        vm.ClearCommand.Execute(null);

        Assert.Empty(vm.Rows);
        Assert.Equal("", vm.Summary);
        Assert.Equal("", vm.AddNote);
        Assert.Equal("Extract", vm.ExtractButtonText);
    }

    [Fact]
    public async Task RemoveSelectedRemovesExactlyTheGivenRows()
    {
        var a = Touch("a.zip");
        var b = Touch("b.zip");
        var vm = MakeVm(new FakeDialogs());
        await vm.AddFilesAsync(new[] { a, b });
        Assert.Equal(2, vm.Rows.Count);

        var toRemove = vm.Rows.Where(r => r.Path == a).ToList();
        vm.RemoveSelected(toRemove);

        var remaining = Assert.Single(vm.Rows);
        Assert.Equal(b, remaining.Path);
    }

    [Fact]
    public async Task RealExtractorSmokeTestOnATempZip()
    {
        var zipPath = Path.Combine(_dir, "real.zip");
        using (var zip = ZipFile.Open(zipPath, ZipArchiveMode.Create))
        {
            using var es = zip.CreateEntry("a.txt").Open();
            var bytes = "hello"u8.ToArray();
            es.Write(bytes, 0, bytes.Length);
        }

        var vm = MakeVm(new FakeDialogs());   // default extractor: the real Zipper.Extract

        await vm.AddFilesAsync(new[] { zipPath });
        await vm.ExtractAsync();

        var row = Assert.Single(vm.Rows);
        Assert.Equal(UnzipRowStatus.Ok, row.StatusKind);
        var outDir = Path.Combine(_dir, "real");
        Assert.True(Directory.Exists(outDir));
        Assert.Equal(outDir, row.OutputFolder);
    }
}
