using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Threading;
using OrdoSort.Wpf.Theme;
using OrdoSort.Wpf.Views;

namespace OrdoSort.Wpf.Tests;

/// <summary>2026-08-29, "the columns don't autofit, hiding text, and I have
/// to readjust every time": DataGridColumnCap's rule became autofit-then-
/// wrap. These facts prove the mechanism on a bare grid built in code — a
/// star Name column with a floor and a tracked Auto Note column, both
/// wrapping, the shape every tool window has — so each one is about the
/// class, not about any window's XAML (AutoFitColumnTests covers the
/// windows).
///
/// The WPF facts they lean on were measured before the class was written
/// (see the plan, docs/superpowers/plans/2026-08-29-grid-autofit-wrap.md):
/// a wrapped cell grows its row; an Auto column displays at min(desired,
/// MaxWidth); WPF's desired width never shrinks, which is why the class
/// caps at the MEASURED width even when everything fits — relaxing to
/// infinity would leave a column at its old width forever.</summary>
[Collection(HighlightContrastTests.Name)]
public class DataGridColumnCapTests
{
    private readonly HighlightContrastFixture _fx;
    public DataGridColumnCapTests(HighlightContrastFixture fx) => _fx = fx;

    private sealed class Row
    {
        public string Name { get; init; } = "";
        public string Note { get; init; } = "";
    }

    private sealed record BareGrid(
        Window Window, DataGrid DataGrid, DataGridTextColumn Name, DataGridTextColumn Note,
        ObservableCollection<Row> Rows);

    /// <summary>Track is called the way the windows call it — only the Auto
    /// column is passed; the star column is discovered.</summary>
    private static BareGrid Build(double windowWidth, double nameFloor, params Row[] rows)
    {
        var items = new ObservableCollection<Row>(rows);
        var grid = new DataGrid
        {
            ItemsSource = items, AutoGenerateColumns = false, IsReadOnly = true,
            CanUserAddRows = false, HeadersVisibility = DataGridHeadersVisibility.Column,
        };
        var name = new DataGridTextColumn
        {
            Header = "Name", Binding = new Binding("Name"),
            Width = new DataGridLength(1, DataGridLengthUnitType.Star), MinWidth = nameFloor,
            ElementStyle = Wrapping(),
        };
        var note = new DataGridTextColumn
        {
            Header = "Note", Binding = new Binding("Note"), Width = DataGridLength.Auto,
            ElementStyle = Wrapping(),
        };
        grid.Columns.Add(name);
        grid.Columns.Add(note);
        DataGridColumnCap.Track(grid, note);
        var window = new Window
        {
            Width = windowWidth, Height = 400, Content = grid,
            WindowStartupLocation = WindowStartupLocation.Manual, Left = -20000, Top = 0,
            ShowActivated = false,
        };
        return new BareGrid(window, grid, name, note, items);
    }

    private static Style Wrapping()
    {
        var style = new Style(typeof(TextBlock));
        style.Setters.Add(new Setter(TextBlock.TextWrappingProperty, TextWrapping.Wrap));
        return style;
    }

    private static void ShowAndSettle(Window window)
    {
        window.Show();
        Settle(window);
    }

    /// <summary>UpdateLayout, then drain Background priority: WPF reconciles
    /// star-column widths there, and a headless window never gets the
    /// WM_SIZE that does it for free (AutoFitColumnTests.ShowOffscreenThenResizeTo
    /// records that measurement).</summary>
    private static void Settle(Window window)
    {
        window.UpdateLayout();
        Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.Background);
        window.UpdateLayout();
    }

    private static DataGridRow RowAt(DataGrid grid, int index) =>
        (DataGridRow)grid.ItemContainerGenerator.ContainerFromIndex(index);

    private static TextBlock CellText(DataGrid grid, DataGridColumn column, int rowIndex) =>
        (TextBlock)column.GetCellContent(RowAt(grid, rowIndex));

    /// <summary>What the class measures for one cell, restated so a fact
    /// can say "as wide as its content" with a number.</summary>
    private static double ContentWidthOf(TextBlock text) =>
        Math.Ceiling(new FormattedText(
            text.Text, CultureInfo.CurrentUICulture, text.FlowDirection,
            new Typeface(text.FontFamily, text.FontStyle, text.FontWeight, text.FontStretch),
            text.FontSize, Brushes.Black, null!, TextOptions.GetTextFormattingMode(text),
            VisualTreeHelper.GetDpi(text).PixelsPerDip).WidthIncludingTrailingWhitespace);

    private static double LineHeightOf(TextBlock text) => text.FontSize * text.FontFamily.LineSpacing;

    /// <summary>The class's own budget, restated: the grid's width less the
    /// vertical-scrollbar reservation and its 20px safety margin. The same
    /// pattern as AutoFitColumnTests.ExpectedTriageColumnCap — a fact that
    /// uses it doubles as a check that the constants haven't drifted.</summary>
    private static double AvailableWidthOf(DataGrid grid) =>
        grid.ActualWidth - SystemParameters.VerticalScrollBarWidth - 20;

    private static Visibility HorizontalScrollbarOf(DataGrid grid) =>
        FindDescendant<ScrollViewer>(grid)!.ComputedHorizontalScrollBarVisibility;

    private static string Ms(int count) => new('M', count);

    [Fact]
    public void WhenEverythingFitsTheTrackedColumnIsExactlyAsWideAsItsContent() => _fx.Invoke(() =>
    {
        ThemeManager.Apply(_fx.App, dark: false);
        var g = Build(900, 100, new Row { Name = "a.pdf", Note = "merged 3 PDFs" });
        try
        {
            ShowAndSettle(g.Window);
            var text = CellText(g.DataGrid, g.Note, 0);
            var content = ContentWidthOf(text);
            Assert.True(Math.Abs(g.Note.ActualWidth - content) <= 2,
                $"Note is {g.Note.ActualWidth}px for {content}px of content — expected the content width, give or take a pixel of slack");
            Assert.True(RowAt(g.DataGrid, 0).ActualHeight < 1.5 * LineHeightOf(text),
                "nothing should wrap when everything fits");
            Assert.True(g.Name.ActualWidth > 400,
                $"the star column should take the rest, not sit at its floor: {g.Name.ActualWidth}px");
            Assert.NotEqual(Visibility.Visible, HorizontalScrollbarOf(g.DataGrid));
        }
        finally { g.Window.Close(); }
    });

    [Fact]
    public void WhenTheContentDoesNotFitTheWidthIsSplitInProportionAndTheTextWraps() => _fx.Invoke(() =>
    {
        ThemeManager.Apply(_fx.App, dark: false);
        // Thirty M's against sixty: content widths in a 1:2 ratio, both far
        // wider than a 500px window can show.
        var g = Build(500, 40, new Row { Name = Ms(30), Note = Ms(60) });
        try
        {
            ShowAndSettle(g.Window);
            var nameContent = ContentWidthOf(CellText(g.DataGrid, g.Name, 0)) + 1;
            var noteContent = ContentWidthOf(CellText(g.DataGrid, g.Note, 0)) + 1;
            var expectedNoteShare = AvailableWidthOf(g.DataGrid) * noteContent / (nameContent + noteContent);
            Assert.True(Math.Abs(g.Note.MaxWidth - expectedNoteShare) <= 3,
                $"Note's cap is {g.Note.MaxWidth}px; splitting {AvailableWidthOf(g.DataGrid)}px between " +
                $"{nameContent}px and {noteContent}px of content in proportion gives {expectedNoteShare}px");
            Assert.True(Math.Abs(g.Note.ActualWidth - g.Note.MaxWidth) <= 1,
                $"Note should sit at its cap: {g.Note.ActualWidth}px against {g.Note.MaxWidth}px");
            Assert.True(g.Name.ActualWidth >= 150,
                $"Name should get its share rather than be starved to its 40px floor: {g.Name.ActualWidth}px");
            var noteText = CellText(g.DataGrid, g.Note, 0);
            Assert.True(noteText.ActualHeight >= 2 * LineHeightOf(noteText),
                $"Note should wrap: {noteText.ActualHeight}px against a {LineHeightOf(noteText)}px line");
            Assert.NotEqual(Visibility.Visible, HorizontalScrollbarOf(g.DataGrid));
        }
        finally { g.Window.Close(); }
    });

    [Fact]
    public void AColumnWhoseShareWouldFallUnderItsFloorIsHeldThereAndTheOtherTakesTheRest() => _fx.Invoke(() =>
    {
        ThemeManager.Apply(_fx.App, dark: false);
        var g = Build(500, 180, new Row { Name = "a.pdf", Note = Ms(80) });
        try
        {
            ShowAndSettle(g.Window);
            // A proportional share of a short name would be ~70px; the 180px
            // floor holds, and Note gets exactly what the floor leaves.
            var expectedNoteCap = AvailableWidthOf(g.DataGrid) - 180;
            Assert.True(Math.Abs(g.Note.MaxWidth - expectedNoteCap) <= 2,
                $"Note's cap is {g.Note.MaxWidth}px; with Name held at its 180px floor it should be {expectedNoteCap}px");
            Assert.True(g.Name.ActualWidth >= 180,
                $"Name must never drop under its floor: {g.Name.ActualWidth}px");
            Assert.True(Math.Abs(g.Note.ActualWidth - g.Note.MaxWidth) <= 1, "Note should sit at its cap");
            Assert.NotEqual(Visibility.Visible, HorizontalScrollbarOf(g.DataGrid));
        }
        finally { g.Window.Close(); }
    });

    [Fact]
    public void AWrappedCellGrowsItsRowRatherThanClippingTheText() => _fx.Invoke(() =>
    {
        ThemeManager.Apply(_fx.App, dark: false);
        var g = Build(500, 40, new Row { Name = Ms(30), Note = Ms(60) }, new Row { Name = "b.pdf", Note = "ok" });
        try
        {
            ShowAndSettle(g.Window);
            var wrapped = CellText(g.DataGrid, g.Note, 0);
            var lineHeight = LineHeightOf(wrapped);
            Assert.True(wrapped.ActualHeight >= 2 * lineHeight,
                $"the long cell should be at least two lines: {wrapped.ActualHeight}px against {lineHeight}px");
            Assert.True(RowAt(g.DataGrid, 0).ActualHeight >= wrapped.ActualHeight - 1,
                $"the row ({RowAt(g.DataGrid, 0).ActualHeight}px) must grow to hold the wrapped text ({wrapped.ActualHeight}px)");
            Assert.True(RowAt(g.DataGrid, 1).ActualHeight < 1.5 * lineHeight,
                $"the short row beside it should stay one line: {RowAt(g.DataGrid, 1).ActualHeight}px");
        }
        finally { g.Window.Close(); }
    });

    [Fact]
    public void ATrackedColumnShrinksBackWhenItsLongContentIsRemoved() => _fx.Invoke(() =>
    {
        ThemeManager.Apply(_fx.App, dark: false);
        var g = Build(900, 100, new Row { Name = "a.pdf", Note = Ms(60) }, new Row { Name = "b.pdf", Note = "ok" });
        try
        {
            ShowAndSettle(g.Window);
            Assert.True(g.Note.ActualWidth > 500, $"precondition: the long row should make Note wide ({g.Note.ActualWidth}px)");
            g.Rows.RemoveAt(0);
            Settle(g.Window);
            // WPF alone would leave it at the old width — its desired width
            // never shrinks (measured: 802px after the long row was removed).
            Assert.True(g.Note.ActualWidth < 80,
                $"Note should shrink back to its remaining content once the long row is gone: {g.Note.ActualWidth}px");
        }
        finally { g.Window.Close(); }
    });

    [Fact]
    public void AHeaderWiderThanEveryCellSetsTheTrackedColumnsWidth() => _fx.Invoke(() =>
    {
        ThemeManager.Apply(_fx.App, dark: false);
        var g = Build(900, 100, new Row { Name = "a.pdf", Note = "ok" });
        g.Note.Header = "A considerably longer column header";
        try
        {
            ShowAndSettle(g.Window);
            var header = FindAllDescendants<DataGridColumnHeader>(g.DataGrid).First(h => h.Column == g.Note);
            var headerText = FindDescendant<TextBlock>(header)!;
            // Theme/Styles.xaml gives DataGridColumnHeader Padding="8,6".
            var expected = ContentWidthOf(headerText) + header.Padding.Left + header.Padding.Right;
            Assert.True(Math.Abs(g.Note.ActualWidth - expected) <= 3,
                $"Note is {g.Note.ActualWidth}px; its header needs {expected}px");
        }
        finally { g.Window.Close(); }
    });

    [Fact]
    public void AStarColumnIsNeverGivenACapOfItsOwn() => _fx.Invoke(() =>
    {
        ThemeManager.Apply(_fx.App, dark: false);
        var g = Build(500, 40, new Row { Name = Ms(30), Note = Ms(60) });
        try
        {
            ShowAndSettle(g.Window);
            // It takes what the governed columns leave; capping it too would
            // fight WPF's own star reconciliation.
            Assert.True(double.IsPositiveInfinity(g.Name.MaxWidth),
                $"the star column's MaxWidth should be untouched: {g.Name.MaxWidth}");
        }
        finally { g.Window.Close(); }
    });

    private static T? FindDescendant<T>(DependencyObject root) where T : DependencyObject
    {
        var count = VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T match) return match;
            if (FindDescendant<T>(child) is { } nested) return nested;
        }
        return null;
    }

    private static List<T> FindAllDescendants<T>(DependencyObject root) where T : DependencyObject
    {
        var results = new List<T>();
        var count = VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T match) results.Add(match);
            results.AddRange(FindAllDescendants<T>(child));
        }
        return results;
    }
}
