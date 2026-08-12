using System.IO.Compression;
using OrdoSort.Core;
using OrdoSort.Wpf.ViewModels;
using PdfSharp.Pdf;

namespace OrdoSort.Wpf.Tests;

/// <summary>Task 5 (merge PDFs from zip tool). InlineWorkScheduler resolves
/// every _scheduler.Run call synchronously on the calling thread — same
/// reasoning as PageCountsViewModelTests' own class doc — so these tests can
/// call MergeAsync directly (the same internal-method pattern
/// ToolViewModelTests uses for UnlockViewModel.UnlockAsync) and assert
/// immediately after, no polling needed.</summary>
public class ZipMergeViewModelTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "ordozipmergevm_" + Guid.NewGuid());

    public ZipMergeViewModelTests() => Directory.CreateDirectory(_dir);

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

    private static ZipMergeViewModel MakeVm(FakeDialogs dialogs,
        Func<string, ZipMerge.MergeResult>? merger = null) =>
        new(dialogs, new InlineWorkScheduler(), uiContext: null, merger);

    [Fact]
    public async Task StatusesAndNotesAreAppliedPerRowAfterAMergeRun()
    {
        var ok = Touch("ok.zip");
        var noPdfs = Touch("nopdfs.zip");
        var bad = Touch("bad.zip");
        var vm = MakeVm(new FakeDialogs(), path =>
            path == ok ? new ZipMerge.MergeResult(path, "ok", Output: Path.Combine(_dir, "ok.pdf"), PdfCount: 2)
            : path == noPdfs ? new ZipMerge.MergeResult(path, "no_pdfs", Message: "no PDFs inside")
            : new ZipMerge.MergeResult(path, "error", Message: "couldn't read 'x.pdf': bad"));

        await vm.AddFilesAsync(new[] { ok, noPdfs, bad });
        await vm.MergeAsync();

        var okRow = Assert.Single(vm.Rows, r => r.Path == ok);
        Assert.Equal(ZipRowStatus.Ok, okRow.StatusKind);
        Assert.Equal(Path.Combine(_dir, "ok.pdf"), okRow.Output);
        Assert.Contains("ok.pdf", okRow.Note);
        Assert.Contains("2 PDFs", okRow.Note);

        var noPdfsRow = Assert.Single(vm.Rows, r => r.Path == noPdfs);
        Assert.Equal(ZipRowStatus.NoPdfs, noPdfsRow.StatusKind);
        Assert.Equal("no PDFs inside", noPdfsRow.Note);
        Assert.Null(noPdfsRow.Output);

        var badRow = Assert.Single(vm.Rows, r => r.Path == bad);
        Assert.Equal(ZipRowStatus.Error, badRow.StatusKind);
        Assert.Contains("x.pdf", badRow.Note);
    }

    [Fact]
    public async Task MixedResultsProduceASummaryWithAllThreeClauses()
    {
        var ok1 = Touch("ok1.zip");
        var noPdfs1 = Touch("nopdfs1.zip");
        var bad1 = Touch("bad1.zip");
        var vm = MakeVm(new FakeDialogs(), path =>
            path == ok1 ? new ZipMerge.MergeResult(path, "ok", Output: path + ".out.pdf", PdfCount: 1)
            : path == noPdfs1 ? new ZipMerge.MergeResult(path, "no_pdfs", Message: "no PDFs inside")
            : new ZipMerge.MergeResult(path, "error", Message: "boom"));

        await vm.AddFilesAsync(new[] { ok1, noPdfs1, bad1 });
        await vm.MergeAsync();

        Assert.Equal("1 merged · 1 had no PDFs · 1 failed", vm.Summary);
    }

    [Fact]
    public async Task SummaryOmitsZeroPartsWhenEverythingMerges()
    {
        var a = Touch("a.zip");
        var b = Touch("b.zip");
        var vm = MakeVm(new FakeDialogs(),
            path => new ZipMerge.MergeResult(path, "ok", Output: path + ".out.pdf", PdfCount: 1));

        await vm.AddFilesAsync(new[] { a, b });
        await vm.MergeAsync();

        Assert.Equal("2 merged", vm.Summary);
    }

    [Fact]
    public async Task SummaryOmitsZeroPartsWhenEverythingFails()
    {
        var a = Touch("a.zip");
        var vm = MakeVm(new FakeDialogs(), path => new ZipMerge.MergeResult(path, "error", Message: "nope"));

        await vm.AddFilesAsync(new[] { a });
        await vm.MergeAsync();

        Assert.Equal("1 failed", vm.Summary);
    }

    [Fact]
    public async Task MergeButtonTextReflectsRowCount()
    {
        var vm = MakeVm(new FakeDialogs());
        Assert.Equal("Merge", vm.MergeButtonText);

        var a = Touch("a.zip");
        await vm.AddFilesAsync(new[] { a });
        Assert.Equal("Merge 1 zip", vm.MergeButtonText);

        var b = Touch("b.zip");
        await vm.AddFilesAsync(new[] { b });
        Assert.Equal("Merge 2 zips", vm.MergeButtonText);
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

    /// <summary>Windows resolves a path case-insensitively, so "a.zip" and
    /// "A.zip" are the same file on disk — File.Exists says yes to both.
    /// AddFilesAsync's dedupe now runs through Intake.Add (Core), which
    /// canonicalizes each path before comparing — this pins that the second
    /// spelling is turned away as "already listed" instead of landing as a
    /// second row over the same bytes.</summary>
    [Fact]
    public async Task ACaseOnlyDuplicateIsNotAddedTwice()
    {
        var a = Touch("a.zip");
        var shouty = Path.Combine(_dir, "A.zip");   // same file, different spelling
        var vm = MakeVm(new FakeDialogs());

        await vm.AddFilesAsync(new[] { a, shouty });

        Assert.Single(vm.Rows);
        Assert.Contains("1 added", vm.AddNote);
        Assert.Contains("1 ignored", vm.AddNote);
    }

    [Fact]
    public async Task OnlyPendingRowsMergeOnASecondRun()
    {
        var a = Touch("a.zip");
        var b = Touch("b.zip");
        var calls = new List<string>();
        var vm = MakeVm(new FakeDialogs(), path =>
        {
            calls.Add(path);
            return new ZipMerge.MergeResult(path, "ok", Output: path + ".out.pdf", PdfCount: 1);
        });

        await vm.AddFilesAsync(new[] { a, b });
        await vm.MergeAsync();
        Assert.Equal(2, calls.Count);

        // a fresh Pending row joins two rows that already finished — only
        // the new one should merge on this second run
        var c = Touch("c.zip");
        await vm.AddFilesAsync(new[] { c });
        calls.Clear();
        await vm.MergeAsync();

        Assert.Equal(new[] { c }, calls);
    }

    [Fact]
    public async Task CancelBetweenZipsStopsRowsNotYetStarted()
    {
        var a = Touch("a.zip");
        var b = Touch("b.zip");
        ZipMergeViewModel vm = null!;
        vm = MakeVm(new FakeDialogs(), path =>
        {
            // Deterministic stand-in for "the window closed mid-batch":
            // cancel from inside the scripted merger for the FIRST zip, so
            // the row it belongs to still finishes (it already started) but
            // the loop must not begin a second zip afterward.
            if (path == a) vm.Cancel();
            return new ZipMerge.MergeResult(path, "ok", Output: path + ".out.pdf", PdfCount: 1);
        });

        await vm.AddFilesAsync(new[] { a, b });
        await vm.MergeAsync();

        var rowA = Assert.Single(vm.Rows, r => r.Path == a);
        var rowB = Assert.Single(vm.Rows, r => r.Path == b);
        Assert.Equal(ZipRowStatus.Ok, rowA.StatusKind);        // started zip ran to completion
        Assert.Equal(ZipRowStatus.Pending, rowB.StatusKind);   // never started
    }

    [Fact]
    public async Task ClearEmptiesRowsAndResetsSummaryAndAddNote()
    {
        var a = Touch("a.zip");
        var vm = MakeVm(new FakeDialogs(),
            path => new ZipMerge.MergeResult(path, "ok", Output: path + ".out.pdf", PdfCount: 1));
        await vm.AddFilesAsync(new[] { a });
        await vm.MergeAsync();
        Assert.NotEqual("", vm.Summary);

        vm.ClearCommand.Execute(null);

        Assert.Empty(vm.Rows);
        Assert.Equal("", vm.Summary);
        Assert.Equal("", vm.AddNote);
        Assert.Equal("Merge", vm.MergeButtonText);
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
    public async Task RealMergerSmokeTestOnATwoPdfZip()
    {
        var zipPath = Path.Combine(_dir, "real.zip");
        using (var zip = ZipFile.Open(zipPath, ZipArchiveMode.Create))
        {
            void AddPdf(string entryName)
            {
                using var doc = new PdfDocument();
                doc.AddPage();
                using var ms = new MemoryStream();
                doc.Save(ms, closeStream: false);
                ms.Position = 0;
                using var es = zip.CreateEntry(entryName).Open();
                ms.CopyTo(es);
            }
            AddPdf("a.pdf");
            AddPdf("b.pdf");
        }

        var vm = MakeVm(new FakeDialogs());   // default merger: the real ZipMerge.MergeZip

        await vm.AddFilesAsync(new[] { zipPath });
        await vm.MergeAsync();

        var row = Assert.Single(vm.Rows);
        Assert.Equal(ZipRowStatus.Ok, row.StatusKind);
        Assert.True(File.Exists(Path.Combine(_dir, "real.pdf")));
        Assert.Equal(Path.Combine(_dir, "real.pdf"), row.Output);
    }
}
