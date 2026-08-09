using OrdoSort.Core;
using OrdoSort.Wpf.ViewModels;
using PdfSharp.Pdf;

namespace OrdoSort.Wpf.Tests;

/// <summary>Task 3 (PDF page counts tool). InlineWorkScheduler resolves every
/// _scheduler.Run call — and, since MaxConcurrentCounts never actually
/// contends for a handful of test files, every _countGate.WaitAsync call too
/// — synchronously on the calling thread, so unlike FilenameListViewModelTests
/// (whose DebouncedProbe genuinely posts through a real Timer) these tests
/// can just await AddFilesAsync directly: no WaitFor polling needed.</summary>
public class PageCountsViewModelTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "ordopagecounts_" + Guid.NewGuid());

    public PageCountsViewModelTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { /* best effort */ }
    }

    /// <summary>Same shape as FilenameListViewModelTests' Touch: supports a
    /// nested relative path so a test can build a small subfolder.</summary>
    private string Touch(string relative)
    {
        var path = Path.Combine(_dir, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "x");
        return path;
    }

    private static PageCountsViewModel MakeVm(FakeDialogs dialogs,
        Func<string, PageCounts.CountResult>? counter = null) =>
        new(dialogs, new InlineWorkScheduler(), uiContext: null, counter);

    [Fact]
    public async Task RowsFillWithTheScriptedCounterResults()
    {
        var a = Touch("a.pdf");
        var b = Touch("b.pdf");
        var vm = MakeVm(new FakeDialogs(), path =>
            new PageCounts.CountResult(path, path == a ? 3 : 5));

        await vm.AddFilesAsync(new[] { _dir });

        Assert.Equal(2, vm.Rows.Count);
        var rowA = Assert.Single(vm.Rows, r => r.Path == a);
        var rowB = Assert.Single(vm.Rows, r => r.Path == b);
        Assert.Equal(3, rowA.Pages);
        Assert.Equal("", rowA.Note);
        Assert.False(rowA.Pending);
        Assert.Equal(5, rowB.Pages);
        Assert.False(rowB.Pending);
    }

    [Fact]
    public async Task AScriptedErrorRowDoesNotStopTheRestOfTheBatch()
    {
        var ok1 = Touch("ok1.pdf");
        var bad = Touch("bad.pdf");
        var ok2 = Touch("ok2.pdf");
        var vm = MakeVm(new FakeDialogs(), path =>
            path == bad
                ? new PageCounts.CountResult(path, null, "password-protected or unreadable — couldn't count")
                : new PageCounts.CountResult(path, 2));

        await vm.AddFilesAsync(new[] { _dir });

        // all three rows landed and finished, including the two AFTER the
        // scripted failure — one bad row never aborted the batch
        Assert.Equal(3, vm.Rows.Count);
        Assert.All(vm.Rows, r => Assert.False(r.Pending));
        var badRow = Assert.Single(vm.Rows, r => r.Path == bad);
        Assert.Null(badRow.Pages);
        Assert.Contains("unreadable", badRow.Note);
        Assert.Contains("1 unreadable", vm.TotalLine);
        Assert.Equal(4, vm.Rows.Where(r => r.Pages.HasValue).Sum(r => r.Pages)!.Value);
    }

    [Fact]
    public async Task TotalLineArithmeticAndPluralization()
    {
        // Singular: one PDF, one page, nothing unreadable.
        var single = Touch("single.pdf");
        var vm1 = MakeVm(new FakeDialogs(), _ => new PageCounts.CountResult(single, 1));
        await vm1.AddFilesAsync(new[] { single });
        Assert.Equal("1 PDF · 1 page", vm1.TotalLine);

        // Plural PDFs/pages, with one unreadable row folded in.
        var m1 = Touch(Path.Combine("multi", "m1.pdf"));
        var m2 = Touch(Path.Combine("multi", "m2.pdf"));
        var vm2 = MakeVm(new FakeDialogs(), path =>
            path == m2
                ? new PageCounts.CountResult(path, null, "file not found")
                : new PageCounts.CountResult(path, 9));
        await vm2.AddFilesAsync(new[] { Path.Combine(_dir, "multi") });
        Assert.Equal("2 PDFs · 9 pages · 1 unreadable", vm2.TotalLine);
    }

    [Fact]
    public async Task OutputTextHasTabsABlankLineThenTotal()
    {
        var ok = Touch("ok.pdf");
        var bad = Touch("bad.pdf");
        var vm = MakeVm(new FakeDialogs(), path =>
            path == bad
                ? new PageCounts.CountResult(path, null, "file not found")
                : new PageCounts.CountResult(path, 4));

        await vm.AddFilesAsync(new[] { _dir });

        // NaturalSort over full path puts "bad.pdf" before "ok.pdf".
        var expected = string.Join(Environment.NewLine, new[]
        {
            "bad.pdf\tfile not found",
            "ok.pdf\t4",
            "",
            "Total\t4",
        });
        Assert.Equal(expected, vm.OutputText);
    }

    [Fact]
    public async Task NonPdfDropAddsANoteNotARow()
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
        var a = Touch("a.pdf");
        var vm = MakeVm(new FakeDialogs(), path => new PageCounts.CountResult(path, 1));

        await vm.AddFilesAsync(new[] { a });
        Assert.Single(vm.Rows);
        Assert.Equal("", vm.AddNote);

        await vm.AddFilesAsync(new[] { a });   // same file again
        Assert.Single(vm.Rows);
        Assert.Contains("already listed", vm.AddNote);
    }

    [Fact]
    public async Task RemoveSelectedRemovesExactlyTheGivenRows()
    {
        var a = Touch("a.pdf");
        var b = Touch("b.pdf");
        var vm = MakeVm(new FakeDialogs(), path => new PageCounts.CountResult(path, 1));
        await vm.AddFilesAsync(new[] { _dir });
        Assert.Equal(2, vm.Rows.Count);

        var toRemove = vm.Rows.Where(r => r.Path == a).ToList();
        vm.RemoveSelected(toRemove);

        var remaining = Assert.Single(vm.Rows);
        Assert.Equal(b, remaining.Path);
    }

    [Fact]
    public async Task ClearEmptiesRowsAndResetsTotals()
    {
        var a = Touch("a.pdf");
        var vm = MakeVm(new FakeDialogs(), path => new PageCounts.CountResult(path, 2));
        await vm.AddFilesAsync(new[] { a });
        Assert.Single(vm.Rows);

        vm.ClearCommand.Execute(null);

        Assert.Empty(vm.Rows);
        Assert.Equal("", vm.TotalLine);
        Assert.Equal("", vm.AddNote);
        Assert.Equal("", vm.Status);
    }

    [Fact]
    public async Task SaveCommandWritesTheOutputTextWhenAPathIsChosen()
    {
        var a = Touch("a.pdf");
        var savePath = Path.Combine(_dir, "out.txt");
        var dialogs = new FakeDialogs { NextSaveFile = savePath };
        var vm = MakeVm(dialogs, path => new PageCounts.CountResult(path, 2));
        await vm.AddFilesAsync(new[] { a });

        vm.SaveCommand.Execute(null);

        // InlineWorkScheduler runs the write synchronously, so no polling is
        // needed — Save()'s fire-and-forget SaveAsync() runs to completion
        // inline because nothing it awaits ever actually suspends.
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
    public async Task RealCounterSmokeTestAgainstATwoPagePdf()
    {
        var path = Path.Combine(_dir, "real.pdf");
        using (var doc = new PdfDocument())
        {
            doc.AddPage();
            doc.AddPage();
            doc.Save(path);
        }
        var vm = MakeVm(new FakeDialogs());   // default counter: the real PageCounts.Count

        await vm.AddFilesAsync(new[] { path });

        var row = Assert.Single(vm.Rows);
        Assert.Equal(2, row.Pages);
        Assert.Equal("", row.Note);
        Assert.False(row.Pending);
    }
}
