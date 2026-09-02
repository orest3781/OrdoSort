using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
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
public class SharedGridStyleTests
{
    private readonly HighlightContrastFixture _fx;
    public SharedGridStyleTests(HighlightContrastFixture fx) => _fx = fx;

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

    /// <summary>Table-rules, Rule 2: DataGridCell's own horizontal Padding
    /// is 12 (was 8), so text in neighbouring columns has 24px between it —
    /// vertical is untouched. Read off a realized DataGridCell, not walked
    /// out of the implicit Style's Setters: this app never keys the
    /// DataGridCell style, so there is no StaticResource lookup by name to
    /// piggyback on the way GridCellText's own tests do, and the realized
    /// cell is what a user's own 24px gap actually depends on regardless of
    /// which Style in the implicit lookup supplied it.</summary>
    [Fact]
    public void DataGridCellPaddingIsTwelveHorizontalFourVertical() => _fx.Invoke(() =>
    {
        ThemeManager.Apply(_fx.App, dark: false);
        var (win, history, dbPath) = BuildHistoryWindowWithOneRow();
        try
        {
            var grid = FindDescendant<DataGrid>(win)!;
            var row = (DataGridRow)grid.ItemContainerGenerator.ContainerFromIndex(0);
            var cell = FindAllDescendants<DataGridCell>(row).First();
            Assert.Equal(new Thickness(12, 4, 12, 4), cell.Padding);
        }
        finally { CleanupHistory(win, history, dbPath); }
    });

    /// <summary>Table-rules Rule 1's own regression case: PageCountsWindow's
    /// "Pages" column used to declare HorizontalAlignment="Right" on the
    /// reasoning that a column of numbers reads fastest lined up on the
    /// ones place — the owner's instruction covers even that "genuine
    /// reason." Nothing left column-local to remove it: GridCellTextSelectionAware's
    /// own default now puts it at Left.</summary>
    [Fact]
    public void PageCountsPagesColumnAlignsLeftNotRight() => _fx.Invoke(() =>
    {
        ThemeManager.Apply(_fx.App, dark: false);
        var vm = new PageCountsViewModel(new FakeDialogs());
        var row = new PageCountRow(@"C:\inbox\a.pdf");
        row.Apply(new PageCounts.CountResult(row.Path, 42, ""));
        vm.Rows.Add(row);
        var win = new PageCountsWindow(vm)
        {
            WindowStartupLocation = WindowStartupLocation.Manual,
            Left = -20000, Top = 0, ShowActivated = false,
        };
        win.Show();
        win.UpdateLayout();
        try
        {
            var grid = FindDescendant<DataGrid>(win)!;
            var column = grid.Columns.OfType<DataGridTextColumn>().First(c => (string)c.Header == "Pages");
            var gridRow = (DataGridRow)grid.ItemContainerGenerator.ContainerFromIndex(0);
            var text = (TextBlock)column.GetCellContent(gridRow);
            Assert.Equal(HorizontalAlignment.Left, text.HorizontalAlignment);
        }
        finally { win.Close(); }
    });

    /// <summary>Table-rules Rule 1's own regression case: FilenameListWindow's
    /// "Pages" column, same reasoning and same fix as PageCountsWindow's
    /// above — see PagesColumn's own comment in FilenameListWindow.xaml.</summary>
    [Fact]
    public void FilenameListPagesColumnAlignsLeftNotRight() => _fx.Invoke(() =>
    {
        ThemeManager.Apply(_fx.App, dark: false);
        var vm = new FilenameListViewModel(new FakeDialogs()) { Columns = FilenameList.Columns.Pages };
        vm.Rows.Add(new FilenameList.FileRow("a.pdf", 1024, DateTime.Today, @"C:\inbox",
            @"C:\inbox\a.pdf", Pages: 42));
        var win = new FilenameListWindow(vm)
        {
            WindowStartupLocation = WindowStartupLocation.Manual,
            Left = -20000, Top = 0, ShowActivated = false,
        };
        win.Show();
        win.UpdateLayout();
        try
        {
            var grid = FindDescendant<DataGrid>(win)!;
            var column = grid.Columns.OfType<DataGridTextColumn>().First(c => (string)c.Header == "Pages");
            var gridRow = (DataGridRow)grid.ItemContainerGenerator.ContainerFromIndex(0);
            var text = (TextBlock)column.GetCellContent(gridRow);
            Assert.Equal(HorizontalAlignment.Left, text.HorizontalAlignment);
        }
        finally { win.Close(); }
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

    private static T? FindDescendant<T>(DependencyObject root) where T : DependencyObject
    {
        var count = System.Windows.Media.VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
        {
            var child = System.Windows.Media.VisualTreeHelper.GetChild(root, i);
            if (child is T match) return match;
            if (FindDescendant<T>(child) is { } nested) return nested;
        }
        return null;
    }

    private static List<T> FindAllDescendants<T>(DependencyObject root) where T : DependencyObject
    {
        var results = new List<T>();
        var count = System.Windows.Media.VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
        {
            var child = System.Windows.Media.VisualTreeHelper.GetChild(root, i);
            if (child is T match) results.Add(match);
            results.AddRange(FindAllDescendants<T>(child));
        }
        return results;
    }
}
