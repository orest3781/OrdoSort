using System.IO.Compression;
using OrdoSort.Core;
using OrdoSort.Wpf.Services;
using OrdoSort.Wpf.ViewModels;

namespace OrdoSort.Wpf.Tests;

/// <summary>Task 2 (zip tools window). Ports every fact from
/// ZipViewModelTests and UnzipViewModelTests onto the merged
/// ZipExtractViewModel (see task-2-brief.md's rename table), except
/// UnzipViewModelTests' non-zip-rejection fact — this tab accepts loose
/// files by design — plus four new facts pinning behaviour neither of those
/// suites had to cover: Zip and Extract now read their own scope off the
/// SAME list instead of each owning a separate one.
///
/// InlineWorkScheduler resolves every Scheduler.Run call synchronously, same
/// reasoning as both ported suites' own class docs, so
/// ZipAsync/ZipWithDialogAsync/ExtractAsync can be awaited directly and
/// asserted immediately after — no polling needed.</summary>
public class ZipExtractViewModelTests
{
    private static ZipExtractViewModel MakeVm(
        IDialogService? dialogs = null,
        Func<IReadOnlyList<string>, string?, Zipper.ZipResult>? zipper = null,
        Func<string, Zipper.UnzipResult>? extractor = null) =>
        new(dialogs ?? new FakeDialogs(), new InlineWorkScheduler(), uiContext: null, zipper, extractor);

    // ---- ported from ZipViewModelTests --------------------------------

    [Fact]
    public async Task ZipCommandCallsTheZipperWithANullOutputPath()
    {
        using var dir = new TempDir();
        var a = dir.File("a.txt");
        string? seenOutput = "not called";
        var vm = MakeVm(zipper: (paths, output) =>
        {
            seenOutput = output;
            return new Zipper.ZipResult("ok", Path.Combine(dir.Path, "a.zip"));
        });
        await vm.AddPaths(new[] { a });

        await vm.ZipAsync(null);

        Assert.Null(seenOutput);
    }

    [Fact]
    public async Task ZipCommandAppliesTheOkStatusWordingWithTheItemCount()
    {
        using var dir = new TempDir();
        var a = dir.File("a.txt");
        var b = dir.File("b.txt");
        var vm = MakeVm(zipper: (paths, output) => new Zipper.ZipResult("ok", Path.Combine(dir.Path, "made.zip")));
        await vm.AddPaths(new[] { a, b });

        await vm.ZipAsync(null);

        Assert.Contains("made.zip", vm.Status);
        Assert.Contains("2 items", vm.Status);
    }

    [Fact]
    public async Task ZipCommandAppliesTheErrorMessageVerbatimOnFailure()
    {
        using var dir = new TempDir();
        var a = dir.File("a.txt");
        var vm = MakeVm(zipper: (paths, output) => new Zipper.ZipResult("error", null, "nothing to zip"));
        await vm.AddPaths(new[] { a });

        await vm.ZipAsync(null);

        Assert.Equal("nothing to zip", vm.Status);
    }

    [Fact]
    public async Task ZipAsCommandPassesTheChosenPathToTheZipper()
    {
        using var dir = new TempDir();
        var a = dir.File("a.txt");
        var chosen = Path.Combine(dir.Path, "chosen.zip");
        string? seenOutput = null;
        var calls = 0;
        var vm = MakeVm(dialogs: new FakeDialogs { NextSaveFile = chosen }, zipper: (paths, output) =>
        {
            calls++;
            seenOutput = output;
            return new Zipper.ZipResult("ok", chosen);
        });
        await vm.AddPaths(new[] { a });

        await vm.ZipWithDialogAsync();

        Assert.Equal(1, calls);
        Assert.Equal(chosen, seenOutput);
    }

    [Fact]
    public async Task ZipAsCommandSkipsTheZipperWhenTheDialogIsCancelled()
    {
        using var dir = new TempDir();
        var a = dir.File("a.txt");
        var calls = 0;
        var vm = MakeVm(dialogs: new FakeDialogs { NextSaveFile = null }, zipper: (paths, output) =>
        {
            calls++;
            return new Zipper.ZipResult("ok", "irrelevant.zip");
        });
        await vm.AddPaths(new[] { a });

        await vm.ZipWithDialogAsync();

        Assert.Equal(0, calls);
        Assert.Equal("", vm.Status);
    }

    [Fact]
    public async Task AddPathsDedupesDropsMissingPathsAndSetsAddNote()
    {
        using var dir = new TempDir();
        var a = dir.File("a.txt");
        var ghost = Path.Combine(dir.Path, "gone.txt");
        var vm = MakeVm();

        await vm.AddPaths(new[] { a, ghost });
        Assert.Single(vm.Rows);
        Assert.Equal(a, vm.Rows[0].Path);
        Assert.NotEqual("", vm.AddNote);

        await vm.AddPaths(new[] { a });   // same file again
        Assert.Single(vm.Rows);
        Assert.Contains("already listed", vm.AddNote);
    }

    /// <summary>Windows resolves a path case-insensitively, so "a.txt" and
    /// "A.txt" are the same file on disk — File.Exists says yes to both. The
    /// base class's AddPaths dedupe runs through Intake.Add (Core), which
    /// canonicalizes each path before comparing — this pins that the second
    /// spelling is turned away as "already listed" instead of landing as a
    /// second row over the same bytes.</summary>
    [Fact]
    public async Task ACaseOnlyDuplicateOfAFileIsNotAddedTwice()
    {
        using var dir = new TempDir();
        var a = dir.File("a.txt");
        var shouty = Path.Combine(dir.Path, "A.txt");   // same file, different spelling
        var vm = MakeVm();

        await vm.AddPaths(new[] { a, shouty });

        Assert.Single(vm.Rows);
        Assert.Contains("1 added", vm.AddNote);
        Assert.Contains("1 ignored", vm.AddNote);
    }

    [Fact]
    public async Task AddPathsRecordsFileAndFolderKindSeparately()
    {
        using var dir = new TempDir();
        var file = dir.File("a.txt");
        var folder = dir.Dir("sub");
        var vm = MakeVm();

        await vm.AddPaths(new[] { file, folder });

        var fileRow = Assert.Single(vm.Rows, r => r.Path == file);
        var folderRow = Assert.Single(vm.Rows, r => r.Path == folder);
        Assert.Equal("file", fileRow.Kind);
        Assert.Equal("folder", folderRow.Kind);
    }

    [Fact]
    public async Task ZipButtonTextReflectsRowCount()
    {
        using var dir = new TempDir();
        var vm = MakeVm();
        Assert.Equal("Zip", vm.ZipButtonText);

        var a = dir.File("a.txt");
        await vm.AddPaths(new[] { a });
        Assert.Equal("Zip 1 item", vm.ZipButtonText);

        var b = dir.File("b.txt");
        await vm.AddPaths(new[] { b });
        Assert.Equal("Zip 2 items", vm.ZipButtonText);
    }

    [Fact]
    public async Task ClearEmptiesRowsAndResetsStatusAndAddNoteAfterAZip()
    {
        using var dir = new TempDir();
        var a = dir.File("a.txt");
        var vm = MakeVm(zipper: (paths, output) => new Zipper.ZipResult("ok", Path.Combine(dir.Path, "a.zip")));
        await vm.AddPaths(new[] { a });
        await vm.ZipAsync(null);
        Assert.NotEqual("", vm.Status);

        vm.ClearCommand.Execute(null);

        Assert.Empty(vm.Rows);
        Assert.Equal("", vm.Status);
        Assert.Equal("", vm.AddNote);
        Assert.Equal("Zip", vm.ZipButtonText);
    }

    [Fact]
    public async Task RemoveSelectedRemovesExactlyTheGivenRows()
    {
        using var dir = new TempDir();
        var a = dir.File("a.txt");
        var b = dir.File("b.txt");
        var vm = MakeVm();
        await vm.AddPaths(new[] { a, b });
        Assert.Equal(2, vm.Rows.Count);

        var toRemove = vm.Rows.Where(r => r.Path == a).ToList();
        vm.RemoveSelected(toRemove);

        var remaining = Assert.Single(vm.Rows);
        Assert.Equal(b, remaining.Path);
    }

    [Fact]
    public async Task RealZipperSmokeTestOnTwoTempFiles()
    {
        using var dir = new TempDir();
        var a = dir.File("a.txt");
        var b = dir.File("b.txt");
        var vm = MakeVm();   // default zipper: the real Zipper.CreateZip

        await vm.AddPaths(new[] { a, b });
        await vm.ZipAsync(null);

        Assert.Contains("Created", vm.Status);
        // default name for two loose files = the parent folder's own name
        // (dir.Path's own name) — see Zipper.DefaultName's own doc comment.
        var expected = Path.Combine(dir.Path, Path.GetFileName(dir.Path) + ".zip");
        Assert.True(File.Exists(expected));
        using var zip = ZipFile.OpenRead(expected);
        Assert.Equal(2, zip.Entries.Count);
    }

    // ---- ported from UnzipViewModelTests ------------------------------
    // (NonZipDropAddsANoteNotARow is NOT ported — this tab accepts loose
    // files by design; task-2-brief.md ruling 2.)

    [Fact]
    public async Task StatusesAndNotesAreAppliedPerRowAfterAnExtractRun()
    {
        using var dir = new TempDir();
        var ok = dir.File("ok.zip");
        var bad = dir.File("bad.zip");
        var vm = MakeVm(extractor: path =>
            path == ok
                ? new Zipper.UnzipResult(path, "ok", Path.Combine(dir.Path, "ok"))
                : new Zipper.UnzipResult(path, "error", null, "not a valid zip"));

        await vm.AddPaths(new[] { ok, bad });
        await vm.ExtractAsync();

        var okRow = Assert.Single(vm.Rows, r => r.Path == ok);
        Assert.Equal(ZipItemRowStatus.Ok, okRow.StatusKind);
        Assert.Equal(Path.Combine(dir.Path, "ok"), okRow.Output);
        Assert.Contains("ok", okRow.Note);

        var badRow = Assert.Single(vm.Rows, r => r.Path == bad);
        Assert.Equal(ZipItemRowStatus.Error, badRow.StatusKind);
        Assert.Equal("not a valid zip", badRow.Note);
        Assert.Null(badRow.Output);
    }

    [Fact]
    public async Task StatusOmitsZeroPartsWhenEverythingExtracts()
    {
        using var dir = new TempDir();
        var a = dir.File("a.zip");
        var b = dir.File("b.zip");
        var vm = MakeVm(extractor: path => new Zipper.UnzipResult(path, "ok", path + ".out"));

        await vm.AddPaths(new[] { a, b });
        await vm.ExtractAsync();

        Assert.Equal("2 extracted", vm.Status);
    }

    [Fact]
    public async Task StatusOmitsZeroPartsWhenEverythingFails()
    {
        using var dir = new TempDir();
        var a = dir.File("a.zip");
        var vm = MakeVm(extractor: path => new Zipper.UnzipResult(path, "error", null, "nope"));

        await vm.AddPaths(new[] { a });
        await vm.ExtractAsync();

        Assert.Equal("1 failed", vm.Status);
    }

    [Fact]
    public async Task MixedExtractResultsProduceAStatusWithBothClauses()
    {
        using var dir = new TempDir();
        var ok = dir.File("ok.zip");
        var bad = dir.File("bad.zip");
        var vm = MakeVm(extractor: path =>
            path == ok
                ? new Zipper.UnzipResult(path, "ok", path + ".out")
                : new Zipper.UnzipResult(path, "error", null, "boom"));

        await vm.AddPaths(new[] { ok, bad });
        await vm.ExtractAsync();

        Assert.Equal("1 extracted · 1 failed", vm.Status);
    }

    [Fact]
    public async Task ExtractButtonTextReflectsRowCount()
    {
        using var dir = new TempDir();
        var vm = MakeVm();
        Assert.Equal("Extract", vm.ExtractButtonText);

        var a = dir.File("a.zip");
        await vm.AddPaths(new[] { a });
        Assert.Equal("Extract 1 zip", vm.ExtractButtonText);

        var b = dir.File("b.zip");
        await vm.AddPaths(new[] { b });
        Assert.Equal("Extract 2 zips", vm.ExtractButtonText);
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
    /// "A.zip" are the same file on disk — File.Exists says yes to both. The
    /// base class's AddPaths dedupe runs through Intake.Add (Core), which
    /// canonicalizes each path before comparing — this pins that the second
    /// spelling is turned away as "already listed" instead of landing as a
    /// second row over the same bytes.</summary>
    [Fact]
    public async Task ACaseOnlyDuplicateOfAZipIsNotAddedTwice()
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
    public async Task OnlyPendingRowsExtractOnASecondRun()
    {
        using var dir = new TempDir();
        var a = dir.File("a.zip");
        var b = dir.File("b.zip");
        var calls = new List<string>();
        var vm = MakeVm(extractor: path =>
        {
            calls.Add(path);
            return new Zipper.UnzipResult(path, "ok", path + ".out");
        });

        await vm.AddPaths(new[] { a, b });
        await vm.ExtractAsync();
        Assert.Equal(2, calls.Count);

        // a fresh Pending row joins two rows that already finished — only
        // the new one should extract on this second run
        var c = dir.File("c.zip");
        await vm.AddPaths(new[] { c });
        calls.Clear();
        await vm.ExtractAsync();

        Assert.Equal(new[] { c }, calls);
    }

    [Fact]
    public async Task CancelBetweenZipsStopsRowsNotYetStarted()
    {
        using var dir = new TempDir();
        var a = dir.File("a.zip");
        var b = dir.File("b.zip");
        ZipExtractViewModel vm = null!;
        vm = MakeVm(extractor: path =>
        {
            // Deterministic stand-in for "the window closed mid-batch":
            // cancel from inside the scripted extractor for the FIRST zip,
            // so the row it belongs to still finishes (it already started)
            // but the loop must not begin a second zip afterward.
            if (path == a) vm.Cancel();
            return new Zipper.UnzipResult(path, "ok", path + ".out");
        });

        await vm.AddPaths(new[] { a, b });
        await vm.ExtractAsync();

        var rowA = Assert.Single(vm.Rows, r => r.Path == a);
        var rowB = Assert.Single(vm.Rows, r => r.Path == b);
        Assert.Equal(ZipItemRowStatus.Ok, rowA.StatusKind);        // started zip ran to completion
        Assert.Equal(ZipItemRowStatus.Pending, rowB.StatusKind);   // never started
    }

    [Fact]
    public async Task ClearEmptiesRowsAndResetsStatusAndAddNoteAfterAnExtract()
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
        Assert.Equal("", vm.AddNote);
        Assert.Equal("Extract", vm.ExtractButtonText);
    }

    [Fact]
    public async Task RemoveSelectedRemovesExactlyTheGivenZipRows()
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
    public async Task RealExtractorSmokeTestOnATempZip()
    {
        using var dir = new TempDir();
        var zipPath = Path.Combine(dir.Path, "real.zip");
        using (var zip = ZipFile.Open(zipPath, ZipArchiveMode.Create))
        {
            using var es = zip.CreateEntry("a.txt").Open();
            var bytes = "hello"u8.ToArray();
            es.Write(bytes, 0, bytes.Length);
        }

        var vm = MakeVm();   // default extractor: the real Zipper.Extract

        await vm.AddPaths(new[] { zipPath });
        await vm.ExtractAsync();

        var row = Assert.Single(vm.Rows);
        Assert.Equal(ZipItemRowStatus.Ok, row.StatusKind);
        var outDir = Path.Combine(dir.Path, "real");
        Assert.True(Directory.Exists(outDir));
        Assert.Equal(outDir, row.Output);
    }

    // ---- new facts: the buttons light from what's in the list ---------

    /// <summary>The counts are the contract: a mixed list must report its two
    /// scopes independently, so neither button can be misread about what it
    /// will touch.</summary>
    [Fact]
    public async Task AMixedListCountsItemsAndZipsSeparately()
    {
        using var dir = new TempDir();
        var pdf = dir.File("a.pdf");
        var zip = dir.File("b.zip");
        var vm = MakeVm();

        await vm.AddPaths(new[] { pdf, zip });

        Assert.Equal("Zip 2 items", vm.ZipButtonText);
        Assert.Equal("Extract 1 zip", vm.ExtractButtonText);
        Assert.True(vm.ZipCommand.CanExecute(null));
        Assert.True(vm.ExtractCommand.CanExecute(null));
    }

    /// <summary>An archive is still a file, so Zip never excludes anything —
    /// bundling archives together is a real thing people do.</summary>
    [Fact]
    public async Task ZipIsEnabledByAnyNonEmptyListButExtractNeedsAZip()
    {
        using var dir = new TempDir();
        var vm = MakeVm();

        await vm.AddPaths(new[] { dir.File("a.pdf") });

        Assert.True(vm.ZipCommand.CanExecute(null));
        Assert.False(vm.ExtractCommand.CanExecute(null));
        Assert.Equal("Extract", vm.ExtractButtonText);
    }

    /// <summary>Extract must leave the loose files in a mixed list alone —
    /// the extractor is never even asked about them.</summary>
    [Fact]
    public async Task ExtractTouchesOnlyTheZipRows()
    {
        using var dir = new TempDir();
        var pdf = dir.File("a.pdf");
        var zip = dir.File("b.zip");
        var asked = new List<string>();
        var vm = MakeVm(extractor: p =>
        {
            asked.Add(p);
            return new Zipper.UnzipResult(p, "ok", Path.Combine(dir.Path, "b"));
        });

        await vm.AddPaths(new[] { pdf, zip });
        await vm.ExtractAsync();

        Assert.Equal(new[] { zip }, asked);
        Assert.Equal(ZipItemRowStatus.Pending, vm.Rows.Single(r => r.Path == pdf).StatusKind);
    }

    /// <summary>A folder is a legitimate zip source and must not be mistaken
    /// for an archive because of its name.</summary>
    [Fact]
    public async Task AFolderNamedLikeAnArchiveIsStillAFolder()
    {
        using var dir = new TempDir();
        var folder = dir.Dir("bundle.zip");
        var vm = MakeVm();

        await vm.AddPaths(new[] { folder });

        Assert.Equal("folder", vm.Rows.Single().Kind);
        Assert.False(vm.ExtractCommand.CanExecute(null));
    }
}

/// <summary>A private, GUID-named temp folder for one test's files and
/// folders. Neither ZipViewModelTests nor UnzipViewModelTests factored this
/// out — each just kept a private `_dir` field plus TouchFile/TouchFolder
/// helper methods on the test class itself — but the mixed-kind tests here
/// build several rows of both kinds per test, so a small type with File()/
/// Dir() reads better than repeating that pair of helper methods again.
/// Deleted best-effort on Dispose, same as both ported suites' own
/// teardown.</summary>
public sealed class TempDir : IDisposable
{
    public string Path { get; } =
        System.IO.Path.Combine(System.IO.Path.GetTempPath(), "ordoziptoolvm_" + Guid.NewGuid());

    public TempDir() => Directory.CreateDirectory(Path);

    public string File(string name)
    {
        var p = System.IO.Path.Combine(Path, name);
        System.IO.File.WriteAllText(p, "x");
        return p;
    }

    public string Dir(string name)
    {
        var p = System.IO.Path.Combine(Path, name);
        Directory.CreateDirectory(p);
        return p;
    }

    public void Dispose()
    {
        try { Directory.Delete(Path, true); } catch { /* best effort */ }
    }
}
