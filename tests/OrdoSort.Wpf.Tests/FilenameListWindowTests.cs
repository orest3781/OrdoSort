using System.Linq;
using System.Windows;
using System.Windows.Controls;
using OrdoSort.Wpf.Theme;
using OrdoSort.Wpf.ViewModels;
using OrdoSort.Wpf.Windows;

namespace OrdoSort.Wpf.Tests;

[Collection(HighlightContrastTests.Name)]
public class FilenameListWindowTests : UiTest
{
    private readonly HighlightContrastFixture _fx;
    public FilenameListWindowTests(HighlightContrastFixture fx) : base(fx) => _fx = fx;

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
    /// <summary>FL-18: a failed save showed "Couldn't save: Access to the
    /// path 'C:\…" cut off, with no way to read the rest. The status line
    /// shows the whole text as a tooltip once it is cut off.</summary>
    [Fact]
    public void ACutOffStatusCanBeReadInFull() => _fx.Invoke(() =>
    {
        ThemeManager.Apply(_fx.App, dark: false);
        var vm = new FilenameListViewModel(new FakeDialogs(), scheduler: new InlineWorkScheduler());
        var window = new FilenameListWindow(vm);
        window.Left = -20000; window.Top = 0; window.ShowActivated = false;
        window.WindowStartupLocation = WindowStartupLocation.Manual;
        try
        {
            window.Show();
            window.UpdateLayout();

            var status = Ui.Descendants<TextBlock>(window).Single(t =>
                System.Windows.Data.BindingOperations.GetBinding(t, TextBlock.TextProperty)?.Path.Path == "Status");
            Assert.True(OrdoSort.Wpf.Views.TrimmedTextTooltip.GetEnabled(status));
        }
        finally { window.Close(); }
    });

    [Fact]
    public void TheGridDoesNotRunItsOwnClipboardCopy() => _fx.Invoke(() =>
    {
        ThemeManager.Apply(_fx.App, dark: false);
        var vm = new FilenameListViewModel(new FakeDialogs(), scheduler: new InlineWorkScheduler());
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

    private FilenameListWindow OpenOffScreen(FilenameListViewModel vm)
    {
        var window = new FilenameListWindow(vm, _ => { })
        {
            Left = -20000, Top = 0, ShowActivated = false,
            WindowStartupLocation = WindowStartupLocation.Manual,
        };
        window.Show();
        window.UpdateLayout();
        return window;
    }

    private static System.Windows.Media.Color ColourOf(System.Windows.Media.Brush brush) =>
        ((System.Windows.Media.SolidColorBrush)brush).Color;

    /// <summary>FL-11: the File list's headers lit up under the mouse like
    /// every sortable header in the app, but clicking them does nothing — the
    /// # column is the row order, so this table deliberately has no header
    /// sorting. Its headers no longer light up; a sortable table's still do.</summary>
    [Fact]
    public void TheHeadersDoNotLightUpAsIfClickingWouldSort() => _fx.Invoke(() =>
    {
        ThemeManager.Apply(_fx.App, dark: false);
        var hover = ColourOf((System.Windows.Media.Brush)_fx.App.FindResource("Theme.SurfaceHover"));
        var window = OpenOffScreen(new FilenameListViewModel(new FakeDialogs(), scheduler: new InlineWorkScheduler()));
        var sortable = new Window
        {
            Left = -20000, Top = 0, ShowActivated = false, Width = 300, Height = 200,
            WindowStartupLocation = WindowStartupLocation.Manual,
            Content = new DataGrid { Columns = { new DataGridTextColumn { Header = "Name" } } },
        };
        try
        {
            var header = Ui.Descendants<System.Windows.Controls.Primitives.DataGridColumnHeader>(window)
                .Single(h => h.Column == window.FileNameColumn);
            Ui.ForceMouseOver(header, true);
            Assert.NotEqual(hover, ColourOf(header.Background));

            sortable.Show();
            sortable.UpdateLayout();
            var sortableHeader = Ui.Descendants<System.Windows.Controls.Primitives.DataGridColumnHeader>(sortable)
                .Single(h => h.Column is not null);
            Ui.ForceMouseOver(sortableHeader, true);
            Assert.Equal(hover, ColourOf(sortableHeader.Background));
        }
        finally
        {
            window.Close();
            sortable.Close();
        }
    });

    /// <summary>FL-12: the order was one checkbox, "Z to A", whose unticked
    /// state never said "A to Z" anywhere. It is a two-way choice now, and
    /// the current order is always the one marked.</summary>
    [Fact]
    public void TheOrderIsAChoiceBetweenAToZAndZToA() => _fx.Invoke(() =>
    {
        ThemeManager.Apply(_fx.App, dark: false);
        var vm = new FilenameListViewModel(new FakeDialogs(), scheduler: new InlineWorkScheduler());
        var window = OpenOffScreen(vm);
        try
        {
            var radios = Ui.Descendants<RadioButton>(window);
            var aToZ = radios.Single(r => (string)r.Content == "A to Z");
            var zToA = radios.Single(r => (string)r.Content == "Z to A");
            Assert.True(aToZ.IsChecked);
            Assert.False(zToA.IsChecked);
            Assert.DoesNotContain(Ui.Descendants<CheckBox>(window), c => (c.Content as string) == "Z to A");

            zToA.IsChecked = true;

            Assert.True(vm.Descending);
            Assert.False(aToZ.IsChecked);

            aToZ.IsChecked = true;

            Assert.False(vm.Descending);
        }
        finally { window.Close(); }
    });

    /// <summary>FL-13: removing rows — the curation that makes this more than
    /// a dir listing — was only on the right-click menu and the Delete key.
    /// It has a button on the toolbar, next to Restore, whose text says how
    /// many rows Restore brings back (FL-15).</summary>
    [Fact]
    public void RemoveSelectedAndRestoreAreButtonsOnTheToolbar() => _fx.Invoke(() =>
    {
        ThemeManager.Apply(_fx.App, dark: false);
        var vm = new FilenameListViewModel(new FakeDialogs(), scheduler: new InlineWorkScheduler());
        var window = OpenOffScreen(vm);
        try
        {
            var buttons = Ui.Descendants<Button>(window);
            var remove = buttons.Single(b => b.Command == vm.RemoveSelectedCommand);
            Assert.Equal("Remove selected", remove.Content);
            var restore = buttons.Single(b => b.Command == vm.RestoreRemovedCommand);
            Assert.Equal(vm.RestoreLabel, restore.Content);
            // FL-14: Copy is a click handler, not a command, so it is gated by binding
            Assert.False(buttons.Single(b => (b.Content as string) == "Copy to clipboard").IsEnabled);
        }
        finally { window.Close(); }
    });

    /// <summary>DW-31 (FL-04's other half): Ctrl+C in the grid must copy the
    /// same text the Copy button does. Nothing tested it, so the branch could
    /// be deleted and Ctrl+C would silently copy nothing.</summary>
    [Fact]
    public void CtrlCInTheGridCopiesWhatTheCopyButtonCopies() => _fx.Invoke(() =>
    {
        ThemeManager.Apply(_fx.App, dark: false);
        var vm = new FilenameListViewModel(new FakeDialogs(), scheduler: new InlineWorkScheduler());
        foreach (var name in new[] { "alpha.pdf", "bravo.pdf" })
            vm.Rows.Add(new OrdoSort.Core.FilenameList.FileRow(name, 1, DateTime.Today, "", @"C:\inbox\" + name));
        var copied = new List<string>();
        var window = new FilenameListWindow(vm, copied.Add)
        {
            Left = -20000, Top = 0, ShowActivated = false,
            WindowStartupLocation = WindowStartupLocation.Manual,
        };
        try
        {
            window.Show();
            window.UpdateLayout();

            var used = window.HandleGridKey(System.Windows.Input.Key.C, System.Windows.Input.ModifierKeys.Control);

            Assert.True(used);
            Assert.Equal(new[] { "alpha.pdf" + Environment.NewLine + "bravo.pdf" }, copied);
            Assert.Equal("Copied 2 names", vm.Status);
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
        var vm = new FilenameListViewModel(new FakeDialogs(), scheduler: new InlineWorkScheduler());
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
        var vm = new FilenameListViewModel(new FakeDialogs(), scheduler: new InlineWorkScheduler());
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
        var vm = new FilenameListViewModel(new FakeDialogs(), scheduler: new InlineWorkScheduler()) { ShowNumber = true };
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
        var vm = new FilenameListViewModel(new FakeDialogs(), scheduler: new InlineWorkScheduler()) { ShowNumber = true };
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
