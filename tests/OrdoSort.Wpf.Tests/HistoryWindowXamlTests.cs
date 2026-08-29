using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using OrdoSort.Core;
using OrdoSort.Wpf.Theme;
using OrdoSort.Wpf.ViewModels;
using OrdoSort.Wpf.Windows;

namespace OrdoSort.Wpf.Tests;

/// <summary>2026-08-02 audit-remediation: HistoryWindow's empty-state
/// messages (Task 7 Steps 2–3) and the header/binding divergence that lets
/// this grid say "Destination" while still binding <c>Route</c> (Task 9,
/// audit finding I4) — both unaffected by, and still passing after, every
/// column-sizing change below. Built the same headless way as
/// <see cref="DataGridStarColumnTests"/> (off-screen Show()+UpdateLayout()
/// on the shared <see cref="HighlightContrastFixture"/> STA thread) so real
/// Styles.xaml resources and the real production XAML are exercised, not a
/// hand-copied stand-in.
///
/// UPDATED 2026-08-29 (grid-autofit-wrap, Task 5): this suite used to assert
/// Name/Destination/Original/Filed-as each carried
/// <c>TextTrimming="CharacterEllipsis"</c> plus a <c>ToolTip</c> repeating
/// the cell's own text. <see cref="TextColumnsWrapRatherThanTrim"/> now
/// asserts the opposite for all four: a <c>TextWrapping="Wrap"</c> setter,
/// and NEITHER a TextTrimming NOR a ToolTip setter — DataGridColumnCap's
/// autofit wraps a column that gives way rather than clipping it, and a
/// tooltip that only repeated visible text would have had nothing left to
/// add once nothing is hidden. <see cref="WhenIsNotCappedBecauseItsContentIsBounded"/>
/// confirms <c>When</c> was deliberately taken OUT of that governed set
/// instead of joining the other four: its value is a timestamp History
/// formats itself, always 16 characters, so it is sized to its own content
/// rather than ever being asked to wrap a date — asserted as its MaxWidth
/// reading WPF's own uncapped default, PositiveInfinity, which only holds
/// if DataGridColumnCap genuinely never assigns it one (see that fact's own
/// doc comment for why that isn't a vacuous default-value check).
///
/// What this suite CANNOT verify headlessly: it reads declared style
/// setters (TextWrapping/TextTrimming/ToolTip) and column state (MaxWidth)
/// off a window that is shown but off-screen, never painted on a real
/// display — so it proves what the XAML and DataGridColumnCap DECLARE, not
/// that wrapped text visually reflows onto a second line or that the row
/// grows to fit it. That mechanism is measured elsewhere, off this specific
/// window: <see cref="AutoFitColumnTests"/> (the six real windows,
/// including this one) and <see cref="DataGridColumnCapTests"/> (the class
/// itself, on a bare grid built in code — including the underlying WPF fact
/// that a wrapped cell grows its own row).</summary>
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

    /// <summary>2026-08-29: the four text columns wrap instead of trimming
    /// — DataGridColumnCap's autofit gives each its content width when that
    /// fits and a proportional share when it doesn't, and a share is only
    /// honest if the text it can't show on one line goes onto the next.
    /// No tooltip repeats the cell: the whole value is on screen.</summary>
    [Theory]
    [InlineData("Name")]
    [InlineData("Destination")]
    [InlineData("Original")]
    [InlineData("Filed as")]
    public void TextColumnsWrapRatherThanTrim(string header) => _fx.Invoke(() =>
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
            var setters = column.ElementStyle!.Setters.OfType<Setter>().ToList();
            Assert.Contains(setters, s => s.Property == TextBlock.TextWrappingProperty && Equals(s.Value, TextWrapping.Wrap));
            Assert.DoesNotContain(setters, s => s.Property == TextBlock.TextTrimmingProperty);
            Assert.DoesNotContain(setters, s => s.Property == FrameworkElement.ToolTipProperty);
        }
        finally { Cleanup(win, history, dbPath); }
    });

    /// <summary>When holds a timestamp History formats itself — bounded, 16
    /// characters — so it is no longer one of the governed columns: sized
    /// to its content, never asked to give way, never wrapped mid-date.
    /// An uncapped column's MaxWidth is WPF's default, infinity.</summary>
    [Fact]
    public void WhenIsNotCappedBecauseItsContentIsBounded() => _fx.Invoke(() =>
    {
        ThemeManager.Apply(_fx.App, dark: false);
        var (win, history, dbPath) = BuildWindow();
        try
        {
            var grid = FindDescendant<DataGrid>(win)!;
            var when = grid.Columns.First(c => (string)c.Header == "When");
            Assert.True(double.IsPositiveInfinity(when.MaxWidth),
                $"When should not be governed by DataGridColumnCap: MaxWidth is {when.MaxWidth}");
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
