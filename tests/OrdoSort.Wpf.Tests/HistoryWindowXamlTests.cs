using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using OrdoSort.Core;
using OrdoSort.Wpf.Theme;
using OrdoSort.Wpf.ViewModels;
using OrdoSort.Wpf.Windows;

namespace OrdoSort.Wpf.Tests;

/// <summary>2026-08-02 audit-remediation, Task 7 Steps 2–3: HistoryWindow's
/// empty-state messages and the Name/Route columns' ellipsis trimming +
/// tooltips. Built the same headless way as <see cref="DataGridStarColumnTests"/>
/// (off-screen Show()+UpdateLayout() on the shared <see cref="HighlightContrastFixture"/>
/// STA thread) so real Styles.xaml resources and the real production XAML are
/// exercised, not a hand-copied stand-in.
///
/// What this suite CANNOT verify headlessly: whether the trimmed text visually
/// renders with an actual ellipsis glyph, and whether the tooltip popup shows
/// on real mouse hover — TextTrimming/ToolTip are asserted as properties on
/// the generated cell content and the column's ElementStyle respectively, not
/// as rendered pixels or an interactive hover.</summary>
[Collection(HighlightContrastTests.Name)]
public class HistoryWindowXamlTests
{
    private readonly HighlightContrastFixture _fx;
    public HistoryWindowXamlTests(HighlightContrastFixture fx) => _fx = fx;

    private static (HistoryWindow win, History history, string dbPath) BuildWindow(
        Action<HistoryViewModel>? beforeShow = null)
    {
        var dbPath = Path.Combine(Path.GetTempPath(), "ordo_test_history_" + Guid.NewGuid() + ".sqlite");
        var history = new History(dbPath);
        // InlineWorkScheduler: see DataGridStarColumnTests' identical comment —
        // HistoryViewModel's constructor kicks off an async LoadAsync; inline
        // makes it finish synchronously before this method returns.
        var vm = new HistoryViewModel(history, new FakeDialogs(), new InlineWorkScheduler());
        beforeShow?.Invoke(vm);
        var win = new HistoryWindow(vm);
        win.WindowStartupLocation = WindowStartupLocation.Manual;
        win.Left = -20000;
        win.Top = 0;
        win.ShowActivated = false;
        win.Show();
        win.UpdateLayout();
        return (win, history, dbPath);
    }

    private static void Cleanup(Window win, History history, string dbPath)
    {
        try { win.Close(); } catch { /* best effort */ }
        history.Dispose();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { File.Delete(dbPath); } catch { /* best effort */ }
    }

    /// <summary>2026-08-02 audit finding I4, Task 9: this grid was the one
    /// place in the app that showed a user the word "Route". Everywhere else
    /// — the Settings tab, its buttons, its validation text — says
    /// "Destination".
    ///
    /// The second half is the part that matters most, and is why this asserts
    /// the binding as well as the header: "Route" is the INTERNAL name and had
    /// to survive the rename. <c>HistoryRow.Route</c>, the <c>routes</c>
    /// config key, the <c>Route</c>/<c>RouteButtonViewModel</c> types and the
    /// history table's own <c>route_label</c>/<c>route_path</c> columns are
    /// all unchanged; a rename that had followed the label into the binding
    /// path would have produced an empty column, silently.</summary>
    [Fact]
    public void TheHistoryGridSaysDestinationButStillBindsRoute() => _fx.Invoke(() =>
    {
        ThemeManager.Apply(_fx.App, dark: false);
        var (win, history, dbPath) = BuildWindow();
        try
        {
            var grid = FindDescendant<DataGrid>(win)
                ?? throw new InvalidOperationException("No DataGrid descendant found");
            var headers = grid.Columns.Select(c => c.Header?.ToString() ?? "").ToList();

            Assert.Contains("Destination", headers);
            Assert.DoesNotContain("Route", headers);

            var column = grid.Columns.OfType<DataGridTextColumn>()
                .First(c => (string)c.Header == "Destination");
            var binding = Assert.IsType<Binding>(column.Binding);
            Assert.Equal("Route", binding.Path.Path);

            // the machine-read side of the same rename: the CSV export writes
            // the history table's own column names, which are NOT user-facing
            // copy and must not follow the label
            Assert.Contains("route_label", History.Columns);
            Assert.Contains("route_path", History.Columns);
        }
        finally { Cleanup(win, history, dbPath); }
    });

    [Fact]
    public void EmptyHistoryShowsTheNoFilingsMessage() => _fx.Invoke(() =>
    {
        ThemeManager.Apply(_fx.App, dark: false);
        var (win, history, dbPath) = BuildWindow();
        try
        {
            var empty = (TextBlock)win.FindName("EmptyHistoryText")!;
            var noMatches = (TextBlock)win.FindName("NoMatchesText")!;
            Assert.Equal(Visibility.Visible, empty.Visibility);
            Assert.Equal(Visibility.Collapsed, noMatches.Visibility);
            Assert.Equal("No filings recorded yet. Documents you file will appear here.",
                empty.Text);
        }
        finally { Cleanup(win, history, dbPath); }
    });

    [Fact]
    public void PopulatedHistoryHidesBothEmptyStateMessages() => _fx.Invoke(() =>
    {
        ThemeManager.Apply(_fx.App, dark: false);
        History? history = null;
        string? dbPath = null;
        Window? win = null;
        try
        {
            dbPath = Path.Combine(Path.GetTempPath(), "ordo_test_history_" + Guid.NewGuid() + ".sqlite");
            history = new History(dbPath);
            history.LogCommit("c:\\in\\a.pdf", "a.pdf", "A.pdf", "A",
                "insert", "", "Invoices", "c:\\out", tagged: false, "");
            var vm = new HistoryViewModel(history, new FakeDialogs(), new InlineWorkScheduler());
            win = new HistoryWindow(vm);
            win.WindowStartupLocation = WindowStartupLocation.Manual;
            win.Left = -20000;
            win.Top = 0;
            win.ShowActivated = false;
            win.Show();
            win.UpdateLayout();

            var empty = (TextBlock)win.FindName("EmptyHistoryText")!;
            var noMatches = (TextBlock)win.FindName("NoMatchesText")!;
            Assert.Equal(Visibility.Collapsed, empty.Visibility);
            Assert.Equal(Visibility.Collapsed, noMatches.Visibility);
        }
        finally
        {
            if (win is not null) Cleanup(win, history!, dbPath!);
        }
    });

    [Fact]
    public void FilterWithNoMatchesShowsTheNoMatchesMessage() => _fx.Invoke(() =>
    {
        ThemeManager.Apply(_fx.App, dark: false);
        History? history = null;
        string? dbPath = null;
        HistoryWindow? win = null;
        try
        {
            dbPath = Path.Combine(Path.GetTempPath(), "ordo_test_history_" + Guid.NewGuid() + ".sqlite");
            history = new History(dbPath);
            history.LogCommit("c:\\in\\a.pdf", "a.pdf", "A.pdf", "A",
                "insert", "", "Invoices", "c:\\out", tagged: false, "");
            var vm = new HistoryViewModel(history, new FakeDialogs(), new InlineWorkScheduler());
            win = new HistoryWindow(vm);
            win.WindowStartupLocation = WindowStartupLocation.Manual;
            win.Left = -20000;
            win.Top = 0;
            win.ShowActivated = false;
            win.Show();
            win.UpdateLayout();

            vm.Filter = "nonexistentxyz";
            win.UpdateLayout();

            var empty = (TextBlock)win.FindName("EmptyHistoryText")!;
            var noMatches = (TextBlock)win.FindName("NoMatchesText")!;
            Assert.Equal(Visibility.Collapsed, empty.Visibility);
            Assert.Equal(Visibility.Visible, noMatches.Visibility);
            Assert.Equal("No filings match your search.", noMatches.Text);
        }
        finally
        {
            if (win is not null) Cleanup(win, history!, dbPath!);
        }
    });

    /// <summary>Task 7 Step 3: the two fixed-width text columns clipped
    /// without ellipsis or any way to recover the full value. Asserted
    /// directly on each column's ElementStyle (the column-level style WPF
    /// applies to the generated per-cell TextBlock) rather than a realized
    /// cell, since DataGrid virtualizes cells and this property is fixed at
    /// the column level regardless of which rows are realized.
    ///
    /// Header and binding path are separate parameters because Task 9 made
    /// them differ: the column a user reads is headed "Destination" while the
    /// property behind it is still <c>HistoryRow.Route</c>. Collapsing them
    /// back into one argument would quietly re-couple the label to the
    /// internal name.
    ///
    /// EXTENDED 2026-08-07 (autofit-columns, Step 5) to also cover Original/
    /// Filed-as: those two star columns never actually had TextTrimming/
    /// ToolTip before this task despite Name/Destination's original comment
    /// here claiming star columns "don't need this" (they were kept star
    /// rather than switched to Auto — a 3000-row/long-path measurement found
    /// Auto's width visibly jumps for these two specifically, see
    /// HistoryWindow.xaml — but a star column's ASSIGNED width still clips a
    /// value that doesn't fit it without this).</summary>
    [Theory]
    [InlineData("Name", "Name")]
    [InlineData("Destination", "Route")]
    [InlineData("Original", "Original")]
    [InlineData("Filed as", "FiledAs")]
    public void NameAndDestinationColumnsTrimWithEllipsisAndCarryATooltip(
        string header, string bindingPath) => _fx.Invoke(() =>
    {
        ThemeManager.Apply(_fx.App, dark: false);
        var (win, history, dbPath) = BuildWindow();
        try
        {
            var grid = FindDescendant<DataGrid>(win)
                ?? throw new InvalidOperationException("No DataGrid descendant found");
            var column = grid.Columns.OfType<DataGridTextColumn>()
                .FirstOrDefault(c => (string)c.Header == header)
                ?? throw new InvalidOperationException($"No '{header}' column found");

            Assert.NotNull(column.ElementStyle);
            var trimSetter = column.ElementStyle!.Setters
                .OfType<Setter>()
                .FirstOrDefault(s => s.Property == TextBlock.TextTrimmingProperty);
            Assert.NotNull(trimSetter);
            Assert.Equal(TextTrimming.CharacterEllipsis, trimSetter!.Value);

            var tooltipSetter = column.ElementStyle!.Setters
                .OfType<Setter>()
                .FirstOrDefault(s => s.Property == FrameworkElement.ToolTipProperty);
            Assert.NotNull(tooltipSetter);
            var binding = Assert.IsType<Binding>(tooltipSetter!.Value);
            Assert.Equal(bindingPath, binding.Path.Path);
        }
        finally { Cleanup(win, history, dbPath); }
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
}
