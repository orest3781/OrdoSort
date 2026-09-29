using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using OrdoSort.Core;
using OrdoSort.Wpf.Services;
using OrdoSort.Wpf.Theme;
using OrdoSort.Wpf.ViewModels;
using OrdoSort.Wpf.Windows;

namespace OrdoSort.Wpf.Tests;

/// <summary>StandardiseNamesWindow wired to its view model: a drop lands in
/// the one grid as a preview, and each control (letter case, separator,
/// date, word chips) reaches the preview through its real binding. The
/// Result column's status colours are covered with every sibling tool's in
/// DataGridNoteColourTests and DataGridSelectionContrastTests.</summary>
[Collection(HighlightContrastTests.Name)]
public class StandardiseNamesWindowTests : UiTest
{
    private readonly HighlightContrastFixture _fx;
    public StandardiseNamesWindowTests(HighlightContrastFixture fx) : base(fx) => _fx = fx;

    private StandardiseNamesWindow Show(StandardiseNamesViewModel vm, bool atMinWidth = false)
    {
        ThemeManager.Apply(_fx.App, dark: false);
        var window = new StandardiseNamesWindow(vm)
        {
            Left = -20000, Top = 0, ShowActivated = false,
            WindowStartupLocation = WindowStartupLocation.Manual,
        };
        if (atMinWidth) window.Width = window.MinWidth;
        window.Show();
        window.UpdateLayout();
        PumpRender();
        window.UpdateLayout();
        return window;
    }

    private static StandardiseNamesViewModel NewViewModel(IWorkScheduler? scheduler = null) =>
        new(scheduler ?? new InlineWorkScheduler()) { DateText = "20260115" };

    [Fact]
    public void OneGridNoTabsAndADroppedFileShowsItsNewName()
    {
        using var dir = new TempDir();
        var src = dir.File("smith, john.pdf");
        var vm = NewViewModel();
        _fx.Invoke(() =>
        {
            var window = Show(vm);
            try
            {
                var content = (DependencyObject)window.Content;
                Assert.Empty(Descendants<TabControl>(content));
                var grid = Assert.Single(Descendants<DataGrid>(content));
                Assert.Same(vm.Results, grid.ItemsSource);

                window.AcceptDrop(new DataObject(DataFormats.FileDrop, new[] { src }));

                var row = Assert.Single(vm.Results);
                Assert.Equal("20260115-SMITH-JOHN.pdf", row.Result);
                Assert.True(File.Exists(src), "a drop previews; only Rename renames");
                Assert.True(window.RenameButton.IsEnabled);
            }
            finally { window.Close(); }
        });
    }

    [Fact]
    public void TheDropDownsReachThePreview()
    {
        using var dir = new TempDir();
        var src = dir.File("smith john.pdf");
        var vm = NewViewModel();
        _fx.Invoke(() =>
        {
            var window = Show(vm);
            try
            {
                window.AcceptDrop(new DataObject(DataFormats.FileDrop, new[] { src }));

                window.CaseBox.SelectedValue = NameCase.Title;
                window.SeparatorBox.SelectedValue = BulkRename.SegmentJoin.Space;
                window.DatePlacementBox.SelectedValue = DatePlacement.Back;

                Assert.Equal("Smith John 20260115.pdf", Assert.Single(vm.Results).Result);
                Assert.Equal("Title Case", ((StandardiseChoice<NameCase>)window.CaseBox.SelectedItem).Label);
            }
            finally { window.Close(); }
        });
    }

    [Fact]
    public void WithNoDateTheDateControlsAreOff()
    {
        var vm = NewViewModel();
        _fx.Invoke(() =>
        {
            var window = Show(vm);
            try
            {
                Assert.True(window.DateBox.IsEnabled);

                window.DatePlacementBox.SelectedValue = DatePlacement.None;
                window.UpdateLayout();

                Assert.False(window.DateBox.IsEnabled);
                Assert.False(window.DateSourceBox.IsEnabled);
            }
            finally { window.Close(); }
        });
    }

    [Fact]
    public void ABadDateSaysWhyAndHoldsRenameBack()
    {
        using var dir = new TempDir();
        var src = dir.File("smith.pdf");
        var vm = NewViewModel();
        _fx.Invoke(() =>
        {
            var window = Show(vm);
            try
            {
                window.AcceptDrop(new DataObject(DataFormats.FileDrop, new[] { src }));

                window.DateBox.Text = "2026";
                window.UpdateLayout();

                Assert.Equal("2026", vm.DateText);
                var error = Descendants<TextBlock>(window).Single(t => t.Text == vm.DateError);
                Assert.Equal(Visibility.Visible, error.Visibility);
                Assert.False(window.RenameButton.IsEnabled);

                window.DateBox.Text = "20251231";
                window.UpdateLayout();

                Assert.Equal(Visibility.Collapsed, error.Visibility);
                Assert.True(window.RenameButton.IsEnabled);
                Assert.Equal("20251231-SMITH.pdf", Assert.Single(vm.Results).Result);
            }
            finally { window.Close(); }
        });
    }

    /// <summary>Through a real grid selection, not by setting SelectedRows:
    /// that would prove the view model reacts, not that the grid reaches it.</summary>
    [Fact]
    public void SelectingARowShowsItsWordsAndAChipDropsOne()
    {
        using var dir = new TempDir();
        var first = dir.File("smith john.pdf");
        var second = dir.File("jones mary scan.pdf");
        var vm = NewViewModel();
        _fx.Invoke(() =>
        {
            var window = Show(vm);
            try
            {
                window.AcceptDrop(new DataObject(DataFormats.FileDrop, new[] { first, second }));

                window.ResultsGrid.SelectedIndex = 1;
                window.UpdateLayout();

                var chips = Descendants<ToggleButton>(window.ChipsList).ToList();
                Assert.Equal(3, chips.Count);
                Assert.All(chips, chip => Assert.True(chip.IsChecked));

                chips[2].IsChecked = false;

                Assert.Equal("20260115-SMITH-JOHN.pdf", vm.Results[0].Result);
                Assert.Equal("20260115-JONES-MARY.pdf", vm.Results[1].Result);
            }
            finally { window.Close(); }
        });
    }

    /// <summary>Holds work until released; before <see cref="Hold"/> it runs
    /// work inline, so a test can set up without waiting.</summary>
    private sealed class HoldingScheduler : IWorkScheduler
    {
        private readonly ManualResetEventSlim _gate = new(false);
        private volatile bool _holding;
        private int _held;
        public int Held => _held;

        public void Hold() => _holding = true;
        public void Release() => _gate.Set();

        public Task<T> Run<T>(Func<T> work)
        {
            if (!_holding) return Task.FromResult(work());
            Interlocked.Increment(ref _held);
            return Task.Run(() =>
            {
                _gate.Wait();
                return work();
            });
        }

        public Task Run(Action work) => Run(() => { work(); return true; });
    }

    /// <summary>A rename is a handful of moves in one folder, so the window
    /// refuses to close while it runs rather than cancelling it part way.
    /// Rename is started inside _fx.Invoke: IsBusy raises CanExecuteChanged
    /// on commands bound to buttons the fixture's thread owns.</summary>
    [Fact]
    public async Task ClosingWhileRenamingIsRefusedThenSucceedsOnceItFinishes()
    {
        using var dir = new TempDir();
        var src = dir.File("smith, john.pdf");
        var scheduler = new HoldingScheduler();
        var vm = NewViewModel(scheduler);

        StandardiseNamesWindow? window = null;
        _fx.Invoke(() =>
        {
            window = Show(vm);
            window.AcceptDrop(new DataObject(DataFormats.FileDrop, new[] { src }));
            Assert.Single(vm.Results);
            scheduler.Hold();
            vm.RenameCommand.Execute(null);
        });

        var deadline = DateTime.UtcNow.AddSeconds(3);
        while (scheduler.Held == 0 && DateTime.UtcNow < deadline) await Task.Delay(5);
        Assert.True(scheduler.Held > 0, "the rename should have been handed to the scheduler");

        _fx.Invoke(() =>
        {
            window!.Close();
            Assert.True(window.IsVisible, "closing mid-rename must be refused");
        });
        Assert.Contains("wait", vm.Status, StringComparison.OrdinalIgnoreCase);

        scheduler.Release();
        deadline = DateTime.UtcNow.AddSeconds(3);
        while (vm.IsBusy && DateTime.UtcNow < deadline) await Task.Delay(5);
        Assert.False(vm.IsBusy, "the rename should have finished once released");
        Assert.False(File.Exists(src));

        _fx.Invoke(() =>
        {
            window!.Close();
            Assert.False(window.IsVisible, "closing once idle should succeed");
        });
    }

    /// <summary>At MinWidth, the toolbar's buttons once painted across a
    /// long AddNote caption while nothing escaped the window, which
    /// WindowOverflowTests cannot see. The buttons are declared first
    /// (Dock="Left"); this checks the two on-screen rectangles don't meet.</summary>
    [Fact]
    public void ToolbarButtonsDoNotOverlapALongNoteAtMinWidth() => _fx.Invoke(() =>
    {
        using var dir = new TempDir();
        var first = dir.File("smith, john_A12345.pdf");
        var second = dir.File("jones-report.pdf");
        var missing = Path.Combine(dir.Path, "does-not-exist-anymore.pdf");
        var duplicate = Path.Combine(dir.Path, "SMITH, JOHN_A12345.PDF");   // case-only dup of `first`
        var vm = NewViewModel();
#pragma warning disable xUnit1031 // safe: InlineWorkScheduler runs every awaited step synchronously
        vm.AddFilesAsync(new[] { first, second, missing, duplicate }).GetAwaiter().GetResult();
#pragma warning restore xUnit1031
        Assert.True(vm.AddNote.Length > 20, $"precondition: AddNote should be long — got \"{vm.AddNote}\"");

        var win = Show(vm, atMinWidth: true);
        try
        {
            var clearButton = Descendants<Button>(win).First(b => Equals(b.Content, "Clear list"));
            var noteText = Descendants<TextBlock>(win).First(t => t.Text == vm.AddNote);
            var buttonRight = clearButton.TransformToAncestor(win)
                .TransformBounds(new Rect(0, 0, clearButton.ActualWidth, clearButton.ActualHeight)).Right;
            var noteLeft = noteText.TransformToAncestor(win)
                .TransformBounds(new Rect(0, 0, noteText.ActualWidth, noteText.ActualHeight)).Left;

            Assert.True(buttonRight <= noteLeft + 0.5,
                $"Clear list (right edge {buttonRight}px) overlaps AddNote (left edge {noteLeft}px)");
        }
        finally { win.Close(); }
    });
}
