using System.Diagnostics;
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
        Assert.Equal(new[] { "a.pdf", "b.pdf" }, vm.Rows);   // natural order
    }

    [Fact]
    public void TogglingIncludeExtensionRebuildsRowsImmediately()
    {
        Touch("report.pdf");
        var vm = MakeVm(new FakeDialogs());
        vm.AddPaths(new[] { _dir });
        WaitFor(() => vm.Rows.Count == 1, "the initial add should settle first");
        Assert.Equal("report.pdf", vm.Rows[0]);

        vm.IncludeExtension = false;

        WaitFor(() => vm.Rows.Count == 1 && vm.Rows[0] == "report",
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
        Assert.Equal(new[] { "nested.pdf", "top.pdf" }, vm.Rows);
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

        WaitFor(() => vm.Rows.Count == 1 && vm.Rows[0] == "a.pdf",
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

        Assert.Equal(string.Join(Environment.NewLine, vm.Rows), vm.OutputText);
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
}
