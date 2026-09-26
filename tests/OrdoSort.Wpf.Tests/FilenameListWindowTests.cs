using System.Linq;
using System.Windows;
using System.Windows.Controls;
using OrdoSort.Wpf.Theme;
using OrdoSort.Wpf.ViewModels;
using OrdoSort.Wpf.Windows;

namespace OrdoSort.Wpf.Tests;

[Collection(HighlightContrastTests.Name)]
public class FilenameListWindowTests
{
    private readonly HighlightContrastFixture _fx;
    public FilenameListWindowTests(HighlightContrastFixture fx) => _fx = fx;

    /// <summary>Audit FL-04. The grid shipped with WPF's own clipboard support
    /// live, so Ctrl+C emitted DataGrid's tab-separated cells with NO header
    /// row, while the Copy button emitted FilenameList.ToText — which for a
    /// table-shaped listing DOES write a header, and for a numbered list writes
    /// "1. name" rather than "1	name". Same selection, two different payloads,
    /// nothing on screen saying which one you got.
    ///
    /// Setting ClipboardCopyMode to None is what actually closes that: it takes
    /// WPF's copy out of the picture entirely, leaving the window's own Ctrl+C
    /// handler to call the same method the button calls. One payload, by
    /// construction rather than by two implementations agreeing.</summary>
    [Fact]
    public void TheGridDoesNotRunItsOwnClipboardCopy() => _fx.Invoke(() =>
    {
        ThemeManager.Apply(_fx.App, dark: false);
        var vm = new FilenameListViewModel(new FakeDialogs());
        var window = new FilenameListWindow(vm);
        window.Left = -20000; window.Top = 0; window.ShowActivated = false;
        window.WindowStartupLocation = WindowStartupLocation.Manual;
        try
        {
            window.Show();
            window.UpdateLayout();

            Assert.Equal(DataGridClipboardCopyMode.None, window.NamesGrid.ClipboardCopyMode);
        }
        finally { window.Close(); }
    });

    /// <summary>The column-visibility mechanism is imperative on purpose: a
    /// DataGridColumn is not in the visual or logical tree, so a RelativeSource
    /// binding to the view model never resolves and would fail SILENTLY, leaving
    /// every optional column permanently visible. Because the wiring is code
    /// rather than a binding, this is the test that proves it is actually wired.
    ///
    /// All FIVE flag/column pairs, not one — a single-pair version of this test
    /// (this method's own prior shape) proves the mechanism works for whichever
    /// pair it names and says nothing about the other four; a transposition
    /// among those four (say ModifiedColumn wired to ShowFullPath and vice
    /// versa) would pass every assertion such a test makes. Looping over every
    /// pair costs the same and closes that gap: each iteration turns exactly one
    /// flag on, asserts its own column comes Visible AND every other column
    /// stays exactly where it was (the "that column only" half a same-shape bug
    /// would otherwise slip past), then turns it back off and asserts Collapsed
    /// again (both directions) before moving to the next pair — so by the time
    /// a pair is checked, every earlier pair has already been reset to false and
    /// the "everyone else" check is a clean baseline, not noise left over from a
    /// prior iteration.
    ///
    /// The five *Column fields are internal (XAML's default x:Name field
    /// modifier), reachable here via OrdoSort.Wpf.csproj's InternalsVisibleTo
    /// for this test assembly.</summary>
    [Fact]
    public void TogglingEachColumnFlagShowsAndHidesThatColumnOnly() => _fx.Invoke(() =>
    {
        ThemeManager.Apply(_fx.App, dark: false);
        var vm = new FilenameListViewModel(new FakeDialogs());
        var window = new FilenameListWindow(vm);
        window.Left = -20000; window.Top = 0; window.ShowActivated = false;
        window.WindowStartupLocation = WindowStartupLocation.Manual;
        try
        {
            window.Show();
            window.UpdateLayout();

            var pairs = new (Action<bool> Set, DataGridColumn Column, string Name)[]
            {
                (v => vm.ShowNumber = v, window.NumberColumn, nameof(vm.ShowNumber)),
                (v => vm.ShowSize = v, window.SizeColumn, nameof(vm.ShowSize)),
                (v => vm.ShowModified = v, window.ModifiedColumn, nameof(vm.ShowModified)),
                (v => vm.ShowFolder = v, window.FolderColumn, nameof(vm.ShowFolder)),
                (v => vm.ShowFullPath = v, window.FullPathColumn, nameof(vm.ShowFullPath)),
                (v => vm.ShowPages = v, window.PagesColumn, nameof(vm.ShowPages)),
            };

            // Columns default off (FilenameListViewModel.Columns starts at
            // FilenameList.Columns.None), and the constructor's initial
            // SyncColumnVisibility call is what should have gotten every one
            // of the five to Collapsed before any toggle ever happens.
            foreach (var (_, column, name) in pairs)
                Assert.True(column.Visibility == Visibility.Collapsed,
                    $"{name}'s column should start Collapsed (Columns defaults to None)");

            foreach (var (set, column, name) in pairs)
            {
                set(true);

                Assert.True(column.Visibility == Visibility.Visible,
                    $"{name} = true should make its own column Visible");
                // "that column only": every OTHER pair's column must stay
                // exactly where it was. This is what a transposed pair
                // (e.g. ModifiedColumn actually wired to ShowFullPath)
                // cannot survive, even though it would pass a check that
                // only ever looked at the flag's own column.
                foreach (var (_, other, otherName) in pairs.Where(p => p.Column != column))
                    Assert.True(other.Visibility == Visibility.Collapsed,
                        $"{name} = true changed {otherName}'s column too — should affect only its own");

                set(false);

                Assert.True(column.Visibility == Visibility.Collapsed,
                    $"{name} = false should return its own column to Collapsed");
            }
        }
        finally
        {
            window.Close();
        }
    });

    /// <summary>The header checklist and the export share one switch: hiding
    /// Size from the header menu also drops it from the export (table rules
    /// v2, rule 4).</summary>
    [Fact]
    public void TheHeaderMenuFlipsTheSameFlagTheExportReads() => _fx.Invoke(() =>
    {
        ThemeManager.Apply(_fx.App, dark: false);
        var vm = new FilenameListViewModel(new FakeDialogs());
        var window = new FilenameListWindow(vm);
        window.Left = -20000; window.Top = 0; window.ShowActivated = false;
        window.WindowStartupLocation = WindowStartupLocation.Manual;
        try
        {
            window.Show();
            window.UpdateLayout();
            vm.ShowSize = true;
            var explorer = OrdoSort.Wpf.Views.ExplorerColumns.For(window.NamesGrid)!;
            var size = window.NamesGrid.Columns.Single(c => (string)c.Header == "Size");

            var menu = explorer.BuildHeaderMenu(size);
            menu.Items.OfType<MenuItem>().Single(i => (string)i.Header == "Size")
                .RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));

            Assert.False(vm.ShowSize);
            Assert.Equal(Visibility.Collapsed, size.Visibility);
            Assert.False(menu.Items.OfType<MenuItem>().Single(i => (string)i.Header == "File name").IsEnabled);
        }
        finally { window.Close(); }
    });

    /// <summary>Final review, Important 3: with the # column shown it sits
    /// first, but typing still jumps by file name (the anchor), as in Explorer.</summary>
    [Fact]
    public void TypingJumpsByFileNameEvenWithTheRowNumberColumnFirst() => _fx.Invoke(() =>
    {
        ThemeManager.Apply(_fx.App, dark: false);
        var vm = new FilenameListViewModel(new FakeDialogs()) { ShowNumber = true };
        foreach (var name in new[] { "alpha.pdf", "bravo.pdf" })
            vm.Rows.Add(new OrdoSort.Core.FilenameList.FileRow(name, 1024, DateTime.Today, @"C:\inbox", @"C:\inbox\" + name));
        var window = new FilenameListWindow(vm)
        {
            Left = -20000, Top = 0, ShowActivated = false,
            WindowStartupLocation = WindowStartupLocation.Manual,
        };
        try
        {
            window.Show();
            window.UpdateLayout();
            var explorer = OrdoSort.Wpf.Views.ExplorerColumns.For(window.NamesGrid)!;

            Assert.True(explorer.TypeAhead("b"));

            Assert.Equal("bravo.pdf", ((OrdoSort.Core.FilenameList.FileRow)window.NamesGrid.SelectedItem).Name);
        }
        finally { window.Close(); }
    });

    /// <summary>Final review, Important 3: the # column's value comes from
    /// the row itself, so fitting it measures the largest row number, not an
    /// empty string (which shrank it to 40px and cut "120" to "1…").</summary>
    [Fact]
    public void FittingTheRowNumberColumnMakesRoomForTheLargestNumber() => _fx.Invoke(() =>
    {
        ThemeManager.Apply(_fx.App, dark: false);
        var vm = new FilenameListViewModel(new FakeDialogs()) { ShowNumber = true };
        for (var i = 0; i < 120; i++)
            vm.Rows.Add(new OrdoSort.Core.FilenameList.FileRow($"f{i}.pdf", 1, DateTime.Today, @"C:\inbox", $@"C:\inbox{i}.pdf"));
        var window = new FilenameListWindow(vm)
        {
            Left = -20000, Top = 0, ShowActivated = false,
            WindowStartupLocation = WindowStartupLocation.Manual,
        };
        try
        {
            window.Show();
            window.UpdateLayout();
            var grid = window.NamesGrid;
            var explorer = OrdoSort.Wpf.Views.ExplorerColumns.For(grid)!;
            var widest = new System.Windows.Media.FormattedText("120", System.Globalization.CultureInfo.CurrentUICulture,
                FlowDirection.LeftToRight, new System.Windows.Media.Typeface(grid.FontFamily, FontStyles.Normal,
                    FontWeights.Normal, FontStretches.Normal), grid.FontSize, System.Windows.Media.Brushes.Black,
                System.Windows.Media.VisualTreeHelper.GetDpi(grid).PixelsPerDip).WidthIncludingTrailingWhitespace;

            Assert.True(explorer.MeasureFit(window.NumberColumn) >= widest + 24,
                $"fit {explorer.MeasureFit(window.NumberColumn)} leaves no room for \"120\" ({widest}px + 24 padding)");
        }
        finally { window.Close(); }
    });
}
