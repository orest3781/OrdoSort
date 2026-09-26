using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Threading;
using OrdoSort.Wpf.Services;
using OrdoSort.Wpf.Theme;
using OrdoSort.Wpf.Views;

namespace OrdoSort.Wpf.Tests;

/// <summary>Table rules v2: tables behave like File Explorer's Details
/// view. A bare grid with three text columns, so each fact is about the
/// behaviour and not about one window.</summary>
[Collection(HighlightContrastTests.Name)]
public sealed class ExplorerColumnsTests : IDisposable
{
    private readonly HighlightContrastFixture _fx;
    private readonly string _dir = Directory.CreateDirectory(
        Path.Combine(Path.GetTempPath(), "ordo_explorer_" + Guid.NewGuid().ToString("N"))).FullName;

    public ExplorerColumnsTests(HighlightContrastFixture fx) => _fx = fx;

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { /* best effort */ }
    }

    public sealed class Row
    {
        public string Name { get; init; } = "";
        public string Kind { get; init; } = "";
        public string Note { get; init; } = "";
    }

    internal sealed record Bed(Window Window, DataGrid Grid, ObservableCollection<Row> Rows, ExplorerColumns Explorer,
        TableLayoutStore Store)
    {
        public DataGridColumn Column(string header) => Grid.Columns.Single(c => (string)c.Header == header);
    }

    internal Bed Build(double windowWidth = 900, TableLayoutStore? store = null, Func<DateTime>? clock = null,
        params Row[] rows)
    {
        var items = new ObservableCollection<Row>(rows);
        var grid = new DataGrid { ItemsSource = items, AutoGenerateColumns = false, CanUserAddRows = false };
        foreach (var (header, width) in new[] { ("Name", 200.0), ("Kind", 90.0), ("Note", 150.0) })
            grid.Columns.Add(new DataGridTextColumn
            {
                Header = header, Binding = new Binding(header), Width = new DataGridLength(width),
                ElementStyle = (Style)_fx.App.FindResource("GridCellText"),
            });
        store ??= new TableLayoutStore(Path.Combine(_dir, "table-columns.json"));
        var explorer = ExplorerColumns.Attach(grid, "Test", store: store, clock: clock);
        var window = new Window
        {
            Width = windowWidth, Height = 400, Content = grid,
            WindowStartupLocation = WindowStartupLocation.Manual, Left = -20000, Top = 0, ShowActivated = false,
        };
        return new Bed(window, grid, items, explorer, store);
    }

    internal static void Settle(Window window)
    {
        if (!window.IsVisible) window.Show();
        window.UpdateLayout();
        Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
        window.UpdateLayout();
    }

    internal static T? FindDescendant<T>(DependencyObject root) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T match) return match;
            if (FindDescendant<T>(child) is { } nested) return nested;
        }
        return null;
    }

    internal static Row Long(int i) => new() { Name = new string('W', 60) + i, Kind = "pdf", Note = "" };

    // ---- rule 1: fixed widths --------------------------------------------

    [Fact]
    public void ColumnsOpenAtTheirDeclaredWidths() => _fx.Invoke(() =>
    {
        ThemeManager.Apply(_fx.App, dark: false);
        var bed = Build(rows: new Row { Name = "a", Kind = "pdf" });
        try
        {
            Settle(bed.Window);

            Assert.Equal(200, bed.Column("Name").ActualWidth, 0.5);
            Assert.Equal(90, bed.Column("Kind").ActualWidth, 0.5);
            Assert.Equal(150, bed.Column("Note").ActualWidth, 0.5);
        }
        finally { bed.Window.Close(); }
    });

    [Fact]
    public void AWidthNeverChangesWhenRowsArriveOrTheWindowResizes() => _fx.Invoke(() =>
    {
        ThemeManager.Apply(_fx.App, dark: false);
        var bed = Build(rows: new Row { Name = "a", Kind = "pdf" });
        try
        {
            Settle(bed.Window);
            bed.Column("Name").Width = new DataGridLength(260);   // as a drag leaves it

            for (var i = 0; i < 200; i++) bed.Rows.Add(Long(i));
            bed.Window.Width = 600;
            Settle(bed.Window);

            Assert.Equal(260, bed.Column("Name").ActualWidth, 0.5);
            Assert.Equal(90, bed.Column("Kind").ActualWidth, 0.5);
        }
        finally { bed.Window.Close(); }
    });

    [Fact]
    public void ColumnsWiderThanTheTableScrollSideways() => _fx.Invoke(() =>
    {
        ThemeManager.Apply(_fx.App, dark: false);
        var bed = Build(windowWidth: 300, rows: new Row { Name = "a", Kind = "pdf" });
        try
        {
            Settle(bed.Window);

            var scroller = FindDescendant<ScrollViewer>(bed.Grid)!;
            Assert.Equal(Visibility.Visible, scroller.ComputedHorizontalScrollBarVisibility);
        }
        finally { bed.Window.Close(); }
    });

    [Fact]
    public void AColumnCannotBeDraggedNarrowerThanTheMinimum() => _fx.Invoke(() =>
    {
        ThemeManager.Apply(_fx.App, dark: false);
        var bed = Build(rows: new Row { Name = "a" });
        try
        {
            Settle(bed.Window);
            bed.Column("Kind").Width = new DataGridLength(5);
            Settle(bed.Window);

            Assert.Equal(ExplorerColumns.MinColumnWidth, bed.Column("Kind").ActualWidth, 0.5);
        }
        finally { bed.Window.Close(); }
    });

    // ---- rule 5: reorder, first column stays first --------------------------

    [Fact]
    public void TheFirstColumnStaysFirstAfterAReorder() => _fx.Invoke(() =>
    {
        ThemeManager.Apply(_fx.App, dark: false);
        var bed = Build(rows: new Row { Name = "a" });
        try
        {
            Settle(bed.Window);

            bed.Column("Note").DisplayIndex = 0;   // what dropping Note in front of Name does
            bed.Explorer.KeepAnchorFirst();

            Assert.Equal(0, bed.Column("Name").DisplayIndex);
            Assert.Equal(1, bed.Column("Note").DisplayIndex);
        }
        finally { bed.Window.Close(); }
    });

    [Fact]
    public void OtherColumnsCanBeReordered() => _fx.Invoke(() =>
    {
        ThemeManager.Apply(_fx.App, dark: false);
        var bed = Build(rows: new Row { Name = "a" });
        try
        {
            Settle(bed.Window);

            bed.Column("Note").DisplayIndex = 1;
            bed.Explorer.KeepAnchorFirst();

            Assert.Equal(new[] { "Name", "Note", "Kind" },
                bed.Grid.Columns.OrderBy(c => c.DisplayIndex).Select(c => (string)c.Header));
        }
        finally { bed.Window.Close(); }
    });

    // ---- rule 7: remembered ------------------------------------------------

    [Fact]
    public void WidthsOrderHiddenColumnsAndSortComeBackInANewWindow() => _fx.Invoke(() =>
    {
        ThemeManager.Apply(_fx.App, dark: false);
        var first = Build(rows: new Row { Name = "a" });
        try
        {
            Settle(first.Window);
            first.Column("Name").Width = new DataGridLength(310);
            first.Column("Note").DisplayIndex = 1;
            first.Column("Kind").Visibility = Visibility.Collapsed;
            first.Column("Name").SortDirection = ListSortDirection.Descending;
            first.Grid.Items.SortDescriptions.Add(new SortDescription("Name", ListSortDirection.Descending));
            Settle(first.Window);
        }
        finally { first.Window.Close(); }   // closing saves

        var second = Build(store: first.Store, rows: new[] { new Row { Name = "a" }, new Row { Name = "b" } });
        try
        {
            Settle(second.Window);

            Assert.Equal(310, second.Column("Name").ActualWidth, 0.5);
            Assert.Equal(1, second.Column("Note").DisplayIndex);
            Assert.Equal(Visibility.Collapsed, second.Column("Kind").Visibility);
            Assert.Equal(ListSortDirection.Descending, second.Column("Name").SortDirection);
            Assert.Equal("b", ((Row)second.Grid.Items[0]).Name);
        }
        finally { second.Window.Close(); }
    });

    [Fact]
    public void ADamagedLayoutFileOpensAtTheDefaults() => _fx.Invoke(() =>
    {
        ThemeManager.Apply(_fx.App, dark: false);
        var path = Path.Combine(_dir, "table-columns.json");
        File.WriteAllText(path, "{ not json");
        var bed = Build(store: new TableLayoutStore(path), rows: new Row { Name = "a" });
        try
        {
            Settle(bed.Window);

            Assert.Equal(200, bed.Column("Name").ActualWidth, 0.5);
        }
        finally { bed.Window.Close(); }
    });

    /// <summary>Review focus 2: a saved column the grid no longer has is
    /// ignored, and a column the save doesn't mention keeps its XAML width.</summary>
    [Fact]
    public void UnknownSavedColumnsAreIgnoredAndNewColumnsKeepTheirWidth() => _fx.Invoke(() =>
    {
        ThemeManager.Apply(_fx.App, dark: false);
        var store = new TableLayoutStore(Path.Combine(_dir, "table-columns.json"));
        store.Save("Test", new TableLayout(new[]
        {
            new ColumnLayout("Gone", 500, true, 0),
            new ColumnLayout("Name", 222, true, 0),
        }, "Gone", ListSortDirection.Ascending));
        var bed = Build(store: store, rows: new Row { Name = "a" });
        try
        {
            Settle(bed.Window);

            Assert.Equal(222, bed.Column("Name").ActualWidth, 0.5);
            Assert.Equal(90, bed.Column("Kind").ActualWidth, 0.5);
            Assert.All(bed.Grid.Columns, c => Assert.Null(c.SortDirection));
        }
        finally { bed.Window.Close(); }
    });

    /// <summary>Review focus 3: a hand-edited file with display indexes out of
    /// range or repeated must not throw.</summary>
    [Fact]
    public void OutOfRangeOrRepeatedDisplayIndexesAreClamped() => _fx.Invoke(() =>
    {
        ThemeManager.Apply(_fx.App, dark: false);
        var store = new TableLayoutStore(Path.Combine(_dir, "table-columns.json"));
        store.Save("Test", new TableLayout(new[]
        {
            new ColumnLayout("Name", 200, true, 7),
            new ColumnLayout("Kind", 90, true, -3),
            new ColumnLayout("Note", 150, true, 1),
        }, null, null));
        var bed = Build(store: store, rows: new Row { Name = "a" });
        try
        {
            Settle(bed.Window);

            Assert.Equal(new[] { 0, 1, 2 }, bed.Grid.Columns.Select(c => c.DisplayIndex).OrderBy(i => i));
            Assert.Equal(0, bed.Column("Name").DisplayIndex);   // the anchor stays first
        }
        finally { bed.Window.Close(); }
    });

    /// <summary>Review focus 1: a column whose header isn't text has no
    /// identity to save under, and is left alone.</summary>
    [Fact]
    public void AColumnWithANonTextHeaderIsSkipped() => _fx.Invoke(() =>
    {
        ThemeManager.Apply(_fx.App, dark: false);
        var bed = Build(rows: new Row { Name = "a" });
        bed.Grid.Columns.Add(new DataGridTextColumn { Header = new TextBlock { Text = "Odd" }, Width = new DataGridLength(70) });
        try
        {
            Settle(bed.Window);
            bed.Explorer.Save();

            Assert.DoesNotContain(bed.Store.Load("Test")!.Columns, c => c.Header.Contains("Odd"));
        }
        finally { bed.Window.Close(); }
    });

    [Fact]
    public void AFailedSaveIsReportedNotThrown() => _fx.Invoke(() =>
    {
        ThemeManager.Apply(_fx.App, dark: false);
        var blocked = Path.Combine(_dir, "blocked");
        Directory.CreateDirectory(Path.Combine(blocked, "table-columns.json"));   // a folder where the file should be
        var reported = new List<Exception>();
        var grid = new DataGrid();
        grid.Columns.Add(new DataGridTextColumn { Header = "Name", Width = new DataGridLength(100) });
        var explorer = ExplorerColumns.Attach(grid, "Test",
            store: new TableLayoutStore(Path.Combine(blocked, "table-columns.json")),
            reportSaveError: reported.Add);

        explorer.Save();

        Assert.Single(reported);
    });
}
