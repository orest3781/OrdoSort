using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using OrdoSort.Core;
using OrdoSort.Wpf.Theme;
using OrdoSort.Wpf.ViewModels;
using OrdoSort.Wpf.Windows;

namespace OrdoSort.Wpf.Tests;

/// <summary>2026-08-07 autofit-columns, Task 1 Step 6: MatchMergeWindow,
/// BulkRenameWindow and TriageWindow all moved from "some columns star,
/// unbounded" (or, for Triage, "every roster column plain Auto, unbounded")
/// to "content-sized (Auto) and capped, exactly one star filler with a
/// MinWidth". This suite proves the four things that shape is supposed to
/// guarantee, for each of the three grids that took the general shape
/// (History is covered separately — see DataGridStarColumnTests and
/// HistoryWindowXamlTests — because its own measurement, recorded in
/// HistoryWindow.xaml, kept Original/Filed-as on the OLD star+MinWidth shape
/// on purpose):
///
/// 1. A short value in a capped column measures narrow — it fits the
///    content, rather than stretching to fill a fixed/star share the way the
///    pre-fix File/Becomes/Current name columns did.
/// 2. A long value in a capped column stops exactly at that column's
///    MaxWidth rather than growing without bound, and still carries
///    TextTrimming/ToolTip so the capped-off text isn't simply lost.
/// 3. Every capped column in the grid can be at its LONGEST simultaneously
///    (the real worst case for available width) and the grid still shows no
///    horizontal scrollbar — proven by finding the DataGrid's own internal
///    ScrollViewer and reading ComputedHorizontalScrollBarVisibility, with
///    every capped column fed a value long enough to hit its cap.
///
/// What this suite does NOT attempt to prove headlessly: that the filler
/// column visually resolves to "whatever's left" and paints zero dead grey
/// space on a REAL window. DataGridStarColumnTests' own class doc already
/// established (2026-08-02) that headless Show()+UpdateLayout() never
/// resolves genuine star-column fair share at all — it deterministically
/// clamps every star column to its MinWidth instead, regardless of what's
/// tried in-process. That's the SAME mechanism this suite's own
/// GridHasExactlyOneStarFillerColumn fact below still confirms structurally
/// (Width="*", so WPF's own layout engine gives it 100% of whatever's left
/// the moment a real window resolves it) — combined with #3 above (the grid
/// doesn't overflow even with the filler pinned to its worst-case, smallest
/// floor width), a real window can only do better, never worse: the filler
/// gets AT LEAST its floor and never more demand than fits.</summary>
[Collection(HighlightContrastTests.Name)]
public class AutoFitColumnTests
{
    private readonly HighlightContrastFixture _fx;
    public AutoFitColumnTests(HighlightContrastFixture fx) => _fx = fx;

    /// <summary>Long enough to exceed every capped column's MaxWidth in this
    /// app (the smallest, Triage's roster columns, caps at 154px on the
    /// default panel width) at any reasonable font size, without relying on
    /// a real filesystem name length limit.</summary>
    private const string VeryLongValue =
        "A-Very-Long-Roster-Derived-Name-That-Keeps-Going-Well-Past-Any-Sensible-Column-Width-000000000000000000.pdf";

    private const string ShortValue = "a.pdf";

    // --------------------------------------------------------- MatchMerge

    [Fact]
    public void MatchMerge_ShortFileValueMeasuresNarrow() => _fx.Invoke(() =>
    {
        var win = BuildMatchMergeWindow(fileValue: ShortValue, noteValue: "");
        try
        {
            ShowOffscreen(win);
            var column = FindColumnByHeader(win, "File");
            // Well under the 0.35*840=294px cap: "a.pdf" needs a small
            // fraction of that — proving it fits the content rather than
            // reserving the old star column's arbitrary share of the window.
            Assert.True(column.ActualWidth < 100,
                $"MatchMerge File column with short content is {column.ActualWidth}px, expected < 100px");
        }
        finally { win.Close(); }
    });

    [Fact]
    public void MatchMerge_LongFileValueStopsAtTheCapWithEllipsisAndTooltip() => _fx.Invoke(() =>
    {
        var win = BuildMatchMergeWindow(fileValue: VeryLongValue, noteValue: "");
        try
        {
            ShowOffscreen(win);
            var column = FindColumnByHeader(win, "File");
            var expectedCap = win.Width * 0.35;
            Assert.True(column.ActualWidth == expectedCap,
                $"MatchMerge File column with long content is {column.ActualWidth}px, " +
                $"expected exactly its cap {expectedCap}px");
            AssertTrimmingAndTooltip((DataGridBoundColumn)column, "File");
        }
        finally { win.Close(); }
    });

    [Fact]
    public void MatchMerge_AllCappedColumnsAtWorstCaseStillFitWithNoHorizontalScrollbar() => _fx.Invoke(() =>
    {
        var win = BuildMatchMergeWindow(fileValue: VeryLongValue, noteValue: VeryLongValue);
        try
        {
            ShowOffscreen(win);
            AssertNoHorizontalScrollbar(win, "MatchMerge");
        }
        finally { win.Close(); }
    });

    [Fact]
    public void MatchMerge_HasExactlyOneStarFillerColumn() => _fx.Invoke(() =>
    {
        var win = BuildMatchMergeWindow(fileValue: ShortValue, noteValue: "");
        try
        {
            ShowOffscreen(win);
            var grid = FindDescendant<DataGrid>(win)!;
            var star = Assert.Single(grid.Columns.Where(c => c.Width.IsStar));
            Assert.Equal("Becomes", star.Header);
        }
        finally { win.Close(); }
    });

    private static MatchMergeWindow BuildMatchMergeWindow(string fileValue, string noteValue)
    {
        var vm = new MatchMergeViewModel(new Config(), _ => { }, new FakeDialogs());
        vm.Rows.Add(new MatchRow("src.pdf", fileValue, "SOMETHING-SHORT.pdf", noteValue, "merge"));
        return new MatchMergeWindow(vm);
    }

    // -------------------------------------------------------- BulkRename

    [Fact]
    public void BulkRename_ShortCurrentValueMeasuresNarrow() => _fx.Invoke(() =>
    {
        var win = BuildBulkRenameWindow(currentValue: ShortValue, noteValue: "");
        try
        {
            ShowOffscreen(win);
            var column = FindColumnByHeader(win, "Current name");
            // Threshold is looser than MatchMerge's analogous check: an Auto
            // column sizes to max(header, cell content), and "Current name"
            // is itself a longer header than "File" — still well under the
            // 0.35*820=287px cap either way.
            Assert.True(column.ActualWidth < 150,
                $"BulkRename Current name column with short content is {column.ActualWidth}px, expected < 150px");
        }
        finally { win.Close(); }
    });

    [Fact]
    public void BulkRename_LongCurrentValueStopsAtTheCapWithEllipsisAndTooltip() => _fx.Invoke(() =>
    {
        var win = BuildBulkRenameWindow(currentValue: VeryLongValue, noteValue: "");
        try
        {
            ShowOffscreen(win);
            var column = FindColumnByHeader(win, "Current name");
            var expectedCap = win.Width * 0.35;
            Assert.True(column.ActualWidth == expectedCap,
                $"BulkRename Current name column with long content is {column.ActualWidth}px, " +
                $"expected exactly its cap {expectedCap}px");
            AssertTrimmingAndTooltip((DataGridBoundColumn)column, "Current");
        }
        finally { win.Close(); }
    });

    [Fact]
    public void BulkRename_AllCappedColumnsAtWorstCaseStillFitWithNoHorizontalScrollbar() => _fx.Invoke(() =>
    {
        var win = BuildBulkRenameWindow(currentValue: VeryLongValue, noteValue: VeryLongValue);
        try
        {
            ShowOffscreen(win);
            AssertNoHorizontalScrollbar(win, "BulkRename");
        }
        finally { win.Close(); }
    });

    [Fact]
    public void BulkRename_HasExactlyOneStarFillerColumn() => _fx.Invoke(() =>
    {
        var win = BuildBulkRenameWindow(currentValue: ShortValue, noteValue: "");
        try
        {
            ShowOffscreen(win);
            var grid = FindDescendant<DataGrid>(win)!;
            var star = Assert.Single(grid.Columns.Where(c => c.Width.IsStar));
            Assert.Equal("New name  ·  click or press F2 to edit", star.Header);
        }
        finally { win.Close(); }
    });

    private static BulkRenameWindow BuildBulkRenameWindow(string currentValue, string noteValue)
    {
        var vm = new BulkRenameViewModel();
        vm.Preview.Add(new RenameRow("src.pdf", currentValue, "SOMETHING-SHORT.pdf", noteValue,
            changed: true, manual: false, needsName: false, editSeed: "SOMETHING-SHORT.pdf"));
        return new BulkRenameWindow(vm);
    }

    // ------------------------------------------------------------ Triage

    [Fact]
    public void Triage_ShortRosterValueMeasuresNarrow() => _fx.Invoke(() =>
    {
        var win = BuildTriageWindow(secondColumnValue: ShortValue);
        try
        {
            ShowOffscreen(win);
            var column = win.Candidates.Columns.First(c => (string)c.Header == "Control ID");
            // Cap is 0.35*440=154px; "a.pdf" needs a small fraction of that —
            // the header text ("Control ID") is what actually dominates an
            // Auto column's minimum here, same reasoning as BulkRename's
            // analogous check above.
            Assert.True(column.ActualWidth < 110,
                $"Triage Control ID column with short content is {column.ActualWidth}px, expected < 110px");
        }
        finally { win.Close(); }
    });

    [Fact]
    public void Triage_LongRosterValueStopsAtTheCapWithEllipsisAndTooltip() => _fx.Invoke(() =>
    {
        var win = BuildTriageWindow(secondColumnValue: VeryLongValue);
        try
        {
            ShowOffscreen(win);
            var column = win.Candidates.Columns.First(c => (string)c.Header == "Control ID");
            var expectedCap = 440 * 0.35;
            Assert.True(column.ActualWidth == expectedCap,
                $"Triage Control ID column with long content is {column.ActualWidth}px, " +
                $"expected exactly its cap {expectedCap}px");
            AssertTrimmingAndTooltip((DataGridBoundColumn)column, "Control ID");
        }
        finally { win.Close(); }
    });

    [Fact]
    public void Triage_AllCappedColumnsAtWorstCaseStillFitWithNoHorizontalScrollbar() => _fx.Invoke(() =>
    {
        var win = BuildTriageWindow(secondColumnValue: VeryLongValue);
        try
        {
            ShowOffscreen(win);
            AssertNoHorizontalScrollbar(win, "Triage");
        }
        finally { win.Close(); }
    });

    [Fact]
    public void Triage_FirstRosterColumnIsTheStarFillerColumn() => _fx.Invoke(() =>
    {
        var win = BuildTriageWindow(secondColumnValue: ShortValue);
        try
        {
            ShowOffscreen(win);
            var star = Assert.Single(win.Candidates.Columns.Where(c => c.Width.IsStar));
            Assert.Equal("First name", star.Header);
        }
        finally { win.Close(); }
    });

    /// <summary>Constructs a real TriageWindow and feeds Candidates a
    /// fabricated ItemsSource directly — bypassing ShowCurrentAsync/the real
    /// WebView2 entirely (never calling Loaded's async init path in any
    /// observable way before this measures). Dialogs is swapped to
    /// FakeDialogs and the window is always Close()d in the caller's
    /// finally, matching TriageWindowInitRaceTests' own proof that a pending
    /// real InitAndShowAsync continuation checks IsClosed before touching
    /// anything once Close() has already run — so even though Show() here
    /// does fire the real Loaded handler (and therefore does start a real,
    /// genuinely-async WebView2 init in the background), nothing from it can
    /// reach Candidates.ItemsSource or a real dialog before this method's own
    /// synchronous Show()+UpdateLayout()+measurement completes.</summary>
    private static TriageWindow BuildTriageWindow(string secondColumnValue)
    {
        var win = new TriageWindow(new List<MatchMerge.MatchResult>(), new[] { "First name", "Control ID" })
        {
            Dialogs = new FakeDialogs(),
        };
        win.Candidates.ItemsSource = new List<Dictionary<string, string>>
        {
            new() { ["First name"] = "Pat", ["Control ID"] = secondColumnValue },
        };
        return win;
    }

    // ---------------------------------------------------------- plumbing

    private static void ShowOffscreen(Window win)
    {
        win.WindowStartupLocation = WindowStartupLocation.Manual;
        win.Left = -20000;
        win.Top = 0;
        win.ShowActivated = false;
        win.Show();
        win.UpdateLayout();
        System.Windows.Threading.Dispatcher.CurrentDispatcher.Invoke(
            () => { }, System.Windows.Threading.DispatcherPriority.Render);
        win.UpdateLayout();
    }

    private static DataGridColumn FindColumnByHeader(Window win, string header)
    {
        var grid = FindDescendant<DataGrid>(win)
            ?? throw new InvalidOperationException("no DataGrid descendant found");
        return grid.Columns.FirstOrDefault(c => (string)c.Header == header)
            ?? throw new InvalidOperationException($"no '{header}' column found");
    }

    private static void AssertTrimmingAndTooltip(DataGridBoundColumn column, string bindingPathContains)
    {
        Assert.NotNull(column.ElementStyle);
        var trimSetter = column.ElementStyle!.Setters.OfType<Setter>()
            .FirstOrDefault(s => s.Property == TextBlock.TextTrimmingProperty);
        Assert.NotNull(trimSetter);
        Assert.Equal(TextTrimming.CharacterEllipsis, trimSetter!.Value);

        var tooltipSetter = column.ElementStyle!.Setters.OfType<Setter>()
            .FirstOrDefault(s => s.Property == FrameworkElement.ToolTipProperty);
        Assert.NotNull(tooltipSetter);
        var binding = Assert.IsType<Binding>(tooltipSetter!.Value);
        Assert.Contains(bindingPathContains, binding.Path.Path);
    }

    /// <summary>Finds the DataGrid's own internal ScrollViewer (its
    /// PART_..., the real control that decides whether a scrollbar renders)
    /// and asserts it never needs to show one horizontally — the real,
    /// meaningful proof that every capped column at its worst case (longest
    /// possible content, fed by each test's own setup) plus the filler
    /// column pinned to its headless worst case (its MinWidth floor — see
    /// this class's own doc comment for why that's the CONSERVATIVE case,
    /// not an optimistic one) still fits inside the grid's available
    /// width.</summary>
    private static void AssertNoHorizontalScrollbar(Window win, string windowName)
    {
        var grid = FindDescendant<DataGrid>(win)
            ?? throw new InvalidOperationException($"{windowName}: no DataGrid descendant found");
        var scrollViewer = FindDescendant<ScrollViewer>(grid)
            ?? throw new InvalidOperationException($"{windowName}: no ScrollViewer descendant found in the grid");
        Assert.NotEqual(Visibility.Visible, scrollViewer.ComputedHorizontalScrollBarVisibility);
    }

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
