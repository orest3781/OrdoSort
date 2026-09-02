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
/// UPDATED for table-rules (this branch), Rule 4: between 2026-08-29 and
/// here, this suite asserted Name/Destination/Original/Filed-as each
/// carried a <c>TextWrapping="Wrap"</c> setter and neither TextTrimming
/// nor a ToolTip. <see cref="TextColumnsTrimRatherThanWrap"/> now asserts
/// the opposite for all four, reading the REALIZED cell rather than
/// declared Setters (see that fact's own doc comment for why): trimmed with
/// an ellipsis, one line, and the full value reachable as a ToolTip —
/// DataGridColumnCap's autofit still gives each its content width when
/// that fits and a proportional share when it doesn't, but the share that
/// can't hold its content now cuts it off instead of growing the row.
/// <see cref="WhenIsNotCappedBecauseItsContentIsBounded"/> confirms
/// <c>When</c> was deliberately taken OUT of that governed set instead of
/// joining the other four: its value is a timestamp History formats
/// itself, always 16 characters, so it is sized to its own content rather
/// than ever needing to trim or wrap a date — asserted as its MaxWidth
/// reading WPF's own uncapped default, PositiveInfinity, which only holds
/// if DataGridColumnCap genuinely never assigns it one (see that fact's own
/// doc comment for why that isn't a vacuous default-value check).
///
/// What this suite CANNOT verify: that the four columns' own row heights on
/// a REAL, on-screen display look uniform end to end — TextColumnsTrimRatherThanWrap
/// measures ActualHeight on an off-screen window, which reflects real WPF
/// layout, just never painted; a person visually scanning the grid is not
/// what this proves. <see cref="AutoFitColumnTests"/> (this window's own
/// column-cap facts) and <see cref="DataGridColumnCapTests"/> (the class
/// itself, on a bare grid built in code) are the suites that exercise the
/// same underlying mechanism from other angles.</summary>
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

    /// <summary>Long enough to overflow any of the four governed columns'
    /// caps at this window's own default width, at any reasonable font
    /// size — the same reasoning AutoFitColumnTests.VeryLongValue documents
    /// for itself, restated locally since this class doesn't share that
    /// one.</summary>
    private const string VeryLongValue =
        "A-Very-Long-History-Derived-Value-That-Keeps-Going-Well-Past-Any-Sensible-Column-Width-000000000000.pdf";

    /// <summary>Table-rules Rule 4 (this branch) reverses the 2026-08-29
    /// decision this fact used to assert: the four text columns trimmed
    /// with an ellipsis before that date, moved to wrapping that day, and
    /// move BACK to trimming here — DataGridColumnCap's autofit still gives
    /// each its content width when that fits and a proportional share when
    /// it doesn't, but a share that can't hold its content now cuts it off
    /// with "…" rather than growing the row, and the cell's own full text
    /// reaches a ToolTip on hover instead of being left off screen.
    ///
    /// Read off the REALIZED cell on a seeded row carrying
    /// <see cref="VeryLongValue"/> in every governed field, not out of
    /// column.ElementStyle.Setters the way this fact used to: Rule 4 moved
    /// both TextTrimming and the tooltip mechanism onto GridCellText's own
    /// shared BasedOn base (Theme/Styles.xaml), so a Setter walk that never
    /// followed BasedOn would find neither on any of these four columns any
    /// more — the realized TextBlock's own effective properties are correct
    /// regardless of which Style in the chain actually supplied them, and
    /// checking them on an ACTUALLY-overflowing value proves the tooltip
    /// mechanism really fires here, not just that the property is set.</summary>
    [Theory]
    [InlineData("Name")]
    [InlineData("Destination")]
    [InlineData("Original")]
    [InlineData("Filed as")]
    public void TextColumnsTrimRatherThanWrap(string header) => _fx.Invoke(() =>
    {
        ThemeManager.Apply(_fx.App, dark: false);
        var dbPath = Path.Combine(Path.GetTempPath(), "ordo_test_history_" + Guid.NewGuid() + ".sqlite");
        var history = new History(dbPath);
        // originalName -> Original, newName -> FiledAs, nameEntered -> Name,
        // routeLabel -> Route/Destination (HistoryRow.From's own mapping) —
        // long in all four so every InlineData case exercises a genuinely
        // overflowing value, not just the one the current case names.
        history.LogCommit("c:\\in\\a.pdf", VeryLongValue, VeryLongValue, VeryLongValue,
            "insert", "", VeryLongValue, "c:\\out", tagged: false, "");
        var vm = new HistoryViewModel(history, new FakeDialogs(), new InlineWorkScheduler());
        var win = new HistoryWindow(vm)
        {
            WindowStartupLocation = WindowStartupLocation.Manual,
            Left = -20000, Top = 0, ShowActivated = false,
        };
        win.Show();
        win.UpdateLayout();
        try
        {
            var grid = FindDescendant<DataGrid>(win)
                ?? throw new InvalidOperationException("No DataGrid descendant found");
            var column = grid.Columns.OfType<DataGridTextColumn>()
                .FirstOrDefault(c => (string)c.Header == header)
                ?? throw new InvalidOperationException($"No '{header}' column found");
            var row = (DataGridRow)grid.ItemContainerGenerator.ContainerFromIndex(0);
            var text = Assert.IsType<TextBlock>(column.GetCellContent(row));

            Assert.Equal(TextTrimming.CharacterEllipsis, text.TextTrimming);
            Assert.Equal(TextWrapping.NoWrap, text.TextWrapping);

            var lineHeight = text.FontSize * text.FontFamily.LineSpacing;
            Assert.True(text.ActualHeight < 1.5 * lineHeight,
                $"{header}: a trimmed cell should stay on one line, not wrap onto more — " +
                $"the cell is {text.ActualHeight}px against a {lineHeight}px line");
            Assert.True(row.ActualHeight < 1.5 * lineHeight,
                $"{header}: the row should stay one line tall — trimming, not wrapping, " +
                $"absorbs the overflow now — the row is {row.ActualHeight}px against a {lineHeight}px line");

            Assert.Equal(text.Text, text.ToolTip as string);
        }
        finally { Cleanup(win, history, dbPath); }
    });

    /// <summary>When holds a timestamp History formats itself — bounded, 16
    /// characters — so it is no longer one of the governed columns: sized
    /// to its content, never asked to give way, never wrapped mid-date.
    /// An uncapped column's MaxWidth is WPF's default, infinity.
    ///
    /// Not a vacuous default-value check: <c>BuildWindow</c> does
    /// <c>Show()</c> plus <c>UpdateLayout()</c>, which is enough to run
    /// DataGridColumnCap's own <c>Recalculate</c> at least once — if When
    /// were still in the governed set (put <c>WhenColumn</c> back into the
    /// <c>Track</c> call in HistoryWindow.xaml.cs to check), that pass would
    /// assign it a real, finite cap, not leave WPF's default standing.
    /// Confirmed by that exact revert: a reviewer put WhenColumn back into
    /// Track and this fact caught it with a genuine 55px MaxWidth, not
    /// PositiveInfinity.</summary>
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
