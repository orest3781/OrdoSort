using System.Diagnostics;
using OrdoSort.Core;
using OrdoSort.Wpf.Services;
using OrdoSort.Wpf.ViewModels;

namespace OrdoSort.Wpf.Tests;

/// <summary>Task 2 (filename list tool). FilenameListViewModel's listing
/// computes off the UI thread through the same DebouncedProbe shape
/// BulkRenameViewModel uses (see BulkRenameProbeTests) — even with
/// InlineWorkScheduler and probeDelayMs: 0 the underlying System.Threading.Timer
/// still fires its callback on a threadpool thread, not synchronously inside
/// the setter/AddPaths call that armed it, so anything downstream of the
/// probe (Rows, CountsLine) has to be polled for rather than asserted the
/// instant a call returns. Only AddNote (set synchronously inside AddPaths,
/// before Refresh ever arms the probe) and the empty-sources fast path
/// (Clear, and Refresh when nothing has been added yet — both resolve
/// synchronously, matching BulkRenameViewModel's own empty-files shortcut)
/// are safe to assert immediately.</summary>
public class FilenameListViewModelTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "ordofilenamelist_" + Guid.NewGuid());

    public FilenameListViewModelTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { /* best effort */ }
    }

    private string Touch(string relative)
    {
        var path = Path.Combine(_dir, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "x");
        return path;
    }

    /// <summary>Same shape as BulkRenameProbeTests.WaitFor: the listing is
    /// debounced and off the UI thread, so "eventually correct" has to be
    /// polled for.</summary>
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

    private static FilenameListViewModel MakeVm(FakeDialogs dialogs) =>
        new(dialogs, new InlineWorkScheduler(), uiContext: null, probeDelayMs: 0);

    [Fact]
    public void BrowseFolderPopulatesRowsFromTheChosenFolder()
    {
        Touch("b.pdf");
        Touch("a.pdf");
        var dialogs = new FakeDialogs { NextFolder = _dir };
        var vm = MakeVm(dialogs);

        vm.BrowseFolderCommand.Execute(null);

        WaitFor(() => vm.Rows.Count == 2, "Rows should reflect the browsed folder's files");
        Assert.Equal(new[] { "a.pdf", "b.pdf" }, vm.Rows.Select(r => r.Name));   // natural order
    }

    [Fact]
    public void TogglingIncludeExtensionRebuildsRowsImmediately()
    {
        Touch("report.pdf");
        var vm = MakeVm(new FakeDialogs());
        vm.AddPaths(new[] { _dir });
        WaitFor(() => vm.Rows.Count == 1, "the initial add should settle first");
        Assert.Equal("report.pdf", vm.Rows[0].Name);

        vm.IncludeExtension = false;

        WaitFor(() => vm.Rows.Count == 1 && vm.Rows[0].Name == "report",
            "unchecking Include extension should strip it from the listed name");
    }

    [Fact]
    public void TogglingIncludeSubfoldersRebuildsRows()
    {
        Touch("top.pdf");
        Touch(Path.Combine("sub", "nested.pdf"));
        var vm = MakeVm(new FakeDialogs());
        vm.AddPaths(new[] { _dir });
        WaitFor(() => vm.Rows.Count == 1, "IncludeSubfolders defaults to false — only the top-level file should show");

        vm.IncludeSubfolders = true;

        WaitFor(() => vm.Rows.Count == 2, "checking Include subfolders should pull in the nested file too");
        Assert.Equal(new[] { "nested.pdf", "top.pdf" }, vm.Rows.Select(r => r.Name));
    }

    [Fact]
    public void ExtensionFilterNarrowsRowsToTheMatchingTypes()
    {
        Touch("a.pdf");
        Touch("b.txt");
        var vm = MakeVm(new FakeDialogs());
        vm.AddPaths(new[] { _dir });
        WaitFor(() => vm.Rows.Count == 2, "the initial add should settle before filtering");

        vm.ExtensionFilter = "pdf";

        WaitFor(() => vm.Rows.Count == 1 && vm.Rows[0].Name == "a.pdf",
            "the extension filter should narrow the listing down to just the matching type");
    }

    [Fact]
    public void OutputTextMatchesTheCurrentRowsJoinedByNewline()
    {
        Touch("a.pdf");
        Touch("b.pdf");
        var vm = MakeVm(new FakeDialogs());
        vm.AddPaths(new[] { _dir });
        WaitFor(() => vm.Rows.Count == 2, "the add should settle before reading OutputText");

        Assert.Equal(string.Join(Environment.NewLine, vm.Rows.Select(r => r.Name)), vm.OutputText);
    }

    [Fact]
    public void SaveCommandWritesTheOutputTextWhenAPathIsChosen()
    {
        Touch("a.pdf");
        var savePath = Path.Combine(_dir, "out.txt");
        var dialogs = new FakeDialogs { NextSaveFile = savePath };
        var vm = MakeVm(dialogs);
        vm.AddPaths(new[] { _dir });
        WaitFor(() => vm.Rows.Count == 1, "the add should settle before saving");

        vm.SaveCommand.Execute(null);

        // InlineWorkScheduler runs the write synchronously, so no polling
        // is needed here — Save()'s fire-and-forget SaveAsync() runs to
        // completion inline because nothing it awaits ever actually suspends.
        Assert.True(File.Exists(savePath));
        Assert.Equal(vm.OutputText, File.ReadAllText(savePath));
        Assert.Contains("Saved to", vm.Status);
    }

    [Fact]
    public void SaveCommandDoesNothingWhenTheDialogIsCancelled()
    {
        var dialogs = new FakeDialogs { NextSaveFile = null };
        var vm = MakeVm(dialogs);

        vm.SaveCommand.Execute(null);   // must not throw

        Assert.Equal("", vm.Status);
    }

    [Fact]
    public void ClearCommandEmptiesRowsAndCounts()
    {
        Touch("a.pdf");
        var vm = MakeVm(new FakeDialogs());
        vm.AddPaths(new[] { _dir });
        WaitFor(() => vm.Rows.Count == 1, "the add should settle before clearing");

        vm.ClearCommand.Execute(null);

        // Clearing drops _sources to empty, which resolves through the same
        // synchronous fast path Refresh uses when nothing has been added
        // yet — no probe round trip, so this is safe to assert immediately.
        Assert.Empty(vm.Rows);
        Assert.Equal("", vm.CountsLine);
    }

    [Fact]
    public void DuplicateAddPathsSetsAddNoteInsteadOfAddingAgain()
    {
        var vm = MakeVm(new FakeDialogs());

        vm.AddPaths(new[] { _dir });
        Assert.Equal("", vm.AddNote);   // set synchronously inside AddPaths — no probe wait needed

        vm.AddPaths(new[] { _dir });   // same root again
        Assert.Contains("already listed", vm.AddNote);
    }

    /// <summary>Windows resolves a path case-insensitively, so the same
    /// folder root named in two different spellings is one location on
    /// disk. AddPaths dedupes _sources with StringComparer.OrdinalIgnoreCase
    /// — the same policy Intake.Add now owns for the tools that route their
    /// dedupe through it — so the second spelling must not sweep the folder
    /// a second time and land as a second entry.</summary>
    [Fact]
    public void ACaseOnlyDuplicateIsNotAddedTwice()
    {
        var vm = MakeVm(new FakeDialogs());
        var shouty = Path.Combine(Path.GetDirectoryName(_dir)!, Path.GetFileName(_dir).ToUpperInvariant());

        vm.AddPaths(new[] { _dir });
        Assert.Equal("", vm.AddNote);

        vm.AddPaths(new[] { shouty });   // same folder, different spelling
        Assert.Contains("already listed", vm.AddNote);
    }

    /// <summary>"…\dir" and "…\dir\" compare unequal as raw strings, so before
    /// canonicalisation both could sit in _sources and the folder was listed
    /// twice. See PathIdentity — trimming the trailing separator is the half
    /// Path.GetFullPath doesn't do by itself.</summary>
    [Fact]
    public void ATrailingSeparatorDoesNotListTheSameFolderTwice()
    {
        var vm = MakeVm(new FakeDialogs());

        vm.AddPaths(new[] { _dir });
        Assert.Equal("", vm.AddNote);

        vm.AddPaths(new[] { _dir + Path.DirectorySeparatorChar });
        Assert.Contains("already listed", vm.AddNote);
    }

    /// <summary>The whole point of gathering every column up front: a column
    /// toggle must be a projection, not a filesystem walk. If this ever needs a
    /// WaitFor, the data is being re-read and the design has regressed.</summary>
    [Fact]
    public void TurningOnAColumnReprojectsWithoutRebuilding()
    {
        var dialogs = new FakeDialogs { NextFolder = _dir };
        Touch("report.pdf");
        var vm = MakeVm(dialogs);
        vm.BrowseFolderCommand.Execute(null);
        WaitFor(() => vm.Rows.Count == 1, "the add should settle first");

        vm.Columns = FilenameList.Columns.Size;

        // asserted IMMEDIATELY — no WaitFor
        Assert.True(vm.IsTableShape);
        Assert.StartsWith("Name\tSize", vm.OutputText);
    }

    /// <summary>MenuItem.IsChecked is a bool, so the Columns ▾ menu binds each
    /// flag through its own ShowX adapter rather than the flags enum
    /// directly; Columns itself stays the single source of truth both
    /// directions read/write through.</summary>
    [Fact]
    public void TheShowAdaptersAreTwoWayOverTheColumnsFlags()
    {
        var vm = MakeVm(new FakeDialogs());

        vm.ShowSize = true;
        Assert.Equal(FilenameList.Columns.Size, vm.Columns);

        vm.ShowFolder = true;
        Assert.Equal(FilenameList.Columns.Size | FilenameList.Columns.Folder, vm.Columns);

        vm.ShowSize = false;
        Assert.Equal(FilenameList.Columns.Folder, vm.Columns);
        Assert.False(vm.ShowSize);
        Assert.True(vm.ShowFolder);
    }

    [Fact]
    public void TheNameFilterNarrowsRowsInMemory()
    {
        var dialogs = new FakeDialogs { NextFolder = _dir };
        Touch("invoice.pdf"); Touch("report.pdf");
        var vm = MakeVm(dialogs);
        vm.BrowseFolderCommand.Execute(null);
        WaitFor(() => vm.Rows.Count == 2, "the add should settle first");

        vm.NameFilter = "inv";

        Assert.Single(vm.Rows);
        Assert.Equal("invoice.pdf", vm.Rows[0].Name);
    }

    [Fact]
    public void TheNameFilterIsCaseInsensitive()
    {
        var dialogs = new FakeDialogs { NextFolder = _dir };
        Touch("Invoice.pdf");
        var vm = MakeVm(dialogs);
        vm.BrowseFolderCommand.Execute(null);
        WaitFor(() => vm.Rows.Count == 1, "the add should settle first");

        vm.NameFilter = "INVOICE";

        Assert.Single(vm.Rows);
    }

    [Fact]
    public void DescendingReversesTheProjectionWithoutRebuilding()
    {
        var dialogs = new FakeDialogs { NextFolder = _dir };
        Touch("a.pdf"); Touch("b.pdf");
        var vm = MakeVm(dialogs);
        vm.BrowseFolderCommand.Execute(null);
        WaitFor(() => vm.Rows.Count == 2, "the add should settle first");

        vm.Descending = true;

        Assert.Equal(new[] { "b.pdf", "a.pdf" }, vm.Rows.Select(r => r.Name).ToArray());
    }

    [Fact]
    public void OutputCsvFollowsTheSameColumnsAsOutputText()
    {
        var dialogs = new FakeDialogs { NextFolder = _dir };
        Touch("report.pdf");
        var vm = MakeVm(dialogs);
        vm.BrowseFolderCommand.Execute(null);
        WaitFor(() => vm.Rows.Count == 1, "the add should settle first");

        vm.Columns = FilenameList.Columns.Folder;

        Assert.StartsWith("Name,Folder", vm.OutputCsv);
    }

    [Fact]
    public void BrowseFilesAddsEveryFileThePickerReturned()
    {
        var a = Touch("a.pdf");
        var b = Touch("b.pdf");
        var dialogs = new FakeDialogs { NextOpenFiles = new[] { a, b } };
        var vm = MakeVm(dialogs);

        vm.BrowseFilesCommand.Execute(null);

        WaitFor(() => vm.Rows.Count == 2, "both picked files should be listed");
    }

    [Fact]
    public async Task SaveWritesCsvOnceThereAreColumns()
    {
        var target = Path.Combine(_dir, "out.csv");
        var dialogs = new FakeDialogs { NextFolder = _dir, NextSaveFile = target };
        Touch("report.pdf");
        var vm = MakeVm(dialogs);
        vm.BrowseFolderCommand.Execute(null);
        WaitFor(() => vm.Rows.Count == 1, "the add should settle first");

        vm.Columns = FilenameList.Columns.Size;
        await vm.SaveAsync();

        var written = File.ReadAllText(target);
        Assert.StartsWith("Name,Size", written);
    }

    /// <summary>Excel reads a BOM-less CSV in the system ANSI codepage, so
    /// "café" and "文件" open as mojibake — and filenames are exactly the
    /// field this export exists to carry. History.ExportCsv already writes its
    /// own CSV through UTF8Encoding(true) under a doc comment reading
    /// "Excel-friendly BOM"; this is the same rule on this tool's export.
    ///
    /// Asserted on the BYTES on purpose: File.ReadAllText strips a BOM, so
    /// every text-level assertion in this file — including
    /// SaveWritesCsvOnceThereAreColumns just above — passes whether the BOM
    /// is there or not. Only the bytes can tell.</summary>
    [Fact]
    public async Task SaveWritesTheCsvWithABomSoExcelKeepsNonAsciiNames()
    {
        var target = Path.Combine(_dir, "out.csv");
        var dialogs = new FakeDialogs { NextFolder = _dir, NextSaveFile = target };
        Touch("café-文件.pdf");
        var vm = MakeVm(dialogs);
        vm.BrowseFolderCommand.Execute(null);
        WaitFor(() => vm.Rows.Count == 1, "the add should settle first");

        vm.Columns = FilenameList.Columns.Size;
        await vm.SaveAsync();

        var bytes = File.ReadAllBytes(target);
        Assert.True(bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF,
            "the exported CSV should start with the UTF-8 BOM (EF BB BF) — first bytes were "
            + string.Join(" ", bytes.Take(3).Select(b => b.ToString("X2"))));
        Assert.Contains("café-文件.pdf", File.ReadAllText(target));
    }

    /// <summary>The Save button's text and the file Save actually writes come
    /// from one rule, so they cannot drift apart — the button used to be a
    /// literal "Save as .txt…" while a single data column made the dialog
    /// offer a .csv. The notification half matters as much as the value: the
    /// button's Content is bound, so a SaveLabel nobody raises leaves ".txt…"
    /// on screen forever no matter what the property returns.</summary>
    [Fact]
    public void TheSaveLabelNamesTheFormatSaveWillActuallyWrite()
    {
        var vm = MakeVm(new FakeDialogs());
        var notified = new List<string>();
        vm.PropertyChanged += (_, e) => notified.Add(e.PropertyName ?? "");

        Assert.Equal("Save as .txt…", vm.SaveLabel);

        vm.Columns = FilenameList.Columns.Size;
        Assert.Equal("Save as .csv…", vm.SaveLabel);
        Assert.Contains(nameof(vm.SaveLabel), notified);

        // Number alone is still a LIST — "1. invoice.pdf" in a .txt, exactly
        // as FilenameList.IsTable says, which is why the label delegates to it.
        vm.Columns = FilenameList.Columns.Number;
        Assert.Equal("Save as .txt…", vm.SaveLabel);
    }

    [Fact]
    public async Task SaveStillWritesThePlainTextListWithNoColumns()
    {
        var target = Path.Combine(_dir, "out.txt");
        var dialogs = new FakeDialogs { NextFolder = _dir, NextSaveFile = target };
        Touch("report.pdf");
        var vm = MakeVm(dialogs);
        vm.BrowseFolderCommand.Execute(null);
        WaitFor(() => vm.Rows.Count == 1, "the add should settle first");

        await vm.SaveAsync();

        Assert.Equal("report.pdf", File.ReadAllText(target));
    }

    /// <summary>The defect the exclusion set exists to prevent. Note what this
    /// waits on: a NEW file, not the absence of the removed one. Waiting for the
    /// removed row to stay gone proves nothing, because it is already gone the
    /// instant RemoveSelected returns — WaitFor's predicate would be satisfied
    /// before the debounced rebuild ever fires, and a naive Rows.Remove would
    /// pass. "later.pdf" can only appear if the walk actually happened, so by the
    /// time it shows up, a resurrected row would have arrived with it.</summary>
    [Fact]
    public void ARemovedRowStaysRemovedAcrossARebuild()
    {
        var dialogs = new FakeDialogs { NextFolder = _dir };
        Touch("keep.pdf"); Touch("drop.pdf");
        var vm = MakeVm(dialogs);
        vm.BrowseFolderCommand.Execute(null);
        WaitFor(() => vm.Rows.Count == 2, "the add should settle first");

        vm.SelectedPaths = new[] { Path.Combine(_dir, "drop.pdf") };
        vm.RemoveSelectedCommand.Execute(null);
        Assert.Single(vm.Rows);
        Assert.Equal("keep.pdf", vm.Rows[0].Name);

        Touch("later.pdf");            // only a real walk can find this
        vm.ExtensionFilter = "pdf";    // forces a rebuild through the probe

        WaitFor(() => vm.Rows.Any(r => r.Name == "later.pdf"),
            "the rebuild should have walked the folder and picked up the new file");
        Assert.DoesNotContain(vm.Rows, r => r.Name == "drop.pdf");
    }

    [Fact]
    public void TheCountsLineReportsWhatWasRemoved()
    {
        var dialogs = new FakeDialogs { NextFolder = _dir };
        Touch("keep.pdf"); Touch("drop.pdf");
        var vm = MakeVm(dialogs);
        vm.BrowseFolderCommand.Execute(null);
        WaitFor(() => vm.Rows.Count == 2, "the add should settle first");

        vm.SelectedPaths = new[] { Path.Combine(_dir, "drop.pdf") };
        vm.RemoveSelectedCommand.Execute(null);

        Assert.Equal("2 files · 1 removed", vm.CountsLine);
    }

    [Fact]
    public void RestoreRemovedBringsThemBack()
    {
        var dialogs = new FakeDialogs { NextFolder = _dir };
        Touch("keep.pdf"); Touch("drop.pdf");
        var vm = MakeVm(dialogs);
        vm.BrowseFolderCommand.Execute(null);
        WaitFor(() => vm.Rows.Count == 2, "the add should settle first");

        vm.SelectedPaths = new[] { Path.Combine(_dir, "drop.pdf") };
        vm.RemoveSelectedCommand.Execute(null);
        vm.RestoreRemovedCommand.Execute(null);

        Assert.Equal(2, vm.Rows.Count);
        Assert.Equal(0, vm.RemovedCount);
    }

    [Fact]
    public void ClearForgetsTheRemovals()
    {
        var dialogs = new FakeDialogs { NextFolder = _dir };
        Touch("keep.pdf"); Touch("drop.pdf");
        var vm = MakeVm(dialogs);
        vm.BrowseFolderCommand.Execute(null);
        WaitFor(() => vm.Rows.Count == 2, "the add should settle first");
        vm.SelectedPaths = new[] { Path.Combine(_dir, "drop.pdf") };
        vm.RemoveSelectedCommand.Execute(null);

        vm.ClearCommand.Execute(null);
        vm.BrowseFolderCommand.Execute(null);

        WaitFor(() => vm.Rows.Count == 2, "Clear resets the exclusion set as well as the sources");
    }

    [Fact]
    public void RemoveSelectedDoesNothingWithAnEmptySelection()
    {
        var dialogs = new FakeDialogs { NextFolder = _dir };
        Touch("keep.pdf");
        var vm = MakeVm(dialogs);
        vm.BrowseFolderCommand.Execute(null);
        WaitFor(() => vm.Rows.Count == 1, "the add should settle first");

        vm.RemoveSelectedCommand.Execute(null);

        Assert.Single(vm.Rows);
    }

    [Fact]
    public void CopyTextIsEverythingWhenNothingIsSelected()
    {
        var dialogs = new FakeDialogs { NextFolder = _dir };
        Touch("a.pdf"); Touch("b.pdf");
        var vm = MakeVm(dialogs);
        vm.BrowseFolderCommand.Execute(null);
        WaitFor(() => vm.Rows.Count == 2, "the add should settle first");

        Assert.Equal("a.pdf" + Environment.NewLine + "b.pdf", vm.CopyText);
    }

    [Fact]
    public void CopyTextIsJustTheSelectionWhenThereIsOne()
    {
        var dialogs = new FakeDialogs { NextFolder = _dir };
        Touch("a.pdf"); Touch("b.pdf");
        var vm = MakeVm(dialogs);
        vm.BrowseFolderCommand.Execute(null);
        WaitFor(() => vm.Rows.Count == 2, "the add should settle first");

        vm.SelectedPaths = new[] { Path.Combine(_dir, "b.pdf") };

        Assert.Equal("b.pdf", vm.CopyText);
    }

    [Fact]
    public void TheSelectionKeepsTheColumnsAndTheirOrder()
    {
        var dialogs = new FakeDialogs { NextFolder = _dir };
        Touch("a.pdf"); Touch("b.pdf");
        var vm = MakeVm(dialogs);
        vm.BrowseFolderCommand.Execute(null);
        WaitFor(() => vm.Rows.Count == 2, "the add should settle first");

        vm.Columns = FilenameList.Columns.Folder;
        vm.SelectedPaths = new[] { Path.Combine(_dir, "b.pdf") };

        Assert.StartsWith("Name\tFolder" + Environment.NewLine + "b.pdf", vm.CopyText);
    }

    [Fact]
    public void NoteCopiedSaysHowManyOfHowMany()
    {
        var dialogs = new FakeDialogs { NextFolder = _dir };
        Touch("a.pdf"); Touch("b.pdf");
        var vm = MakeVm(dialogs);
        vm.BrowseFolderCommand.Execute(null);
        WaitFor(() => vm.Rows.Count == 2, "the add should settle first");

        vm.SelectedPaths = new[] { Path.Combine(_dir, "b.pdf") };
        vm.NoteCopied();

        Assert.Equal("Copied 1 of 2", vm.Status);
    }

    [Fact]
    public void NoteCopiedSaysThePlainCountWhenNothingIsSelected()
    {
        var dialogs = new FakeDialogs { NextFolder = _dir };
        Touch("a.pdf");
        var vm = MakeVm(dialogs);
        vm.BrowseFolderCommand.Execute(null);
        WaitFor(() => vm.Rows.Count == 1, "the add should settle first");

        vm.NoteCopied();

        Assert.Equal("Copied 1 name", vm.Status);
    }
}
