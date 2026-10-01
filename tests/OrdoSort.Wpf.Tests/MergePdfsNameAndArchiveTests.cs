using OrdoSort.Core;
using OrdoSort.Wpf.ViewModels;

namespace OrdoSort.Wpf.Tests;

/// <summary>Merge PDFs, owner request 2026-10-01: a box for the merged PDF's
/// name, and a tick box that moves the originals into a dated archive folder
/// once the merge has succeeded. Real merges and real moves on a temp folder,
/// except where a fact needs a merge to fail on cue.</summary>
public class MergePdfsNameAndArchiveTests : IDisposable
{
    private readonly TempDir _dir = new();
    private readonly ManualTimeProvider _clock = new();
    public void Dispose() => _dir.Dispose();

    private MergePdfsViewModel NewViewModel(
        Config? config = null, Action? saveConfig = null, FakeDialogs? dialogs = null,
        Func<IReadOnlyList<string>, string?, IReadOnlyList<string>, Func<PasswordRequest, string?>?, PdfMerge.MergeResult>? fileMerger = null,
        Func<string, IReadOnlyList<string>, Func<PasswordRequest, string?>?, PdfMerge.MergeResult>? zipMerger = null) =>
        new(dialogs ?? new FakeDialogs(), Array.Empty<string>(), new InlineWorkScheduler(), uiContext: null,
            zipMerger, fileMerger,
            zipProbe: (p, _) => new Zipper.ZipProbeResult(p, "not_encrypted"),
            pdfProbe: (p, _) => new Unlock.ProbeResult("not_encrypted", p),
            config: config, saveConfig: saveConfig, time: _clock);

    private string Pdf(string name, int pages = 1) =>
        MergePdfsViewModelTests.WritePdf(Path.Combine(_dir.Path, name), pages);

    private string OnDisk(string name) => Path.Combine(_dir.Path, name);

    /// <summary>The name Merge picks when nobody types one: the folder's.</summary>
    private string SuggestedName => Path.GetFileName(_dir.Path) + ".pdf";

    private string ArchiveFolder =>
        MergeArchive.FolderFor(OnDisk("any.pdf"), _clock.GetLocalNow().DateTime);

    // ---------------------------------------------------------- the name box

    [Fact]
    public async Task TheNameBoxStartsWithTheNameMergeWouldPick()
    {
        var vm = NewViewModel();
        Assert.Equal("", vm.OutputName);
        Assert.False(vm.CanNameOutput);

        await vm.AddPaths(new[] { Pdf("a.pdf") });

        Assert.Equal(SuggestedName, vm.OutputName);
        Assert.True(vm.CanNameOutput);
        Assert.Equal(MergePdfsViewModel.OutputNameHint, vm.OutputNameNote);
    }

    /// <summary>A zip becomes "zip name".pdf, so with only zips listed there
    /// is nothing for the box to name.</summary>
    [Fact]
    public async Task WithOnlyZipsListedThereIsNothingToName()
    {
        var vm = NewViewModel();

        await vm.AddPaths(new[] { _dir.File("scans.zip") });

        Assert.False(vm.CanNameOutput);
        Assert.Equal("", vm.OutputName);
    }

    [Fact]
    public async Task ATypedNameIsWhatTheMergeIsSavedAs()
    {
        var vm = NewViewModel();
        await vm.AddPaths(new[] { Pdf("a.pdf"), Pdf("b.pdf", pages: 2) });

        vm.OutputName = "Smith invoices";
        await vm.MergeAsync(null);

        Assert.True(File.Exists(OnDisk("Smith invoices.pdf")));
        Assert.False(File.Exists(OnDisk(SuggestedName)));
        Assert.All(vm.Rows, r => Assert.Equal(OnDisk("Smith invoices.pdf"), r.Output));
    }

    [Fact]
    public async Task ATypedNameStaysWhileTheListChanges()
    {
        var vm = NewViewModel();
        await vm.AddPaths(new[] { Pdf("a.pdf") });
        vm.OutputName = "Smith invoices";

        await vm.AddPaths(new[] { Pdf("b.pdf") });

        Assert.Equal("Smith invoices", vm.OutputName);
    }

    [Fact]
    public async Task AnEmptiedNameBoxUsesTheSuggestedNameAndSaysSo()
    {
        var vm = NewViewModel();
        await vm.AddPaths(new[] { Pdf("a.pdf") });

        vm.OutputName = "";

        Assert.Equal("", vm.OutputName);   // left empty: the box is not refilled under the user's hands
        Assert.Equal($"Blank uses {SuggestedName}", vm.OutputNameNote);
        Assert.True(vm.MergeCommand.CanExecute(null));
        await vm.MergeAsync(null);
        Assert.True(File.Exists(OnDisk(SuggestedName)));
    }

    [Fact]
    public async Task ANameWindowsCannotTakeHoldsMergeBackAndSaysWhy()
    {
        var vm = NewViewModel();
        await vm.AddPaths(new[] { Pdf("a.pdf") });

        vm.OutputName = "Smith: invoices";

        Assert.True(vm.OutputNameIsRefused);
        Assert.Contains("can't contain ':'", vm.OutputNameNote);
        Assert.False(vm.MergeCommand.CanExecute(null));

        vm.OutputName = "Smith invoices";

        Assert.False(vm.OutputNameIsRefused);
        Assert.Equal(MergePdfsViewModel.OutputNameHint, vm.OutputNameNote);
        Assert.True(vm.MergeCommand.CanExecute(null));
    }

    [Fact]
    public async Task AfterAMergeTheNameBoxGoesBackToFollowingTheList()
    {
        var vm = NewViewModel();
        await vm.AddPaths(new[] { Pdf("a.pdf") });
        vm.OutputName = "Smith invoices";
        await vm.MergeAsync(null);

        Assert.Equal("", vm.OutputName);       // nothing left to merge
        Assert.False(vm.CanNameOutput);

        await vm.AddPaths(new[] { Pdf("next.pdf") });

        Assert.Equal(SuggestedName, vm.OutputName);
    }

    /// <summary>A merge held back (a locked PDF nobody opened) keeps the
    /// name: the next press is the same merge.</summary>
    [Fact]
    public async Task AMergeThatDidNotHappenKeepsTheTypedName()
    {
        var a = Pdf("a.pdf");
        var vm = NewViewModel(fileMerger: (paths, _, _, _) =>
            new PdfMerge.MergeResult(paths[0], "needs_password", Message: "needs a password", Item: paths[0]));
        await vm.AddPaths(new[] { a });
        vm.OutputName = "Smith invoices";

        await vm.MergeAsync(null);

        Assert.Equal("Smith invoices", vm.OutputName);
    }

    [Fact]
    public async Task MergeToStartsFromTheNameInTheBox()
    {
        var dialogs = new FakeDialogs();   // NextSaveFile null: the dialog is cancelled
        var vm = NewViewModel(dialogs: dialogs);
        await vm.AddPaths(new[] { Pdf("a.pdf") });

        vm.OutputName = "Smith invoices";
        await vm.MergeToAsync();

        Assert.Equal("Smith invoices.pdf", dialogs.LastSaveSuggested);
    }

    // ------------------------------------------------------------ the archive

    [Fact]
    public async Task OriginalsStayWhereTheyAreUnlessTheBoxIsTicked()
    {
        var a = Pdf("a.pdf");
        var vm = NewViewModel();
        Assert.False(vm.ArchiveOriginals);
        await vm.AddPaths(new[] { a });

        await vm.MergeAsync(null);

        Assert.True(File.Exists(a));
        Assert.False(Directory.Exists(ArchiveFolder));
    }

    [Fact]
    public async Task WithTheBoxTickedTheOriginalsMoveToTheDatedFolderOnceMerged()
    {
        var a = Pdf("a.pdf");
        var b = Pdf("b.pdf", pages: 2);
        var vm = NewViewModel();
        vm.ArchiveOriginals = true;
        await vm.AddPaths(new[] { a, b });

        await vm.MergeAsync(null);

        Assert.True(File.Exists(OnDisk(SuggestedName)));
        Assert.False(File.Exists(a));
        Assert.False(File.Exists(b));
        Assert.True(File.Exists(Path.Combine(ArchiveFolder, "a.pdf")));
        Assert.True(File.Exists(Path.Combine(ArchiveFolder, "b.pdf")));
        Assert.All(vm.Rows, r =>
        {
            Assert.Equal(ZipItemRowStatus.Ok, r.StatusKind);
            Assert.EndsWith($"original moved to {Path.GetFileName(ArchiveFolder)}", r.Note);
        });
        Assert.Equal("1 merged · 2 originals moved to the archive", vm.Status);
    }

    [Fact]
    public void TickingTheBoxIsRememberedInTheConfig()
    {
        var config = new Config();
        var saves = 0;
        var vm = NewViewModel(config, saveConfig: () => saves++);

        vm.ArchiveOriginals = true;

        Assert.True(config.MergeArchiveOriginals);
        Assert.Equal(1, saves);
        Assert.True(NewViewModel(config).ArchiveOriginals);
    }

    [Fact]
    public async Task AMergeThatFailsMovesNothing()
    {
        var a = Pdf("a.pdf");
        var vm = NewViewModel(fileMerger: (paths, _, _, _) =>
            new PdfMerge.MergeResult(paths[0], "error", Message: "couldn't save the merged PDF: disk full"));
        vm.ArchiveOriginals = true;
        await vm.AddPaths(new[] { a });

        await vm.MergeAsync(null);

        Assert.True(File.Exists(a));
        Assert.False(Directory.Exists(ArchiveFolder));
    }

    [Fact]
    public async Task AZipMovesToTheArchiveOnceItsContentsAreMerged()
    {
        var zip = _dir.File("scans.zip");
        var vm = NewViewModel(zipMerger: (path, _, _) =>
            new PdfMerge.MergeResult(path, "ok", Output: OnDisk("scans.pdf"), PdfCount: 3));
        vm.ArchiveOriginals = true;
        await vm.AddPaths(new[] { zip });

        await vm.MergeAsync(null);

        Assert.False(File.Exists(zip));
        Assert.True(File.Exists(Path.Combine(ArchiveFolder, "scans.zip")));
    }

    /// <summary>The merge itself worked, so the row stays merged; the one
    /// file that would not move is named, with the reason.</summary>
    [Fact]
    public async Task AnOriginalThatCannotBeMovedIsNamedAndTheMergeStillCounts()
    {
        var held = Pdf("held.pdf");
        var free = Pdf("free.pdf");
        var vm = NewViewModel();
        vm.ArchiveOriginals = true;
        await vm.AddPaths(new[] { held, free });

        using (File.Open(held, FileMode.Open, FileAccess.Read, FileShare.Read))
            await vm.MergeAsync(null);

        Assert.True(File.Exists(OnDisk(SuggestedName)));
        Assert.True(File.Exists(held));
        Assert.False(File.Exists(free));
        var heldRow = vm.Rows.Single(r => r.Path == held);
        Assert.Equal(ZipItemRowStatus.Ok, heldRow.StatusKind);
        Assert.Contains("original not moved:", heldRow.Note);
        Assert.Equal("1 merged · 1 original moved to the archive · 1 original couldn't be moved", vm.Status);
    }

    /// <summary>The tick box is read once, when a merge starts: a tick that
    /// lands while it runs applies to the next merge, not to this one.</summary>
    [Fact]
    public async Task ATickThatLandsMidMergeAppliesToTheNextMergeOnly()
    {
        var a = Pdf("a.pdf");
        MergePdfsViewModel? vm = null;
        vm = NewViewModel(fileMerger: (paths, _, _, _) =>
        {
            vm!.ArchiveOriginals = true;   // ticked mid-run: this run was started without it
            return new PdfMerge.MergeResult(paths[0], "ok", Output: OnDisk("out.pdf"), PdfCount: 1);
        });
        await vm.AddPaths(new[] { a });

        await vm.MergeAsync(null);

        Assert.True(File.Exists(a));
    }
}
