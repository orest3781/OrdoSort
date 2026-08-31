using System.IO.Compression;
using OrdoSort.Core;
using OrdoSort.Wpf.Services;
using OrdoSort.Wpf.ViewModels;
using PdfSharp.Pdf;
using PdfSharp.Pdf.IO;
using ZipFile = System.IO.Compression.ZipFile;

namespace OrdoSort.Wpf.Tests;

/// <summary>The Merge PDFs window's view model: PDFs and zips in, one
/// document per source out. Every fact from the tab-era suite is kept
/// (ported onto the new seams) and the new shape is pinned on top: units,
/// fail-whole for the loose group, Merge to…, and the two probes.
///
/// InlineWorkScheduler resolves every Scheduler.Run call synchronously, so
/// MergeAsync can be awaited directly and asserted immediately after. Both
/// probes default to "not encrypted": the real ones on a TempDir's one-byte
/// files would call every row unreadable and leave nothing runnable.</summary>
public class MergePdfsViewModelTests
{
    private static MergePdfsViewModel MakeVm(
        IDialogService? dialogs = null,
        IReadOnlyList<string>? savedPasswords = null,
        Func<string, IReadOnlyList<string>, Func<PasswordRequest, string?>?, PdfMerge.MergeResult>? zipMerger = null,
        Func<IReadOnlyList<string>, string?, IReadOnlyList<string>, Func<PasswordRequest, string?>?, PdfMerge.MergeResult>? fileMerger = null,
        Func<string, IReadOnlyList<string>, Zipper.ZipProbeResult>? zipProbe = null,
        Func<string, IReadOnlyList<string>, Unlock.ProbeResult>? pdfProbe = null,
        IDocumentConverter? converter = null) =>
        new(dialogs ?? new FakeDialogs(), savedPasswords ?? Array.Empty<string>(), new InlineWorkScheduler(), uiContext: null,
            zipMerger, fileMerger,
            zipProbe ?? ((p, _) => new Zipper.ZipProbeResult(p, "not_encrypted")),
            pdfProbe ?? ((p, _) => new Unlock.ProbeResult("not_encrypted", p)),
            converter: converter);

    private static PdfMerge.MergeResult Ok(string source, string output, int pdfs) =>
        new(source, "ok", Output: output, PdfCount: pdfs);

    // ---- ported: zips, one unit each --------------------------------

    [Fact]
    public async Task StatusesAndNotesAreAppliedPerRowAfterAMergeRun()
    {
        using var dir = new TempDir();
        var ok = dir.File("ok.zip");
        var noPdfs = dir.File("nopdfs.zip");
        var bad = dir.File("bad.zip");
        var locked = dir.File("locked.zip");
        var vm = MakeVm(zipMerger: (path, _, _) =>
            path == ok ? Ok(path, Path.Combine(dir.Path, "ok.pdf"), 2)
            : path == noPdfs ? new PdfMerge.MergeResult(path, "no_pdfs", Message: "no PDFs inside")
            : path == locked ? new PdfMerge.MergeResult(path, "needs_password", Message: "'x.pdf' inside needs a password", Item: "x.pdf")
            : new PdfMerge.MergeResult(path, "error", Message: "couldn't read 'x.pdf': bad", Item: "x.pdf"));

        await vm.AddPaths(new[] { ok, noPdfs, bad, locked });
        await vm.MergeAsync(null);

        var okRow = Assert.Single(vm.Rows, r => r.Path == ok);
        Assert.Equal(ZipItemRowStatus.Ok, okRow.StatusKind);
        Assert.Equal(Path.Combine(dir.Path, "ok.pdf"), okRow.Output);
        Assert.Contains("ok.pdf", okRow.Note);
        Assert.Contains("2 documents", okRow.Note);

        var noPdfsRow = Assert.Single(vm.Rows, r => r.Path == noPdfs);
        Assert.Equal(ZipItemRowStatus.NoPdfs, noPdfsRow.StatusKind);
        Assert.Equal("no PDFs inside", noPdfsRow.Note);

        var badRow = Assert.Single(vm.Rows, r => r.Path == bad);
        Assert.Equal(ZipItemRowStatus.Error, badRow.StatusKind);
        Assert.Contains("x.pdf", badRow.Note);

        var lockedRow = Assert.Single(vm.Rows, r => r.Path == locked);
        Assert.Equal(ZipItemRowStatus.NeedsPassword, lockedRow.StatusKind);
        Assert.Equal("'x.pdf' inside needs a password", lockedRow.Note);
        Assert.True(lockedRow.IsRunnable);

        Assert.Equal("1 merged · 1 had nothing to merge · 1 needs a password · 1 failed", vm.Status);
    }

    [Fact]
    public async Task StatusOmitsZeroPartsWhenEverythingMerges()
    {
        using var dir = new TempDir();
        var vm = MakeVm(zipMerger: (path, _, _) => Ok(path, path + ".out.pdf", 1));
        await vm.AddPaths(new[] { dir.File("a.zip"), dir.File("b.zip") });
        await vm.MergeAsync(null);
        Assert.Equal("2 merged", vm.Status);
    }

    [Fact]
    public async Task StatusOmitsZeroPartsWhenEverythingFails()
    {
        using var dir = new TempDir();
        var vm = MakeVm(zipMerger: (path, _, _) => new PdfMerge.MergeResult(path, "error", Message: "nope"));
        await vm.AddPaths(new[] { dir.File("a.zip") });
        await vm.MergeAsync(null);
        Assert.Equal("1 failed", vm.Status);
    }

    [Fact]
    public async Task MergeButtonTextCountsRunnableRowsOfBothKinds()
    {
        using var dir = new TempDir();
        var vm = MakeVm();
        Assert.Equal("Merge", vm.MergeButtonText);
        Assert.False(vm.MergeCommand.CanExecute(null));

        await vm.AddPaths(new[] { dir.File("a.zip") });
        Assert.Equal("Merge 1 item", vm.MergeButtonText);

        await vm.AddPaths(new[] { dir.File("b.pdf") });
        Assert.Equal("Merge 2 items", vm.MergeButtonText);
        Assert.True(vm.MergeCommand.CanExecute(null));
    }

    /// <summary>Extensions widened to MergeTypes.AllExtensions (Task 7): a
    /// .txt file is now ACCEPTED (the Text group), so this no longer proves
    /// intake still refuses something — an .exe, which no MergeTypes group
    /// recognizes at all, replaces it as the genuinely-unsupported case.</summary>
    [Fact]
    public async Task AnUnsupportedFileIsRefusedWithANoteButAPdfIsTaken()
    {
        using var dir = new TempDir();
        var exe = dir.File("installer.exe");
        var pdf = dir.File("scan.pdf");
        var vm = MakeVm();

        await vm.AddPaths(new[] { exe, pdf });

        var row = Assert.Single(vm.Rows);
        Assert.Equal(pdf, row.Path);
        Assert.Equal("pdf", row.Kind);
        Assert.Contains("isn't a PDF, document, image or zip", vm.AddNote);
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

        await vm.AddPaths(new[] { a });
        Assert.Single(vm.Rows);
        Assert.Contains("already listed", vm.AddNote);
    }

    [Fact]
    public async Task ACaseOnlyDuplicateIsNotAddedTwice()
    {
        using var dir = new TempDir();
        var a = dir.File("a.zip");
        var shouty = Path.Combine(dir.Path, "A.zip");
        var vm = MakeVm();

        await vm.AddPaths(new[] { a, shouty });

        Assert.Single(vm.Rows);
        Assert.Contains("1 added", vm.AddNote);
        Assert.Contains("1 ignored", vm.AddNote);
    }

    [Fact]
    public async Task OnlyRunnableRowsMergeOnASecondRun()
    {
        using var dir = new TempDir();
        var a = dir.File("a.zip");
        var b = dir.File("b.zip");
        var calls = new List<string>();
        var vm = MakeVm(zipMerger: (path, _, _) =>
        {
            calls.Add(path);
            return Ok(path, path + ".out.pdf", 1);
        });

        await vm.AddPaths(new[] { a, b });
        await vm.MergeAsync(null);
        Assert.Equal(2, calls.Count);

        var c = dir.File("c.zip");
        await vm.AddPaths(new[] { c });
        calls.Clear();
        await vm.MergeAsync(null);

        Assert.Equal(new[] { c }, calls);
    }

    [Fact]
    public async Task ANeedsPasswordZipIsRunAgainByTheNextMerge()
    {
        using var dir = new TempDir();
        var a = dir.File("a.zip");
        var calls = 0;
        var vm = MakeVm(zipMerger: (path, _, _) => ++calls == 1
            ? new PdfMerge.MergeResult(path, "needs_password", Message: "needs a password", Item: "a.zip")
            : Ok(path, path + ".out.pdf", 1));

        await vm.AddPaths(new[] { a });
        await vm.MergeAsync(null);
        Assert.Equal(ZipItemRowStatus.NeedsPassword, vm.Rows.Single().StatusKind);
        Assert.Equal("Merge 1 item", vm.MergeButtonText);

        await vm.MergeAsync(null);
        Assert.Equal(ZipItemRowStatus.Ok, vm.Rows.Single().StatusKind);
        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task CancelBetweenUnitsStopsRowsNotYetStarted()
    {
        using var dir = new TempDir();
        var a = dir.File("a.zip");
        var b = dir.File("b.zip");
        MergePdfsViewModel vm = null!;
        vm = MakeVm(zipMerger: (path, _, _) =>
        {
            if (path == a) vm.Cancel();
            return Ok(path, path + ".out.pdf", 1);
        });

        await vm.AddPaths(new[] { a, b });
        await vm.MergeAsync(null);

        Assert.Equal(ZipItemRowStatus.Ok, vm.Rows.Single(r => r.Path == a).StatusKind);
        Assert.Equal(ZipItemRowStatus.Pending, vm.Rows.Single(r => r.Path == b).StatusKind);
    }

    [Fact]
    public async Task ClearEmptiesRowsAndResetsStatusAndAddNote()
    {
        using var dir = new TempDir();
        var vm = MakeVm(zipMerger: (path, _, _) => Ok(path, path + ".out.pdf", 1));
        await vm.AddPaths(new[] { dir.File("a.zip") });
        await vm.MergeAsync(null);
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
        var b = dir.File("b.pdf");
        var vm = MakeVm();
        await vm.AddPaths(new[] { a, b });

        vm.RemoveSelected(vm.Rows.Where(r => r.Path == a).ToList());

        Assert.Equal(b, Assert.Single(vm.Rows).Path);
    }

    [Fact]
    public async Task RealZipMergerSmokeTestOnATwoPdfZip()
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
        var vm = MakeVm();   // default zipMerger: the real PdfMerge.MergeZip

        await vm.AddPaths(new[] { zipPath });
        await vm.MergeAsync(null);

        var row = Assert.Single(vm.Rows);
        Assert.Equal(ZipItemRowStatus.Ok, row.StatusKind);
        Assert.Equal(Path.Combine(dir.Path, "real.pdf"), row.Output);
    }

    // ---- the loose group ----------------------------------------------

    private static string WritePdf(string path, int pages = 1)
    {
        using var doc = new PdfDocument();
        for (var i = 0; i < pages; i++) doc.AddPage();
        doc.Save(path);
        return path;
    }

    [Fact]
    public async Task LoosePdfsAreOneUnitAndTheResultLandsOnEveryRow()
    {
        using var dir = new TempDir();
        var a = dir.File("a.pdf");
        var b = dir.File("b.pdf");
        var calls = new List<IReadOnlyList<string>>();
        var vm = MakeVm(fileMerger: (paths, output, _, _) =>
        {
            calls.Add(paths.ToList());
            return Ok(paths[0], Path.Combine(dir.Path, "Job.pdf"), paths.Count);
        });

        await vm.AddPaths(new[] { a, b });
        await vm.MergeAsync(null);

        var one = Assert.Single(calls);
        Assert.Equal(new[] { a, b }, one);
        Assert.All(vm.Rows, r =>
        {
            Assert.Equal(ZipItemRowStatus.Ok, r.StatusKind);
            Assert.Equal("→ Job.pdf (2 documents)", r.Note);
            Assert.Equal(Path.Combine(dir.Path, "Job.pdf"), r.Output);
        });
        Assert.Equal("1 merged", vm.Status);
        Assert.Equal("Merge", vm.MergeButtonText);
    }

    [Fact]
    public async Task ZipsRunFirstAndTheLooseGroupLast()
    {
        using var dir = new TempDir();
        var pdf = dir.File("a.pdf");
        var zip = dir.File("b.zip");
        var order = new List<string>();
        var vm = MakeVm(
            zipMerger: (path, _, _) => { order.Add("zip"); return Ok(path, path + ".out.pdf", 1); },
            fileMerger: (paths, _, _, _) => { order.Add("group"); return Ok(paths[0], Path.Combine(dir.Path, "Job.pdf"), 1); });

        await vm.AddPaths(new[] { pdf, zip });   // the PDF is listed FIRST
        await vm.MergeAsync(null);

        Assert.Equal(new[] { "zip", "group" }, order);
    }

    /// <summary>Fail-whole for the loose group: the culprit takes the
    /// result — runnable NeedsPassword — and every other row is held back
    /// with a note naming it, still Pending, so the next Merge picks them all
    /// up once the culprit is opened or removed.</summary>
    [Fact]
    public async Task AFailedGroupMarksTheCulpritAndHoldsTheOthersBack()
    {
        using var dir = new TempDir();
        var cover = dir.File("cover.pdf");
        var report = dir.File("report.pdf");
        var locked = dir.File("locked.pdf");
        var vm = MakeVm(fileMerger: (paths, _, _, _) =>
            new PdfMerge.MergeResult(paths[0], "needs_password", Message: "needs a password", Item: locked));

        await vm.AddPaths(new[] { cover, report, locked });
        await vm.MergeAsync(null);

        var lockedRow = vm.Rows.Single(r => r.Path == locked);
        Assert.Equal(ZipItemRowStatus.NeedsPassword, lockedRow.StatusKind);
        Assert.Equal("needs a password", lockedRow.Note);
        foreach (var held in vm.Rows.Where(r => r.Path != locked))
        {
            Assert.Equal(ZipItemRowStatus.Pending, held.StatusKind);
            Assert.Equal("not merged — locked.pdf needs a password", held.Note);
        }
        Assert.Equal("Merge 3 items", vm.MergeButtonText);
        Assert.Equal("1 needs a password", vm.Status);
    }

    [Fact]
    public async Task AnUnreadableCulpritIsAnErrorAndTheOthersSayCouldntBeRead()
    {
        using var dir = new TempDir();
        var good = dir.File("good.pdf");
        var junk = dir.File("junk.pdf");
        var vm = MakeVm(fileMerger: (paths, _, _, _) =>
            new PdfMerge.MergeResult(paths[0], "error", Message: "couldn't read it: not a PDF", Item: junk));

        await vm.AddPaths(new[] { good, junk });
        await vm.MergeAsync(null);

        Assert.Equal(ZipItemRowStatus.Error, vm.Rows.Single(r => r.Path == junk).StatusKind);
        var held = vm.Rows.Single(r => r.Path == good);
        Assert.Equal(ZipItemRowStatus.Pending, held.StatusKind);
        Assert.Equal("not merged — junk.pdf couldn't be read", held.Note);
        Assert.Equal("Merge 1 item", vm.MergeButtonText);   // the Error row is finished; the held one is not
    }

    [Fact]
    public async Task AGroupFailureWithNoCulpritLeavesEveryRowPendingWithTheMessage()
    {
        using var dir = new TempDir();
        var vm = MakeVm(fileMerger: (paths, _, _, _) =>
            new PdfMerge.MergeResult(paths[0], "error", Message: "couldn't save the merged PDF: disk full"));

        await vm.AddPaths(new[] { dir.File("a.pdf"), dir.File("b.pdf") });
        await vm.MergeAsync(null);

        Assert.All(vm.Rows, r =>
        {
            Assert.Equal(ZipItemRowStatus.Pending, r.StatusKind);
            Assert.Equal("couldn't save the merged PDF: disk full", r.Note);
        });
    }

    /// <summary>Typed during a zip, remembered for the group: zips run first,
    /// so a password typed for one serves the loose PDFs after it without a
    /// second prompt.</summary>
    [Fact]
    public async Task APasswordTypedForAZipIsACandidateForTheLooseGroup()
    {
        using var dir = new TempDir();
        var zip = dir.File("a.zip");
        var pdf = dir.File("b.pdf");
        var dialogs = new FakeDialogs();
        dialogs.PasswordAnswers.Enqueue("typed");
        IReadOnlyList<string>? groupCandidates = null;
        var vm = MakeVm(dialogs: dialogs,
            zipMerger: (path, _, ask) => ask!(new PasswordRequest("a.zip", null, false)) == "typed"
                ? Ok(path, path + ".out.pdf", 1)
                : new PdfMerge.MergeResult(path, "needs_password", Message: "needs a password", Item: "a.zip"),
            fileMerger: (paths, _, candidates, _) =>
            {
                groupCandidates = candidates.ToList();
                return Ok(paths[0], Path.Combine(dir.Path, "Job.pdf"), 1);
            });

        await vm.AddPaths(new[] { zip, pdf });
        await vm.MergeAsync(null);

        Assert.Equal(new[] { "typed" }, groupCandidates);
        Assert.Single(dialogs.PasswordRequests);
    }

    /// <summary>Typed for one PDF, remembered for the next in the SAME unit.
    /// The loose group is one Core call over many documents, so the answer
    /// has to reach the candidate list Core is still enumerating — the
    /// spec's own motivating case is seven PDFs locked with one password,
    /// and a per-unit snapshot would ask seven times.</summary>
    [Fact]
    public async Task APasswordTypedForOneLoosePdfServesTheNextInTheSameGroup()
    {
        using var dir = new TempDir();
        var first = dir.File("a.pdf");
        var second = dir.File("b.pdf");
        var dialogs = new FakeDialogs();
        dialogs.PasswordAnswers.Enqueue("typed");   // ONE answer for TWO locked PDFs
        var vm = MakeVm(dialogs: dialogs, fileMerger: (paths, _, candidates, ask) =>
        {
            foreach (var path in paths)
            {
                // Re-read per document, exactly as PdfMerge.MergeFilesCore
                // hands the same list to AddPdf for every path.
                if (candidates.Contains("typed")) continue;
                if (ask!(new PasswordRequest(Path.GetFileName(path), null, false)) != "typed")
                    return new PdfMerge.MergeResult(paths[0], "needs_password",
                        Message: "needs a password", Item: path);
            }
            return Ok(paths[0], Path.Combine(dir.Path, "Job.pdf"), paths.Count);
        });

        await vm.AddPaths(new[] { first, second });
        await vm.MergeAsync(null);

        Assert.Single(dialogs.PasswordRequests);
        Assert.All(vm.Rows, r => Assert.Equal(ZipItemRowStatus.Ok, r.StatusKind));
    }

    // ---- Merge to… ----------------------------------------------------

    [Fact]
    public async Task MergeToIsEnabledOnlyWhileARunnableLoosePdfIsListed()
    {
        using var dir = new TempDir();
        var vm = MakeVm(
            zipMerger: (path, _, _) => Ok(path, path + ".out.pdf", 1),
            fileMerger: (paths, _, _, _) => Ok(paths[0], Path.Combine(dir.Path, "Job.pdf"), 1));

        await vm.AddPaths(new[] { dir.File("a.zip") });
        Assert.False(vm.MergeToCommand.CanExecute(null));

        await vm.AddPaths(new[] { dir.File("b.pdf") });
        Assert.True(vm.MergeToCommand.CanExecute(null));

        await vm.MergeAsync(null);
        Assert.False(vm.MergeToCommand.CanExecute(null));   // merged — nothing loose left to send anywhere
    }

    [Fact]
    public async Task MergeToPassesTheChosenPathToTheLooseGroupOnly()
    {
        using var dir = new TempDir();
        var zip = dir.File("a.zip");
        var pdf = dir.File("b.pdf");
        var chosen = Path.Combine(dir.Path, "chosen.pdf");
        string? seenOutput = "not called";
        var vm = MakeVm(dialogs: new FakeDialogs { NextSaveFile = chosen },
            zipMerger: (path, _, _) => Ok(path, path + ".out.pdf", 1),
            fileMerger: (paths, output, _, _) => { seenOutput = output; return Ok(paths[0], output!, 1); });

        await vm.AddPaths(new[] { zip, pdf });
        await vm.MergeToAsync();

        Assert.Equal(chosen, seenOutput);
        Assert.Equal(chosen, vm.Rows.Single(r => r.Path == pdf).Output);
        Assert.Equal(zip + ".out.pdf", vm.Rows.Single(r => r.Path == zip).Output);
    }

    [Fact]
    public async Task MergeToCancelledIsASilentNoOp()
    {
        using var dir = new TempDir();
        var calls = 0;
        var vm = MakeVm(dialogs: new FakeDialogs { NextSaveFile = null },
            fileMerger: (paths, _, _, _) => { calls++; return Ok(paths[0], "irrelevant.pdf", 1); });
        await vm.AddPaths(new[] { dir.File("a.pdf") });

        await vm.MergeToAsync();

        Assert.Equal(0, calls);
        Assert.Equal("", vm.Status);
    }

    // ---- the probes on add ---------------------------------------------

    [Fact]
    public async Task EachKindGetsItsOwnProbeAndTheVerdictLandsOnTheRow()
    {
        using var dir = new TempDir();
        var zip = dir.File("a.zip");
        var pdf = dir.File("b.pdf");
        var vm = MakeVm(
            zipProbe: (p, _) => new Zipper.ZipProbeResult(p, "needs_password"),
            pdfProbe: (p, _) => new Unlock.ProbeResult("ready", p, MatchedIndex: 0));

        await vm.AddPaths(new[] { zip, pdf });

        var zipRow = vm.Rows.Single(r => r.Path == zip);
        Assert.Equal(ZipItemRowStatus.NeedsPassword, zipRow.StatusKind);
        Assert.Equal("needs a password", zipRow.Note);
        var pdfRow = vm.Rows.Single(r => r.Path == pdf);
        Assert.Equal(ZipItemRowStatus.Pending, pdfRow.StatusKind);
        Assert.Equal("a saved password opens this", pdfRow.Note);
        Assert.Equal("Merge 2 items", vm.MergeButtonText);
    }

    [Fact]
    public async Task APdfInUseStaysPendingWithANote()
    {
        using var dir = new TempDir();
        var vm = MakeVm(pdfProbe: (p, _) => new Unlock.ProbeResult("in_use", p, Message: "It's open in another program"));

        await vm.AddPaths(new[] { dir.File("b.pdf") });

        var row = Assert.Single(vm.Rows);
        Assert.Equal(ZipItemRowStatus.Pending, row.StatusKind);
        Assert.Equal("open in another program", row.Note);
    }

    /// <summary>The real probe and the real merger on real documents — the
    /// difference between this file's scripts and the feature.</summary>
    [Fact]
    public async Task RealFileMergerSmokeTestOnTwoLoosePdfs()
    {
        using var dir = new TempDir();
        var a = WritePdf(Path.Combine(dir.Path, "a.pdf"), 2);
        var b = WritePdf(Path.Combine(dir.Path, "b.pdf"), 3);
        var vm = new MergePdfsViewModel(new FakeDialogs(), Array.Empty<string>(), new InlineWorkScheduler());

        await vm.AddPaths(new[] { a, b });
        Assert.All(vm.Rows, r => Assert.Equal(ZipItemRowStatus.Pending, r.StatusKind));   // the real probe: not encrypted
        await vm.MergeAsync(null);

        var expected = Path.Combine(dir.Path, Path.GetFileName(dir.Path) + ".pdf");
        Assert.All(vm.Rows, r => Assert.Equal(ZipItemRowStatus.Ok, r.StatusKind));
        Assert.True(File.Exists(expected));
        using var merged = PdfReader.Open(expected, PdfDocumentOpenMode.Import);
        Assert.Equal(5, merged.PageCount);
    }

    /// <summary>The two windows' lists never interact — that separation is
    /// the whole reason Merge PDFs has its own window rather than being a
    /// third button beside Extract.</summary>
    [Fact]
    public async Task ItsListIsIndependentOfTheZipAndUnzipWindow()
    {
        using var dir = new TempDir();
        var zip = dir.File("a.zip");
        var merge = MakeVm();
        var zipExtract = new ZipExtractViewModel(new FakeDialogs(), Array.Empty<string>(), new InlineWorkScheduler(),
            extractor: (p, _, _) => new Zipper.UnzipResult(p, "ok", Path.Combine(dir.Path, "a")),
            zipProbe: (p, _) => new Zipper.ZipProbeResult(p, "not_encrypted"));

        await merge.AddPaths(new[] { zip });
        await zipExtract.AddPaths(new[] { zip });
        await zipExtract.ExtractAsync();

        Assert.Equal(ZipItemRowStatus.Pending, merge.Rows.Single().StatusKind);
        Assert.True(merge.MergeCommand.CanExecute(null));
    }

    // ---- Task 8: the IsPdf sweep, Notes, and disposal -------------------

    /// <summary>Always claims every extension — used where a fact needs a
    /// row to stay Pending/runnable regardless of what is actually installed
    /// on the machine running the test (real Word/Excel/PowerPoint
    /// availability must never decide whether a fact passes).</summary>
    private sealed class AlwaysHandlesConverter : IDocumentConverter
    {
        public bool Handles(string extension) => true;
        public ConversionResult ToPdf(byte[] source, string displayName,
            IReadOnlyList<string> candidates, Func<PasswordRequest, string?>? ask) =>
            new("ok", new byte[] { 1 });
    }

    private sealed class DisposableConverter : IDocumentConverter, IDisposable
    {
        public bool Disposed { get; private set; }
        public bool Handles(string extension) => false;
        public ConversionResult ToPdf(byte[] source, string displayName,
            IReadOnlyList<string> candidates, Func<PasswordRequest, string?>? ask) =>
            new("unsupported", null);
        public void Dispose() => Disposed = true;
    }

    /// <summary>The button's own count, CanExecute, and a REAL run — the
    /// real default converter and the real PdfMerge.MergeFiles, no fakes —
    /// must all agree for a list holding only a document that needs
    /// conversion. This is the exact defect the IsPdf sweep exists to fix:
    /// before it, a lone .docx (a .txt here, so this fact needs nothing
    /// installed) showed an enabled "Merge 1 item" that did nothing on
    /// click, because MergeAsync's own loose-unit selection filtered on
    /// IsPdf and never picked it up — revert that sweep and this fact fails,
    /// because the row stays Pending instead of Ok.</summary>
    [Fact]
    public async Task AListOfOnlyANonPdfDocumentReportsAndMergesTheSameCount()
    {
        // 2026-08-31: Text is off by default (the owner's conservative
        // default), so it has to be switched on for a lone .txt to be
        // included at all — otherwise the row would stay excluded and
        // Pending, and this fact's whole premise (an enabled "Merge 1 item"
        // that actually does something) would never be reached.
        using var dir = new TempDir();
        var textPath = dir.File("notes.txt");   // TempDir.File writes non-empty content
        var vm = new MergePdfsViewModel(new FakeDialogs(), Array.Empty<string>(), new InlineWorkScheduler());
        vm.SetTypeEnabled(MergeTypes.Text, true);

        await vm.AddPaths(new[] { textPath });

        Assert.Equal("Merge 1 item", vm.MergeButtonText);
        Assert.True(vm.MergeCommand.CanExecute(null));

        await vm.MergeAsync(null);

        var row = Assert.Single(vm.Rows);
        Assert.Equal(ZipItemRowStatus.Ok, row.StatusKind);
        Assert.True(File.Exists(row.Output));
        Assert.Equal("Merge", vm.MergeButtonText);
    }

    /// <summary>"Merge to…" — Task 8 widens it past literal loose PDFs, the
    /// same widening MergeAsync's own unit selection gets: a document that
    /// needs converting is just as much a candidate for a chosen Save-As
    /// name as a loose PDF always was.</summary>
    [Fact]
    public async Task MergeToIncludesNonPdfDocumentsToo()
    {
        using var dir = new TempDir();
        var docx = dir.File("report.docx");
        var chosen = Path.Combine(dir.Path, "chosen.pdf");
        string? seenOutput = "not called";
        var vm = MakeVm(dialogs: new FakeDialogs { NextSaveFile = chosen },
            converter: new AlwaysHandlesConverter(),
            fileMerger: (paths, output, _, _) => { seenOutput = output; return Ok(paths[0], output!, paths.Count); });
        // Word is off by default -- without this the docx row is excluded,
        // MergeToCommand.CanExecute is false (RunnableLooseDocuments is 0),
        // and the very next assertion fails outright.
        vm.SetTypeEnabled(MergeTypes.Word, true);

        await vm.AddPaths(new[] { docx });
        Assert.True(vm.MergeToCommand.CanExecute(null));

        await vm.MergeToAsync();

        Assert.Equal(chosen, seenOutput);
    }

    /// <summary>MergeResult.Notes — a hard requirement, not polish (task
    /// brief): "only the first of 3 worksheets" and similar advisories built
    /// by Tasks 3/4/6 must reach the row, or that whole channel is
    /// invisible. Appended to the row's existing verdict text, on the SAME
    /// run that also reports the ordinary "→ file (N PDFs)" success note —
    /// revert the Apply change and this fact fails because the note goes
    /// back to being just the bare verdict.</summary>
    [Fact]
    public async Task NotesFromAResultAreAppendedToTheRowsNote()
    {
        using var dir = new TempDir();
        var vm = MakeVm(fileMerger: (paths, _, _, _) =>
            new PdfMerge.MergeResult(paths[0], "ok", Output: Path.Combine(dir.Path, "Job.pdf"), PdfCount: 1,
                Notes: new[] { "only the first of 3 worksheets — install Excel to include them all" }));
        // Excel is off by default -- without this the xlsx row is excluded,
        // never selected into a unit, the fake fileMerger above is never
        // called at all, and the row stays Pending rather than reaching Ok.
        vm.SetTypeEnabled(MergeTypes.Excel, true);

        await vm.AddPaths(new[] { dir.File("a.xlsx") });
        await vm.MergeAsync(null);

        var row = Assert.Single(vm.Rows);
        Assert.Equal(ZipItemRowStatus.Ok, row.StatusKind);
        Assert.Contains("→ Job.pdf", row.Note);
        Assert.Contains("only the first of 3 worksheets", row.Note);
    }

    /// <summary>A "no_pdfs" zip can still carry Notes (PdfMerge.MergeZipCore
    /// names what it found but couldn't convert even when nothing ended up
    /// mergeable) — proving Notes surfaces on a non-"ok" status too, not
    /// only the success path.</summary>
    [Fact]
    public async Task NotesSurfaceEvenWhenTheUnitDidNotEndInOk()
    {
        using var dir = new TempDir();
        var vm = MakeVm(zipMerger: (path, _, _) =>
            new PdfMerge.MergeResult(path, "no_pdfs", Message: "nothing to merge inside",
                Notes: new[] { "report.docx: Word isn't installed, so this can't be converted" }));

        await vm.AddPaths(new[] { dir.File("a.zip") });
        await vm.MergeAsync(null);

        var row = Assert.Single(vm.Rows);
        Assert.Equal(ZipItemRowStatus.NoPdfs, row.StatusKind);
        Assert.Contains("nothing to merge inside", row.Note);
        Assert.Contains("report.docx", row.Note);
    }

    /// <summary>MergePdfsViewModel.Dispose reaches whatever converter it was
    /// built with, generically, through IDisposable — the mechanism
    /// MergePdfsWindow.OnClosed relies on to tear down (or restore) an
    /// Office session without knowing the converter is a ConverterChain
    /// wrapping an OfficeConverter at all.</summary>
    [Fact]
    public void DisposingTheViewModelDisposesAnInjectedDisposableConverter()
    {
        var converter = new DisposableConverter();
        var vm = new MergePdfsViewModel(new FakeDialogs(), Array.Empty<string>(), new InlineWorkScheduler(),
            converter: converter);

        vm.Dispose();

        Assert.True(converter.Disposed);
    }

    // ---- Fix round 1: the warning drain, and the .ppt probe wording -----

    /// <summary>Reports whatever warnings it already holds, on demand — no
    /// Dispose() required, unlike the real OfficeConverter. That is the
    /// whole point: it proves MergePdfsViewModel's OWN wiring (drain after a
    /// run, dedupe, fold into Status) independently of exactly when the real
    /// converter happens to populate its list.</summary>
    private sealed class WarningReportingConverter : IDocumentConverter, IReportsRestorationWarnings
    {
        private readonly List<string> _warnings = new();
        public IReadOnlyList<string> RestorationWarnings => _warnings;
        public void AddWarning(string warning) => _warnings.Add(warning);
        public bool Handles(string extension) => true;
        public ConversionResult ToPdf(byte[] source, string displayName,
            IReadOnlyList<string> candidates, Func<PasswordRequest, string?>? ask) =>
            new("ok", new byte[] { 1 });
    }

    /// <summary>The review's Critical fix, pinned directly: a converter that
    /// reports a restoration warning must have it appear in Status AFTER a
    /// merge run, with the window (here, just the view model — nothing about
    /// this fact needs a real Window at all) still open. The previous design
    /// only ever folded RestorationWarnings into Status from inside
    /// Dispose(), which MergePdfsWindow.OnClosed calls after the window has
    /// already closed — unreachable in every case. This fact never calls
    /// vm.Dispose() at all, which is exactly what proves the warning was
    /// shown some other way.</summary>
    [Fact]
    public async Task RestorationWarningsFromAMergeRunAppearInStatusWhileTheWindowIsStillOpen()
    {
        using var dir = new TempDir();
        var converter = new WarningReportingConverter();
        // "DisplayAlerts", not "Visible" -- OfficeConverter never writes
        // Visible at all any more (review follow-up, 2026-08-31), so a
        // restoration warning it actually emits can only ever be about
        // DisplayAlerts or AutomationSecurity.
        converter.AddWarning("Word: couldn't restore DisplayAlerts (RPC server unavailable)");
        var vm = MakeVm(converter: converter,
            fileMerger: (paths, output, _, _) => Ok(paths[0], Path.Combine(dir.Path, "Job.pdf"), paths.Count));
        // Word is off by default -- without this the docx row is excluded,
        // MergeAsync builds zero units, and RunBatchAsync returns before
        // ever touching Status (DrainConverterWarnings still runs
        // unconditionally afterward and would append the warning to an
        // untouched "" regardless) -- this fact's own docstring, "AFTER a
        // merge run", would then be proven by a run that never happened.
        vm.SetTypeEnabled(MergeTypes.Word, true);

        await vm.AddPaths(new[] { dir.File("a.docx") });
        await vm.MergeAsync(null);

        Assert.Contains("couldn't restore DisplayAlerts", vm.Status);
    }

    /// <summary>The other half of the same fix: the converter's warnings
    /// list is append-only, so a SECOND run must not repeat what the first
    /// run already showed — only genuinely new entries get folded in.</summary>
    [Fact]
    public async Task ASecondMergeRunOnlyReportsWarningsNotAlreadyReported()
    {
        using var dir = new TempDir();
        var converter = new WarningReportingConverter();
        converter.AddWarning("Word: first warning");
        var vm = MakeVm(converter: converter,
            fileMerger: (paths, output, _, _) => Ok(paths[0], Path.Combine(dir.Path, "Job.pdf"), paths.Count));
        // Word is off by default -- see the sibling fact above for why this
        // matters here too. Without it, BOTH runs below build zero units:
        // RunBatchAsync's own tally-overwrite of Status (the thing that
        // resets it between runs) never executes on that early-return path,
        // so a real bug survived here on the FIRST attempt at this fix —
        // "first warning" persisted into the second run's Status instead of
        // being cleared, and the DoesNotContain assertion below failed for
        // real (Status was "first warning · second warning", not just
        // "second warning") until Word was switched on.
        vm.SetTypeEnabled(MergeTypes.Word, true);
        await vm.AddPaths(new[] { dir.File("a.docx") });
        await vm.MergeAsync(null);
        Assert.Contains("first warning", vm.Status);

        converter.AddWarning("Excel: second warning");
        await vm.AddPaths(new[] { dir.File("b.docx") });
        await vm.MergeAsync(null);

        Assert.Contains("second warning", vm.Status);
        // "first warning" was folded in once, by the FIRST drain, and must
        // not have been repeated by the second. The prior form of this
        // assertion (IndexOf == LastIndexOf) was non-discriminating: it
        // holds whether "first warning" appears once OR ZERO times, so it
        // would pass just as happily if the text were missing entirely.
        // RunBatchAsync's own tally overwrites Status at the start of every
        // run, so the true, correct outcome here IS zero occurrences —
        // DoesNotContain says that directly.
        Assert.DoesNotContain("first warning", vm.Status);
    }

    /// <summary>Review Important 1: OfficeConverter.Handles("ppt") is false
    /// even when PowerPoint IS installed (its own documented exception — no
    /// safe password path exists for the legacy binary format). Before this
    /// fix, the probe's generic wording would have told someone with
    /// PowerPoint installed that PowerPoint isn't installed — a false
    /// statement about their own machine, in red, at drop time. Gated the
    /// same way OfficeConverterTests gates its own Office-dependent facts
    /// (PowerPointInstalled, computed once via Type.GetTypeFromProgID,
    /// touching no COM): there is nothing to prove on a machine where
    /// PowerPoint genuinely isn't installed, since the whole point is
    /// proving the wording is right when the app truly is there.</summary>
    [Fact]
    public async Task APptFileWithPowerPointInstalledIsRefusedWithItsOwnReasonNotAFalseNotInstalledClaim()
    {
        if (!OfficeConverterTests.PowerPointInstalled) return;

        using var dir = new TempDir();
        var vm = new MergePdfsViewModel(new FakeDialogs(), Array.Empty<string>(), new InlineWorkScheduler());
        // PowerPoint is off by default -- without this the probe never
        // runs at all (Probe() skips an excluded row on purpose), so the
        // row would read "not included" rather than exercising the
        // ppt-specific wording this fact exists to prove.
        vm.SetTypeEnabled(MergeTypes.PowerPoint, true);

        await vm.AddPaths(new[] { dir.File("deck.ppt") });

        var row = Assert.Single(vm.Rows);
        Assert.Equal(ZipItemRowStatus.Error, row.StatusKind);
        Assert.DoesNotContain("isn't installed", row.Note);
        Assert.Contains("save it as .pptx", row.Note);
    }
}
