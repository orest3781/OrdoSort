using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using OrdoSort.Core;
using OrdoSort.Wpf.Theme;
using OrdoSort.Wpf.ViewModels;
using OrdoSort.Wpf.Windows;

namespace OrdoSort.Wpf.Tests;

/// <summary>Table-rules, Rules 1 and 2 — the two changes that live entirely
/// in Theme/Styles.xaml's shared DataGridColumnHeader and DataGridCell
/// styles, proven here once on real, rendered windows rather than
/// re-asserted per column per window.
///
/// RULE 1: every column header and every cell aligns left, in every grid in
/// the app — no exceptions, not even a column with a "genuine reason" to
/// differ. The survey this rule asked for found three concrete offenders,
/// each with its own dedicated regression fact below: PageCountsWindow's
/// and FilenameListWindow's own "Pages" columns (both explicitly
/// right-aligned — "a column of numbers reads fastest lined up on the ones
/// place" — reverted on the owner's own explicit instruction that the rule
/// has no exceptions), and HistoryWindow's "Undone" DataGridCheckBoxColumn,
/// left at WPF's own stock Stretch default for that column type and never
/// previously overridden by this app — measured, not assumed: reverting the
/// fix locally during this task and reading the realized CheckBox's own
/// HorizontalAlignment back reported Stretch, not the Center a reader might
/// guess. DataGridColumnHeader.HorizontalContentAlignment's own fix turned
/// out to be belt-and-braces rather than load-bearing: reverting IT locally
/// changed nothing, because WPF's stock header template already rendered
/// left — EveryRealizedColumnHeaderAlignsLeft is kept anyway (Theme/
/// Styles.xaml's own comment on the Setter explains why: a real, testable
/// declaration of intent rather than an accident this file never actually
/// decided), reported here rather than silently left unrevert-proofed.
///
/// RULE 2: DataGridCell's own horizontal Padding moved from 8 to 12, so
/// text in neighbouring columns has 24px between it.</summary>
[Collection(HighlightContrastTests.Name)]
public class SharedGridStyleTests : UiTest
{
    private readonly HighlightContrastFixture _fx;
    public SharedGridStyleTests(HighlightContrastFixture fx) : base(fx) => _fx = fx;

    private static (Window win, History history, string dbPath) BuildHistoryWindowWithOneRow()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), "ordo_test_align_" + Guid.NewGuid() + ".sqlite");
        var history = new History(dbPath);
        history.LogCommit(@"c:\in\a.pdf", "a.pdf", "A.pdf", "A",
            "insert", "", "Invoices", @"c:\out", tagged: false, "");
        var vm = new HistoryViewModel(history, new FakeDialogs(), new InlineWorkScheduler());
        var win = new HistoryWindow(vm)
        {
            WindowStartupLocation = WindowStartupLocation.Manual,
            Left = -20000, Top = 0, ShowActivated = false,
        };
        win.Show();
        win.UpdateLayout();
        return (win, history, dbPath);
    }

    private static void CleanupHistory(Window win, History history, string dbPath)
    {
        try { win.Close(); } catch { /* best effort */ }
        history.Dispose();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { File.Delete(dbPath); } catch { /* best effort */ }
    }

    /// <summary>Every column header in a real, multi-column grid — proves
    /// Theme/Styles.xaml's shared DataGridColumnHeader style, not a
    /// per-column override, is what every window relies on.</summary>
    [Fact]
    public void EveryRealizedColumnHeaderAlignsLeft() => _fx.Invoke(() =>
    {
        ThemeManager.Apply(_fx.App, dark: false);
        var (win, history, dbPath) = BuildHistoryWindowWithOneRow();
        try
        {
            var grid = FindDescendant<DataGrid>(win)!;
            var headers = FindAllDescendants<DataGridColumnHeader>(grid)
                .Where(h => h.Column is not null)
                .ToList();
            Assert.True(headers.Count >= 5,
                $"only {headers.Count} realized column headers found — HistoryWindow has six columns " +
                "(When/Original/Filed as/Name/Destination/Undone); this floor would catch the walk " +
                "silently finding nothing");
            var offenders = headers
                .Where(h => h.HorizontalContentAlignment != HorizontalAlignment.Left)
                .Select(h => $"{h.Column!.Header} ({h.HorizontalContentAlignment})")
                .ToList();
            Assert.True(offenders.Count == 0,
                "these realized column headers are not left-aligned: " + string.Join(", ", offenders));
        }
        finally { CleanupHistory(win, history, dbPath); }
    });

    /// <summary>Every plain text cell in the same window — HistoryWindow's
    /// Name/Destination/Original/Filed as columns carry no alignment
    /// Setter of their own, so this is GridCellText's shared default, not a
    /// per-column one.</summary>
    [Fact]
    public void EveryRealizedTextCellAlignsLeft() => _fx.Invoke(() =>
    {
        ThemeManager.Apply(_fx.App, dark: false);
        var (win, history, dbPath) = BuildHistoryWindowWithOneRow();
        try
        {
            var grid = FindDescendant<DataGrid>(win)!;
            var row = (DataGridRow)grid.ItemContainerGenerator.ContainerFromIndex(0);
            var textColumns = grid.Columns.OfType<DataGridTextColumn>().ToList();
            Assert.True(textColumns.Count >= 4,
                $"only {textColumns.Count} DataGridTextColumns found — expected at least the four " +
                "governed text columns (Name/Destination/Original/Filed as)");

            var offenders = textColumns
                .Select(c => (Header: c.Header, Text: (TextBlock)c.GetCellContent(row)))
                .Where(c => c.Text.HorizontalAlignment != HorizontalAlignment.Left)
                .Select(c => $"{c.Header} ({c.Text.HorizontalAlignment})")
                .ToList();
            Assert.True(offenders.Count == 0,
                "these realized text cells are not left-aligned: " + string.Join(", ", offenders));
        }
        finally { CleanupHistory(win, history, dbPath); }
    });

    /// <summary>Table-rules, Rule 2: 12px on each side of a cell's text, so
    /// neighbouring columns have 24px between them, and 4px above and below.
    /// Measured where the text actually LANDS inside the cell, not read off
    /// the Padding property: WPF's stock DataGridCell template ignores
    /// Padding, and the fact this replaced (which asserted the property's
    /// value) passed for weeks while every cell in the app drew its text
    /// hard against the column edge (found 2026-09-25).</summary>
    [Fact]
    public void CellTextSitsTwelvePixelsInFromTheColumnEdge() => _fx.Invoke(() =>
    {
        ThemeManager.Apply(_fx.App, dark: false);
        var (win, history, dbPath) = BuildHistoryWindowWithOneRow();
        try
        {
            var grid = FindDescendant<DataGrid>(win)!;
            var row = (DataGridRow)grid.ItemContainerGenerator.ContainerFromIndex(0);
            var cell = FindAllDescendants<DataGridCell>(row).First();
            var text = FindDescendant<TextBlock>(cell)!;

            var offset = text.TranslatePoint(new Point(0, 0), cell);

            Assert.Equal(12, offset.X, 0.5);
            Assert.Equal(4, offset.Y, 0.5);
        }
        finally { CleanupHistory(win, history, dbPath); }
    });

    /// <summary>A column's header label starts on the same vertical line as
    /// the text in the cells under it, so a column reads as one column.</summary>
    [Fact]
    public void HeaderTextLinesUpWithCellText() => _fx.Invoke(() =>
    {
        ThemeManager.Apply(_fx.App, dark: false);
        var (win, history, dbPath) = BuildHistoryWindowWithOneRow();
        try
        {
            var grid = FindDescendant<DataGrid>(win)!;
            var row = (DataGridRow)grid.ItemContainerGenerator.ContainerFromIndex(0);
            var cell = FindAllDescendants<DataGridCell>(row).First();
            var cellText = FindDescendant<TextBlock>(cell)!;
            var header = FindAllDescendants<DataGridColumnHeader>(grid).First(h => h.Column == cell.Column);
            var headerText = FindDescendant<TextBlock>(header)!;

            var cellX = cellText.TranslatePoint(new Point(0, 0), grid).X;
            var headerX = headerText.TranslatePoint(new Point(0, 0), grid).X;

            Assert.True(Math.Abs(cellX - headerX) <= 1,
                $"header text starts at {headerX:F1}px, the cell text under it at {cellX:F1}px");
        }
        finally { CleanupHistory(win, history, dbPath); }
    });

    /// <summary>Table rules v2, rule 5: columns can be dragged to a new
    /// place; rows keep one height.</summary>
    [Fact]
    public void ColumnsCanBeReorderedButRowsKeepOneHeight() => _fx.Invoke(() =>
    {
        ThemeManager.Apply(_fx.App, dark: false);
        var (win, history, dbPath) = BuildHistoryWindowWithOneRow();
        try
        {
            var grid = FindDescendant<DataGrid>(win)!;
            Assert.True(grid.CanUserReorderColumns);
            Assert.False(grid.CanUserResizeRows);
        }
        finally { CleanupHistory(win, history, dbPath); }
    });

    /// <summary>Rule 8: no lines between rows and no alternating shading,
    /// as in Explorer's Details view.</summary>
    [Fact]
    public void RowsHaveNoLinesAndNoStriping() => _fx.Invoke(() =>
    {
        ThemeManager.Apply(_fx.App, dark: false);
        var (win, history, dbPath) = BuildHistoryWindowWithOneRow();
        try
        {
            var grid = FindDescendant<DataGrid>(win)!;
            Assert.Equal(DataGridGridLinesVisibility.None, grid.GridLinesVisibility);
            Assert.Equal(((SolidColorBrush)grid.RowBackground).Color, ((SolidColorBrush)grid.AlternatingRowBackground).Color);
        }
        finally { CleanupHistory(win, history, dbPath); }
    });

    /// <summary>Rule 9: a number column's values sit on the right edge,
    /// as in Explorer's Size column; header labels stay left. The shared
    /// number style right-aligns (and stretches, so the ellipsis still
    /// works), and every number column in the app uses it.</summary>
    [Fact]
    public void NumberColumnsRightAlignTheirValues() => _fx.Invoke(() =>
    {
        var style = (Style)_fx.App.FindResource("GridCellNumber");
        var setters = style.Setters.OfType<Setter>().ToList();
        Assert.Contains(setters, s => s.Property == TextBlock.TextAlignmentProperty
            && (TextAlignment)s.Value == TextAlignment.Right);
        Assert.Contains(setters, s => s.Property == FrameworkElement.HorizontalAlignmentProperty
            && (HorizontalAlignment)s.Value == HorizontalAlignment.Stretch);

        var windows = Path.Combine(Repo.Root, "src", "OrdoSort.Wpf", "Windows");
        foreach (var (file, header) in new[]
        {
            ("PageCountsWindow.xaml", "Pages"), ("FilenameListWindow.xaml", "Pages"),
            ("FilenameListWindow.xaml", "Size"), ("FilenameListWindow.xaml", "#"),
        })
        {
            var xaml = File.ReadAllText(Path.Combine(windows, file));
            // Match the column tag itself: the File list's column menu has
            // MenuItems with the same Header text earlier in the file.
            var match = System.Text.RegularExpressions.Regex.Match(xaml,
                $"<DataGridTextColumn\\s[^>]*Header=\"{System.Text.RegularExpressions.Regex.Escape(header)}\"");
            var start = match.Success ? match.Index : -1;
            var end = xaml.IndexOf("</DataGridTextColumn>", start, StringComparison.Ordinal);
            Assert.True(start >= 0 && end > start, $"{file} has no {header} column");
            Assert.Contains("GridCellNumber", xaml[start..end]);
        }
    });

    [Fact]
    public void HeaderLabelsAlignLeft() => _fx.Invoke(() =>
    {
        var style = (Style)_fx.App.FindResource(typeof(DataGridColumnHeader));
        Assert.Contains(style.Setters.OfType<Setter>(), s => s.Property == Control.HorizontalContentAlignmentProperty
            && (HorizontalAlignment)s.Value == HorizontalAlignment.Left);
    });

    /// <summary>Rule 6: the sorted header shows a chevron at 3:1 or better in
    /// both themes.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void TheSortChevronIsVisible(bool dark) => _fx.Invoke(() =>
    {
        ThemeManager.Apply(_fx.App, dark);
        var (win, history, dbPath) = BuildHistoryWindowWithOneRow();
        try
        {
            var grid = FindDescendant<DataGrid>(win)!;
            var column = grid.Columns.First(c => c.CanUserSort);
            column.SortDirection = System.ComponentModel.ListSortDirection.Ascending;
            win.UpdateLayout();
            var header = FindAllDescendants<DataGridColumnHeader>(grid).First(h => h.Column == column);
            var chevron = (System.Windows.Shapes.Path)header.Template.FindName("SortChevron", header);

            Assert.Equal(Visibility.Visible, chevron.Visibility);
            var stroke = ((SolidColorBrush)chevron.Stroke).Color;
            var background = ((SolidColorBrush)header.Background).Color;
            Assert.True(ThemePalette.ContrastRatio(new Rgb(stroke.R, stroke.G, stroke.B),
                new Rgb(background.R, background.G, background.B)) >= 3);
        }
        finally { CleanupHistory(win, history, dbPath); }
    });

    /// <summary>Table-rules Rule 1's third regression case, and the only
    /// non-text one: WPF's own stock DataGridCheckBoxColumn leaves its
    /// generated CheckBox at Stretch by default, never previously
    /// overridden here. Confirmed empirically, not assumed: reverting
    /// HistoryWindow.xaml's HorizontalAlignment="Left" Setter locally
    /// during this task and reading the realized CheckBox's own
    /// HorizontalAlignment back reported Stretch, not the Center a reader
    /// might expect from how the cell actually looked.</summary>
    [Fact]
    public void HistoryUndoneCheckboxAlignsLeftNotStretch() => _fx.Invoke(() =>
    {
        ThemeManager.Apply(_fx.App, dark: false);
        var (win, history, dbPath) = BuildHistoryWindowWithOneRow();
        try
        {
            var grid = FindDescendant<DataGrid>(win)!;
            var column = grid.Columns.OfType<DataGridCheckBoxColumn>().Single(c => (string)c.Header == "Undone");
            var row = (DataGridRow)grid.ItemContainerGenerator.ContainerFromIndex(0);
            var checkBox = (CheckBox)column.GetCellContent(row);
            Assert.Equal(HorizontalAlignment.Left, checkBox.HorizontalAlignment);
        }
        finally { CleanupHistory(win, history, dbPath); }
    });

}
