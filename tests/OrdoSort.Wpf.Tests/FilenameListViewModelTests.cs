using System.Collections.Specialized;
using System.ComponentModel;
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

    private static FilenameListViewModel MakeVm(FakeDialogs dialogs,
        Func<string, PageCounts.CountResult>? counter = null) =>
        new(dialogs, new InlineWorkScheduler(), uiContext: null, probeDelayMs: 0, counter: counter);

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

    /// <summary>Audit FL-06. Status carried "Copied 12 names", "Saved to
    /// filenames.csv", "Couldn't save: …" and "Clipboard busy" in one amber —
    /// the colour this app reserves for "needs attention", so a clean save
    /// looked exactly like a failed one. PageCountsWindow, written for the same
    /// footer, already puts save/copy feedback in CaptionText and keeps amber
    /// for the batch's own verdict; its comment even cites this window as the
    /// model it followed. This window was the one not following its own
    /// convention. The view model now says which kind of message it is.</summary>
    [Fact]
    public void ASuccessfulSaveIsNotFlaggedAsAProblem()
    {
        Touch("a.pdf");
        var dialogs = new FakeDialogs { NextSaveFile = Path.Combine(_dir, "out.txt") };
        var vm = MakeVm(dialogs);
        vm.AddPaths(new[] { _dir });
        WaitFor(() => vm.Rows.Count == 1, "the add should settle before saving");

        vm.SaveCommand.Execute(null);

        Assert.Contains("Saved to", vm.Status);
        Assert.False(vm.StatusIsProblem);
    }

    [Fact]
    public void AFailedSaveIsFlaggedAsAProblem()
    {
        Touch("a.pdf");
        var intoAMissingFolder = Path.Combine(_dir, "no-such-folder", "out.txt");
        var dialogs = new FakeDialogs { NextSaveFile = intoAMissingFolder };
        var vm = MakeVm(dialogs);
        vm.AddPaths(new[] { _dir });
        WaitFor(() => vm.Rows.Count == 1, "the add should settle before saving");

        vm.SaveCommand.Execute(null);

        Assert.Contains("Couldn't save", vm.Status);
        Assert.True(vm.StatusIsProblem);
    }

    /// <summary>A problem flag that never clears is its own bug: the next
    /// successful action has to put the footer back to normal.</summary>
    [Fact]
    public void ASuccessAfterAFailureClearsTheProblemFlag()
    {
        Touch("a.pdf");
        var dialogs = new FakeDialogs { NextSaveFile = Path.Combine(_dir, "gone", "out.txt") };
        var vm = MakeVm(dialogs);
        vm.AddPaths(new[] { _dir });
        WaitFor(() => vm.Rows.Count == 1, "the add should settle before saving");

        vm.SaveCommand.Execute(null);
        Assert.True(vm.StatusIsProblem);

        dialogs.NextSaveFile = Path.Combine(_dir, "out.txt");
        vm.SaveCommand.Execute(null);

        Assert.False(vm.StatusIsProblem);
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
    /// WaitFor, the data is being re-read and the design has regressed.
    ///
    /// WHAT IT ASSERTS AND WHY. IsTableShape and OutputText are both computed
    /// on demand from Rows + Columns, so both are already true the instant the
    /// setter returns whether it called Reproject() or Refresh() — this test
    /// used to assert only those two and was therefore green against the exact
    /// regression it is named for. The notifications are what tell the two
    /// apart: Reproject re-renders in memory and raises OutputText and Rows'
    /// CollectionChanged SYNCHRONOUSLY, inside the setter, on this thread,
    /// where Refresh only arms the debounced probe and its result arrives
    /// later, on a threadpool thread, long after the next line has run.
    ///
    /// TheNameFilterNarrowsRowsInMemory and
    /// DescendingReversesTheProjectionWithoutRebuilding get this for free by
    /// asserting on Rows' CONTENT immediately, which a not-yet-run rebuild
    /// cannot have produced. A column toggle changes no row's content, so this
    /// one has to watch the notifications instead.
    ///
    /// The thread check is what makes that a fact rather than a race: a
    /// rebuild's notifications arrive on the probe's own thread, so counting
    /// only the ones raised on this thread means a late arrival can never be
    /// mistaken for a synchronous reprojection.</summary>
    [Fact]
    public void TurningOnAColumnReprojectsWithoutRebuilding()
    {
        var dialogs = new FakeDialogs { NextFolder = _dir };
        Touch("report.pdf");
        var vm = MakeVm(dialogs);
        vm.BrowseFolderCommand.Execute(null);
        WaitFor(() => vm.Rows.Count == 1, "the add should settle first");
        var rowBefore = vm.Rows[0];

        var setterThread = Environment.CurrentManagedThreadId;
        var notified = new List<string>();
        var rowsChanged = new List<NotifyCollectionChangedAction>();
        void OnPropertyChanged(object? _, PropertyChangedEventArgs e)
        {
            if (Environment.CurrentManagedThreadId == setterThread) notified.Add(e.PropertyName ?? "");
        }
        void OnRowsChanged(object? _, NotifyCollectionChangedEventArgs e)
        {
            if (Environment.CurrentManagedThreadId == setterThread) rowsChanged.Add(e.Action);
        }
        vm.PropertyChanged += OnPropertyChanged;
        ((INotifyCollectionChanged)vm.Rows).CollectionChanged += OnRowsChanged;

        vm.Columns = FilenameList.Columns.Size;

        vm.PropertyChanged -= OnPropertyChanged;
        ((INotifyCollectionChanged)vm.Rows).CollectionChanged -= OnRowsChanged;

        // all asserted IMMEDIATELY — no WaitFor
        Assert.Contains(nameof(vm.OutputText), notified);
        Assert.Contains(NotifyCollectionChangedAction.Reset, rowsChanged);   // Rows.Clear()
        Assert.Contains(NotifyCollectionChangedAction.Add, rowsChanged);     // ...then re-added
        // The SAME FileRow instance came back: an in-memory reprojection reuses
        // what the last Build produced, where a fresh walk would have made a new one.
        Assert.Same(rowBefore, vm.Rows[0]);
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

    /// <summary>The Find box made a previously-rare state easy to reach, and
    /// the window's empty-state text was bound to Rows.Count — so filtering a
    /// 200-file listing down to nothing told the user to drag in files they
    /// had already dragged in. The window binds these two flags instead, the
    /// way HistoryWindow binds HistoryViewModel's IsEmpty/NoMatches.</summary>
    [Fact]
    public void TheEmptyStateTellsNothingLoadedApartFromNothingMatching()
    {
        var dialogs = new FakeDialogs { NextFolder = _dir };
        Touch("invoice.pdf");
        var vm = MakeVm(dialogs);

        Assert.True(vm.IsEmpty);       // nothing added yet — the real empty state
        Assert.False(vm.NoMatches);

        vm.BrowseFolderCommand.Execute(null);
        WaitFor(() => vm.Rows.Count == 1, "the add should settle first");
        Assert.False(vm.IsEmpty);
        Assert.False(vm.NoMatches);

        vm.NameFilter = "zzz";

        Assert.Empty(vm.Rows);
        Assert.False(vm.IsEmpty);      // the file is still listed, just hidden
        Assert.True(vm.NoMatches);
    }

    /// <summary>Audit FL-01. The IsEmpty/NoMatches split fixed the Find box
    /// and only the Find box. ExtensionFilter is applied inside Build (via
    /// Intake.Expand), so a type filter matching nothing empties _allRows as
    /// well as Rows — and IsEmpty, defined as "Rows.Count == 0 &amp;&amp;
    /// _allRows.Count == 0", went true. A user with a folder already added
    /// was told to drag in files they had already dragged in: the exact bug
    /// the split exists to prevent, reached by the other filter.</summary>
    [Fact]
    public void TheTypeFilterHidingEverythingIsNoMatchesNotAnEmptyList()
    {
        var dialogs = new FakeDialogs { NextFolder = _dir };
        Touch("invoice.pdf");
        var vm = MakeVm(dialogs);

        vm.BrowseFolderCommand.Execute(null);
        WaitFor(() => vm.Rows.Count == 1, "the add should settle first");

        vm.ExtensionFilter = "zzz";
        WaitFor(() => vm.Rows.Count == 0, "the type filter should hide the only file");

        Assert.False(vm.IsEmpty);      // the folder IS added — nothing left to drag in
        Assert.True(vm.NoMatches);
    }

    /// <summary>Audit FL-03. Reproject calls Rows.Clear(), the DataGrid drops
    /// its selection on that Reset, and the window pushes the now-empty
    /// selection straight back into SelectedPaths — so selecting five rows and
    /// then ticking a column in Columns ▾ (a perfectly ordinary thing to do
    /// before copying) silently turned "copy my five" into "copy all 200",
    /// with CopyText's everything-when-nothing-is-selected fallback making it
    /// look like a clean copy.
    ///
    /// The handler below IS the regression: a view model on its own never sees
    /// the selection cleared, so a test without it passes against the broken
    /// code and proves nothing. It reproduces exactly what WPF does.</summary>
    [Fact]
    public void TheRowSelectionSurvivesAReprojection()
    {
        Touch("alpha.pdf");
        Touch("beta.pdf");
        Touch("gamma.pdf");
        var vm = MakeVm(new FakeDialogs());
        vm.AddPaths(new[] { _dir });
        WaitFor(() => vm.Rows.Count == 3, "the add should settle first");

        vm.Rows.CollectionChanged += (_, e) =>
        {
            if (e.Action == NotifyCollectionChangedAction.Reset)
                vm.SelectedPaths = Array.Empty<string>();
        };

        var alpha = Path.Combine(_dir, "alpha.pdf");
        vm.SelectedPaths = new[] { alpha };

        vm.Descending = true;   // a reproject that hides nothing

        Assert.Equal(new[] { alpha }, vm.SelectedPaths);
        Assert.Equal("alpha.pdf", vm.CopyText);
    }

    /// <summary>The other half of FL-03: restoring the selection must not
    /// resurrect rows the reproject deliberately hid, or Copy would emit a
    /// name that is no longer on screen.</summary>
    [Fact]
    public void ARestoredSelectionDropsRowsTheFilterHid()
    {
        Touch("alpha.pdf");
        Touch("beta.pdf");
        var vm = MakeVm(new FakeDialogs());
        vm.AddPaths(new[] { _dir });
        WaitFor(() => vm.Rows.Count == 2, "the add should settle first");

        vm.Rows.CollectionChanged += (_, e) =>
        {
            if (e.Action == NotifyCollectionChangedAction.Reset)
                vm.SelectedPaths = Array.Empty<string>();
        };

        var alpha = Path.Combine(_dir, "alpha.pdf");
        vm.SelectedPaths = new[] { alpha, Path.Combine(_dir, "beta.pdf") };

        vm.NameFilter = "alpha";

        Assert.Equal(new[] { alpha }, vm.SelectedPaths);
    }

    /// <summary>Audit FL-05. Delete removed rows instantly and the only way
    /// back was "Restore removed", which restores EVERYTHING — so curating 60
    /// rows down to 12 and then fat-fingering Delete once more meant undoing
    /// all 48 and starting over. Removals become a stack; Ctrl+Z pops the last
    /// batch and leaves every earlier one alone.</summary>
    [Fact]
    public void UndoRestoresOnlyTheLastRemovalBatch()
    {
        Touch("alpha.pdf");
        Touch("beta.pdf");
        Touch("gamma.pdf");
        var vm = MakeVm(new FakeDialogs());
        vm.AddPaths(new[] { _dir });
        WaitFor(() => vm.Rows.Count == 3, "the add should settle first");

        vm.SelectedPaths = new[] { Path.Combine(_dir, "alpha.pdf") };
        vm.RemoveSelectedCommand.Execute(null);
        Assert.Equal(2, vm.Rows.Count);

        vm.SelectedPaths = new[] { Path.Combine(_dir, "beta.pdf") };
        vm.RemoveSelectedCommand.Execute(null);
        Assert.Single(vm.Rows);

        vm.UndoRemovalCommand.Execute(null);

        // beta comes back; alpha stays removed — that is the whole point
        Assert.Equal(new[] { "beta.pdf", "gamma.pdf" }, vm.Rows.Select(r => r.Name));
    }

    /// <summary>Undo pops one batch at a time; Restore removed still clears the
    /// whole stack in one go. Two controls, each saying plainly what it does.</summary>
    [Fact]
    public void RestoreRemovedStillClearsEveryBatchAtOnce()
    {
        Touch("alpha.pdf");
        Touch("beta.pdf");
        var vm = MakeVm(new FakeDialogs());
        vm.AddPaths(new[] { _dir });
        WaitFor(() => vm.Rows.Count == 2, "the add should settle first");

        vm.SelectedPaths = new[] { Path.Combine(_dir, "alpha.pdf") };
        vm.RemoveSelectedCommand.Execute(null);
        vm.SelectedPaths = new[] { Path.Combine(_dir, "beta.pdf") };
        vm.RemoveSelectedCommand.Execute(null);
        Assert.Empty(vm.Rows);

        vm.RestoreRemovedCommand.Execute(null);

        Assert.Equal(2, vm.Rows.Count);
        Assert.Equal(0, vm.RemovedCount);
    }

    /// <summary>Undo with nothing removed must be a no-op, not a crash — the
    /// keystroke is always live, whatever state the list is in.</summary>
    [Fact]
    public void UndoWithNothingRemovedDoesNothing()
    {
        Touch("alpha.pdf");
        var vm = MakeVm(new FakeDialogs());
        vm.AddPaths(new[] { _dir });
        WaitFor(() => vm.Rows.Count == 1, "the add should settle first");

        vm.UndoRemovalCommand.Execute(null);

        Assert.Single(vm.Rows);
    }

    /// <summary>The Pages column is the first that costs disk I/O per row, so
    /// nothing is counted until it is asked for. The Assert.All below is what
    /// makes this a real test rather than one waiting on a state the code had
    /// already reached: it pins the false half of the transition before
    /// ShowPages is ever set.</summary>
    [Fact]
    public void TurningOnThePagesColumnCountsTheListedPdfs()
    {
        Touch("a.pdf");
        Touch("b.pdf");
        var vm = MakeVm(new FakeDialogs(), counter: p => new PageCounts.CountResult(p, 7));
        vm.AddPaths(new[] { _dir });
        WaitFor(() => vm.Rows.Count == 2, "the add should settle first");

        Assert.All(vm.Rows, r => Assert.Null(r.Pages));   // nothing counted until asked

        vm.ShowPages = true;

        WaitFor(() => vm.Rows.All(r => r.Pages == 7), "every listed PDF should get its count");
    }

    /// <summary>Opening a .txt as a PDF is a guaranteed failure and a wasted
    /// disk read; the counter must never even be asked.</summary>
    [Fact]
    public void OnlyPdfRowsAreEverCounted()
    {
        Touch("a.pdf");
        Touch("notes.txt");
        var asked = new List<string>();
        var vm = MakeVm(new FakeDialogs(), counter: p =>
        {
            lock (asked) asked.Add(p);
            return new PageCounts.CountResult(p, 3);
        });
        vm.AddPaths(new[] { _dir });
        WaitFor(() => vm.Rows.Count == 2, "the add should settle first");

        vm.ShowPages = true;
        WaitFor(() => vm.Rows.Single(r => r.Name == "a.pdf").Pages == 3, "the PDF should be counted");

        Assert.Null(vm.Rows.Single(r => r.Name == "notes.txt").Pages);
        Assert.Equal(new[] { Path.Combine(_dir, "a.pdf") }, asked);
    }

    /// <summary>A count that fails carries its reason into the row, the way
    /// PageCountsViewModel's own Note column always has — a blank cell would
    /// leave the user unable to tell "not a PDF" from "locked".</summary>
    [Fact]
    public void AFailedCountCarriesItsReasonIntoTheRow()
    {
        Touch("locked.pdf");
        var vm = MakeVm(new FakeDialogs(), counter: p =>
            new PageCounts.CountResult(p, null, "open in another program — couldn't count"));
        vm.AddPaths(new[] { _dir });
        WaitFor(() => vm.Rows.Count == 1, "the add should settle first");

        vm.ShowPages = true;

        WaitFor(() => vm.Rows[0].PageNote.Length > 0, "the failure should reach the row");
        Assert.Null(vm.Rows[0].Pages);
        Assert.Contains("another program", vm.Rows[0].PageNote);
    }

    /// <summary>Counts are cached on the full path, so the reprojections that
    /// happen on every Find keystroke re-render what is already known instead
    /// of going back to disk. Without this, typing in the Find box would reopen
    /// every PDF in the list, once per character.</summary>
    [Fact]
    public void ACountedRowIsNotCountedAgainWhenTheFilterChanges()
    {
        Touch("alpha.pdf");
        Touch("beta.pdf");
        var calls = 0;
        var vm = MakeVm(new FakeDialogs(), counter: p =>
        {
            Interlocked.Increment(ref calls);
            return new PageCounts.CountResult(p, 5);
        });
        vm.AddPaths(new[] { _dir });
        WaitFor(() => vm.Rows.Count == 2, "the add should settle first");

        vm.ShowPages = true;
        WaitFor(() => vm.Rows.All(r => r.Pages == 5), "both PDFs should be counted");
        var callsAfterCounting = calls;

        // Reproject is synchronous inside the setter, so this needs no polling —
        // and polling for an already-true condition is exactly the trap here.
        vm.NameFilter = "alpha";

        Assert.Single(vm.Rows);
        Assert.Equal(5, vm.Rows[0].Pages);            // still shows what it knows
        Assert.Equal(callsAfterCounting, calls);      // without going back to disk
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
