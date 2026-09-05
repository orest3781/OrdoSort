using OrdoSort.Core;
using OrdoSort.Wpf.ViewModels;

namespace OrdoSort.Wpf.Tests;

public class HistoryViewModelTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "ordohist_" + Guid.NewGuid());
    private readonly History _history;
    private readonly FakeDialogs _dialogs = new();

    public HistoryViewModelTests()
    {
        Directory.CreateDirectory(_dir);
        _history = new History(Path.Combine(_dir, "history.sqlite"));
    }

    public void Dispose()
    {
        _history.Dispose();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        for (var i = 0; i < 10; i++)
        {
            try { Directory.Delete(_dir, true); return; } catch { Thread.Sleep(50); }
        }
    }

    private void Seed(int count, string route = "Invoices")
    {
        for (var i = 0; i < count; i++)
            _history.LogCommit($"c:\\in\\{i}.pdf", $"{i}.pdf", $"NAME {i}.pdf",
                $"NAME {i}", "insert", "", route, "c:\\out", tagged: false, "");
    }

    [Fact]
    public void LoadsNewestFiveHundredWithFooter()
    {
        Seed(600);
        var vm = new HistoryViewModel(_history, _dialogs, new InlineWorkScheduler());
        Assert.Equal(500, vm.Rows.Count);
        Assert.Equal("NAME 599", vm.Rows[0].Name);   // newest first
        Assert.True(vm.CanShowAll);
        Assert.Equal("Showing the latest 500 of 600 filings", vm.FooterText);
    }

    [Fact]
    public void ShowAllLoadsEverything()
    {
        Seed(600);
        var vm = new HistoryViewModel(_history, _dialogs, new InlineWorkScheduler());
        vm.ShowAllCommand.Execute(null);
        Assert.Equal(600, vm.Rows.Count);
        Assert.False(vm.CanShowAll);
        Assert.Equal("600 of 600 filings shown", vm.FooterText);
    }

    /// <summary>UX-06: a query on a share can take seconds, and the window
    /// showed neither rows nor an empty-state line while it ran; worse,
    /// Show all was gated on "not yet shown all" only, so a second click
    /// during the slow load started a second full query. IsBusy now covers
    /// both: the window binds a Loading line to it and Show all is parked.
    /// ControlledWorkScheduler holds each load in flight until released.</summary>
    [Fact]
    public void ShowAllIsParkedWhileALoadIsInFlight()
    {
        Seed(600);
        var scheduler = new ControlledWorkScheduler();
        var vm = new HistoryViewModel(_history, _dialogs, scheduler);   // the initial load is queued

        Assert.True(vm.IsBusy);
        Assert.False(vm.ShowAllCommand.CanExecute(null));

        scheduler.ReleaseNext();
        Assert.False(vm.IsBusy);
        Assert.Equal(HistoryViewModel.InitialLoad, vm.Rows.Count);
        Assert.True(vm.ShowAllCommand.CanExecute(null));

        // Important 2: "No filings match your search." and "Loading…" share
        // one Grid cell in HistoryWindow.xaml, so filtering to zero matches
        // and then clicking Show all used to render both lines on top of
        // each other for the whole load.
        vm.Filter = "zzz";
        Assert.True(vm.NoMatches);

        vm.ShowAllCommand.Execute(null);
        Assert.True(vm.IsBusy);
        Assert.False(vm.NoMatches);   // cleared the instant the load starts, not overlaid on Loading…
        Assert.False(vm.ShowAllCommand.CanExecute(null));
        Assert.Equal(HistoryViewModel.InitialLoad, vm.Rows.Count);   // stale rows stay visible

        scheduler.ReleaseAll();
        vm.Filter = "";
        Assert.False(vm.IsBusy);
        Assert.Equal(600, vm.Rows.Count);
        Assert.False(vm.ShowAllCommand.CanExecute(null));   // everything is shown now
    }

    [Fact]
    public void SmallTablesNeedNoShowAll()
    {
        Seed(3);
        var vm = new HistoryViewModel(_history, _dialogs, new InlineWorkScheduler());
        Assert.Equal(3, vm.Rows.Count);
        Assert.False(vm.CanShowAll);
    }

    [Fact]
    public void FilterNarrowsAcrossColumns()
    {
        Seed(20);
        _history.LogCommit("c:\\in\\x.pdf", "x.pdf", "SMITH JOHN.pdf",
            "SMITH JOHN", "replace", "", "Statements", "c:\\out", tagged: false, "");
        var vm = new HistoryViewModel(_history, _dialogs, new InlineWorkScheduler());

        // RowsView is the filtered display the grid binds to...
        vm.Filter = "smith";
        Assert.Single(vm.RowsView.Cast<HistoryRow>());
        vm.Filter = "statements";
        Assert.Single(vm.RowsView.Cast<HistoryRow>());
        vm.Filter = "";
        Assert.Equal(21, vm.RowsView.Cast<HistoryRow>().Count());

        // ...while Rows, the master collection, is never narrowed by a filter.
        Assert.Equal(21, vm.Rows.Count);
    }

    [Fact]
    public void RevertedRowsAreFlagged()
    {
        var id = _history.LogCommit("c:\\in\\x.pdf", "x.pdf", "Y.pdf", "Y",
            "insert", "", "Invoices", "c:\\out", tagged: false, "");
        _history.MarkReverted(id);
        var vm = new HistoryViewModel(_history, _dialogs, new InlineWorkScheduler());
        Assert.True(Assert.Single(vm.Rows).Reverted);
    }

    /// <summary>2026-08-02 audit-remediation, Task 7 Step 1: the OLD Refresh()
    /// did Rows.Clear() + per-item Add() of only the FILTERED subset on every
    /// keystroke — so the master row collection itself shrank/reordered with
    /// the Find box, and the objects living in Rows right after a keystroke
    /// were not the same set (by count or reference) as what was there right
    /// before. History is the app's one unbounded-growth collection, so a
    /// per-keystroke rebuild of the whole list is the instance that matters.
    /// This asserts the real fix: Rows (whatever the grid's master collection
    /// is) holds the SAME instances, same count, same order, before and after
    /// a Filter change — filtering must narrow what's DISPLAYED without ever
    /// touching the underlying collection.</summary>
    [Fact]
    public void FilteringDoesNotRebuildTheUnderlyingRowCollection()
    {
        Seed(20);
        var vm = new HistoryViewModel(_history, _dialogs, new InlineWorkScheduler());
        var before = vm.Rows.ToList();
        Assert.Equal(20, before.Count);

        vm.Filter = "NAME 1";   // narrows what the grid displays, not the master list

        var after = vm.Rows.ToList();
        Assert.Equal(before.Count, after.Count);
        for (var i = 0; i < before.Count; i++)
            Assert.Same(before[i], after[i]);
    }

    /// <summary>2026-08-02 audit-remediation, Task 7 Step 2: a history with
    /// nothing filed yet is a genuinely different situation from a search
    /// that just came up empty, and the empty-state copy tells them apart.</summary>
    [Fact]
    public void EmptyStateWhenNoFilingsRecorded()
    {
        var vm = new HistoryViewModel(_history, _dialogs, new InlineWorkScheduler());
        Assert.True(vm.IsEmpty);
        Assert.False(vm.NoMatches);
    }

    [Fact]
    public void NoMatchesWhenFilingsExistButFilterExcludesAll()
    {
        Seed(5);
        var vm = new HistoryViewModel(_history, _dialogs, new InlineWorkScheduler());
        Assert.False(vm.IsEmpty);
        Assert.False(vm.NoMatches);

        vm.Filter = "nonexistentxyz";
        Assert.False(vm.IsEmpty);
        Assert.True(vm.NoMatches);

        vm.Filter = "";
        Assert.False(vm.NoMatches);
    }

    [Fact]
    public void ExportGoesThroughTheDialogService()
    {
        Seed(2);
        var vm = new HistoryViewModel(_history, _dialogs, new InlineWorkScheduler());
        var dest = Path.Combine(_dir, "out.csv");
        _dialogs.NextSaveFile = dest;
        vm.ExportCommand.Execute(null);
        Assert.True(File.Exists(dest));
        Assert.Single(_dialogs.Infos);
    }
}
