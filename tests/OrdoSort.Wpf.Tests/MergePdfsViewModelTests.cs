using System.IO.Compression;
using OrdoSort.Core;
using OrdoSort.Wpf.Services;
using OrdoSort.Wpf.ViewModels;
using PdfSharp.Pdf;

namespace OrdoSort.Wpf.Tests;

/// <summary>Task 3 (zip tools window). Ports every fact from
/// ZipMergeViewModelTests onto the Merge PDFs tab's own MergePdfsViewModel
/// (see task-3-brief.md's rename table), including the non-zip-rejection
/// fact — unlike the sibling Zip &amp; unzip tab, this one takes archives
/// only, so "that isn't a zip" is still the honest answer here — plus one
/// new fact pinning that this tab's list is independent of the other tab's.
///
/// InlineWorkScheduler resolves every Scheduler.Run call synchronously, same
/// reasoning as the ported suite's own class doc, so MergeAsync can be
/// awaited directly and asserted immediately after — no polling needed.</summary>
public class MergePdfsViewModelTests
{
    private static MergePdfsViewModel MakeVm(
        Func<string, ZipMerge.MergeResult>? merger = null) =>
        new(new InlineWorkScheduler(), uiContext: null, merger);

    [Fact]
    public async Task StatusesAndNotesAreAppliedPerRowAfterAMergeRun()
    {
        using var dir = new TempDir();
        var ok = dir.File("ok.zip");
        var noPdfs = dir.File("nopdfs.zip");
        var bad = dir.File("bad.zip");
        var vm = MakeVm(merger: path =>
            path == ok ? new ZipMerge.MergeResult(path, "ok", Output: Path.Combine(dir.Path, "ok.pdf"), PdfCount: 2)
            : path == noPdfs ? new ZipMerge.MergeResult(path, "no_pdfs", Message: "no PDFs inside")
            : new ZipMerge.MergeResult(path, "error", Message: "couldn't read 'x.pdf': bad"));

        await vm.AddPaths(new[] { ok, noPdfs, bad });
        await vm.MergeAsync();

        var okRow = Assert.Single(vm.Rows, r => r.Path == ok);
        Assert.Equal(ZipItemRowStatus.Ok, okRow.StatusKind);
        Assert.Equal(Path.Combine(dir.Path, "ok.pdf"), okRow.Output);
        Assert.Contains("ok.pdf", okRow.Note);
        Assert.Contains("2 PDFs", okRow.Note);

        var noPdfsRow = Assert.Single(vm.Rows, r => r.Path == noPdfs);
        Assert.Equal(ZipItemRowStatus.NoPdfs, noPdfsRow.StatusKind);
        Assert.Equal("no PDFs inside", noPdfsRow.Note);
        Assert.Null(noPdfsRow.Output);

        var badRow = Assert.Single(vm.Rows, r => r.Path == bad);
        Assert.Equal(ZipItemRowStatus.Error, badRow.StatusKind);
        Assert.Contains("x.pdf", badRow.Note);
    }

    [Fact]
    public async Task MixedResultsProduceAStatusWithAllThreeClauses()
    {
        using var dir = new TempDir();
        var ok1 = dir.File("ok1.zip");
        var noPdfs1 = dir.File("nopdfs1.zip");
        var bad1 = dir.File("bad1.zip");
        var vm = MakeVm(merger: path =>
            path == ok1 ? new ZipMerge.MergeResult(path, "ok", Output: path + ".out.pdf", PdfCount: 1)
            : path == noPdfs1 ? new ZipMerge.MergeResult(path, "no_pdfs", Message: "no PDFs inside")
            : new ZipMerge.MergeResult(path, "error", Message: "boom"));

        await vm.AddPaths(new[] { ok1, noPdfs1, bad1 });
        await vm.MergeAsync();

        Assert.Equal("1 merged · 1 had no PDFs · 1 failed", vm.Status);
    }

    [Fact]
    public async Task StatusOmitsZeroPartsWhenEverythingMerges()
    {
        using var dir = new TempDir();
        var a = dir.File("a.zip");
        var b = dir.File("b.zip");
        var vm = MakeVm(merger:
            path => new ZipMerge.MergeResult(path, "ok", Output: path + ".out.pdf", PdfCount: 1));

        await vm.AddPaths(new[] { a, b });
        await vm.MergeAsync();

        Assert.Equal("2 merged", vm.Status);
    }

    [Fact]
    public async Task StatusOmitsZeroPartsWhenEverythingFails()
    {
        using var dir = new TempDir();
        var a = dir.File("a.zip");
        var vm = MakeVm(merger: path => new ZipMerge.MergeResult(path, "error", Message: "nope"));

        await vm.AddPaths(new[] { a });
        await vm.MergeAsync();

        Assert.Equal("1 failed", vm.Status);
    }

    [Fact]
    public async Task MergeButtonTextReflectsRowCount()
    {
        using var dir = new TempDir();
        var vm = MakeVm();
        Assert.Equal("Merge", vm.MergeButtonText);

        var a = dir.File("a.zip");
        await vm.AddPaths(new[] { a });
        Assert.Equal("Merge 1 zip", vm.MergeButtonText);

        var b = dir.File("b.zip");
        await vm.AddPaths(new[] { b });
        Assert.Equal("Merge 2 zips", vm.MergeButtonText);
    }

    [Fact]
    public async Task NonZipDropAddsANoteNotARow()
    {
        using var dir = new TempDir();
        var txt = dir.File("notes.txt");
        var vm = MakeVm();

        await vm.AddPaths(new[] { txt });

        Assert.Empty(vm.Rows);
        Assert.NotEqual("", vm.AddNote);
    }

    [Fact]
    public async Task DuplicateReAddSetsAddNoteWithoutAddingADuplicateRow()
    {
        using var dir = new TempDir();
        var a = dir.File("a.zip");
        var vm = MakeVm();

        await vm.AddPaths(new[] { a });
        Assert.Single(vm.Rows);
        Assert.Equal("", vm.AddNote);

        await vm.AddPaths(new[] { a });   // same file again
        Assert.Single(vm.Rows);
        Assert.Contains("already listed", vm.AddNote);
    }

    /// <summary>Windows resolves a path case-insensitively, so "a.zip" and
    /// "A.zip" are the same file on disk — File.Exists says yes to both.
    /// AddPaths's dedupe runs through Intake.Add (Core), which canonicalizes
    /// each path before comparing — this pins that the second spelling is
    /// turned away as "already listed" instead of landing as a second row
    /// over the same bytes.</summary>
    [Fact]
    public async Task ACaseOnlyDuplicateIsNotAddedTwice()
    {
        using var dir = new TempDir();
        var a = dir.File("a.zip");
        var shouty = Path.Combine(dir.Path, "A.zip");   // same file, different spelling
        var vm = MakeVm();

        await vm.AddPaths(new[] { a, shouty });

        Assert.Single(vm.Rows);
        Assert.Contains("1 added", vm.AddNote);
        Assert.Contains("1 ignored", vm.AddNote);
    }

    [Fact]
    public async Task OnlyPendingRowsMergeOnASecondRun()
    {
        using var dir = new TempDir();
        var a = dir.File("a.zip");
        var b = dir.File("b.zip");
        var calls = new List<string>();
        var vm = MakeVm(merger: path =>
        {
            calls.Add(path);
            return new ZipMerge.MergeResult(path, "ok", Output: path + ".out.pdf", PdfCount: 1);
        });

        await vm.AddPaths(new[] { a, b });
        await vm.MergeAsync();
        Assert.Equal(2, calls.Count);

        // a fresh Pending row joins two rows that already finished — only
        // the new one should merge on this second run
        var c = dir.File("c.zip");
        await vm.AddPaths(new[] { c });
        calls.Clear();
        await vm.MergeAsync();

        Assert.Equal(new[] { c }, calls);
    }

    [Fact]
    public async Task CancelBetweenZipsStopsRowsNotYetStarted()
    {
        using var dir = new TempDir();
        var a = dir.File("a.zip");
        var b = dir.File("b.zip");
        MergePdfsViewModel vm = null!;
        vm = MakeVm(merger: path =>
        {
            // Deterministic stand-in for "the window closed mid-batch":
            // cancel from inside the scripted merger for the FIRST zip, so
            // the row it belongs to still finishes (it already started) but
            // the loop must not begin a second zip afterward.
            if (path == a) vm.Cancel();
            return new ZipMerge.MergeResult(path, "ok", Output: path + ".out.pdf", PdfCount: 1);
        });

        await vm.AddPaths(new[] { a, b });
        await vm.MergeAsync();

        var rowA = Assert.Single(vm.Rows, r => r.Path == a);
        var rowB = Assert.Single(vm.Rows, r => r.Path == b);
        Assert.Equal(ZipItemRowStatus.Ok, rowA.StatusKind);        // started zip ran to completion
        Assert.Equal(ZipItemRowStatus.Pending, rowB.StatusKind);   // never started
    }

    [Fact]
    public async Task ClearEmptiesRowsAndResetsStatusAndAddNote()
    {
        using var dir = new TempDir();
        var a = dir.File("a.zip");
        var vm = MakeVm(merger: path => new ZipMerge.MergeResult(path, "ok", Output: path + ".out.pdf", PdfCount: 1));
        await vm.AddPaths(new[] { a });
        await vm.MergeAsync();
        Assert.NotEqual("", vm.Status);

        vm.ClearCommand.Execute(null);

        Assert.Empty(vm.Rows);
        Assert.Equal("", vm.Status);
        Assert.Equal("", vm.AddNote);
        Assert.Equal("Merge", vm.MergeButtonText);
    }

    [Fact]
    public async Task RemoveSelectedRemovesExactlyTheGivenRows()
    {
        using var dir = new TempDir();
        var a = dir.File("a.zip");
        var b = dir.File("b.zip");
        var vm = MakeVm();
        await vm.AddPaths(new[] { a, b });
        Assert.Equal(2, vm.Rows.Count);

        var toRemove = vm.Rows.Where(r => r.Path == a).ToList();
        vm.RemoveSelected(toRemove);

        var remaining = Assert.Single(vm.Rows);
        Assert.Equal(b, remaining.Path);
    }

    [Fact]
    public async Task RealMergerSmokeTestOnATwoPdfZip()
    {
        using var dir = new TempDir();
        var zipPath = Path.Combine(dir.Path, "real.zip");
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

        var vm = MakeVm();   // default merger: the real ZipMerge.MergeZip

        await vm.AddPaths(new[] { zipPath });
        await vm.MergeAsync();

        var row = Assert.Single(vm.Rows);
        Assert.Equal(ZipItemRowStatus.Ok, row.StatusKind);
        Assert.True(File.Exists(Path.Combine(dir.Path, "real.pdf")));
        Assert.Equal(Path.Combine(dir.Path, "real.pdf"), row.Output);
    }

    // ---- new fact: the tabs' lists never interact ---------------------

    /// <summary>The two tabs' lists never interact — that separation is the
    /// whole reason Merge PDFs has its own tab rather than being a third
    /// button beside Extract.</summary>
    [Fact]
    public async Task ItsListIsIndependentOfTheZipAndUnzipTab()
    {
        using var dir = new TempDir();
        var zip = dir.File("a.zip");
        var merge = MakeVm();
        var zipExtract = new ZipExtractViewModel(new FakeDialogs(), new InlineWorkScheduler(),
            extractor: p => new Zipper.UnzipResult(p, "ok", Path.Combine(dir.Path, "a")));

        await merge.AddPaths(new[] { zip });
        await zipExtract.AddPaths(new[] { zip });
        await zipExtract.ExtractAsync();

        Assert.Equal(ZipItemRowStatus.Pending, merge.Rows.Single().StatusKind);
        Assert.True(merge.MergeCommand.CanExecute(null));
    }
}
