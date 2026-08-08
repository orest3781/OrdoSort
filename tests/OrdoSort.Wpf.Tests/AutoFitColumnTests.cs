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
/// gets AT LEAST its floor and never more demand than fits.
///
/// FIX ROUND 1 (2026-08-07): every Triage fact originally set
/// Candidates.ItemsSource directly, which never runs ShowCurrentAsync and
/// therefore never inserts the "Why" column — a real, fixed-260px column
/// the real app DOES show alongside roster columns for any suggested-status
/// item, a normal queue per MatchMerge.cs, not a rare one. That gap let a
/// real default-state overflow (Why + First name + Control ID totaling
/// 422.5px against a 416px panel) ship past a fully green suite. Every
/// Triage fact below now drives the real ShowCurrentAsync — see
/// BuildTriageWindow/ShowOffscreenAndDriveCurrent — and two facts assert
/// ComputedHorizontalScrollBarVisibility directly with Why present, at both
/// the app's default 2 roster columns and at 3.</summary>
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
    //
    // Fix round 1 finding: the first version of these tests set
    // Candidates.ItemsSource directly, bypassing ShowCurrentAsync entirely —
    // which meant the "Why" column (ShowCurrentAsync inserts it, fixed
    // 260px, whenever the current item's Status is "suggested" — a normal,
    // common queue per MatchMerge.cs, not a rare one) was NEVER present in
    // any of these tests, even though the real app can and does show it
    // alongside roster columns in the SAME 440px side panel. That's how a
    // real default-state overflow (Why=251.5 + First name=90 + Control
    // ID=81 = 422.5px against a 416px panel, scrollbar VISIBLE) shipped past
    // a green suite. Every Triage test below now drives the real
    // ShowCurrentAsync — see BuildTriageWindow/ShowOffscreenAndDriveCurrent.

    /// <summary>Reproduces TriageWindow's own internal budget formula (see
    /// its constructor) so these tests double as a live check that the
    /// production constants (WhyColumnWidth/PanelHorizontalMargins/
    /// SafetyMargin/FillerMinWidth, duplicated here — same pattern as
    /// MatchMerge/BulkRename's tests inlining their 0.35 share) and this
    /// test's own expectation can't silently drift apart.</summary>
    private static double ExpectedTriageColumnCap(int headerCount, bool couldShowWhy)
    {
        const double whyColumnWidth = 260;
        const double panelHorizontalMargins = 24;
        const double safetyMargin = 20;
        const double fillerMinWidth = 60;
        var rosterBudget = Math.Max(fillerMinWidth, 440 - panelHorizontalMargins
            - safetyMargin - (couldShowWhy ? whyColumnWidth : 0));
        var cappedColumnCount = Math.Max(1, headerCount - 1);
        return Math.Max(20, (rosterBudget - fillerMinWidth) / cappedColumnCount);
    }

    [Fact]
    public void Triage_ShortRosterValueMeasuresNarrow() => _fx.Invoke(() =>
    {
        var win = BuildTriageWindow(new[] { "First name", "Control ID" }, suggested: false, ShortValue);
        try
        {
            ShowOffscreenAndDriveCurrent(win);
            var column = win.Candidates.Columns.First(c => (string)c.Header == "Control ID");
            // No "Why" in this batch (ambiguous only) — the wide, no-Why cap
            // (336px, see ExpectedTriageColumnCap) applies; "a.pdf" needs a
            // small fraction of it regardless.
            Assert.True(column.ActualWidth < 110,
                $"Triage Control ID column with short content is {column.ActualWidth}px, expected < 110px");
        }
        finally { win.Close(); }
    });

    [Fact]
    public void Triage_LongRosterValueStopsAtTheCapWithEllipsisAndTooltip() => _fx.Invoke(() =>
    {
        var win = BuildTriageWindow(new[] { "First name", "Control ID" }, suggested: false, VeryLongValue);
        try
        {
            ShowOffscreenAndDriveCurrent(win);
            var column = win.Candidates.Columns.First(c => (string)c.Header == "Control ID");
            var expectedCap = ExpectedTriageColumnCap(headerCount: 2, couldShowWhy: false);
            Assert.True(column.ActualWidth == expectedCap,
                $"Triage Control ID column with long content is {column.ActualWidth}px, " +
                $"expected exactly its cap {expectedCap}px");
            AssertTrimmingAndTooltip((DataGridBoundColumn)column, "Control ID");
        }
        finally { win.Close(); }
    });

    [Fact]
    public void Triage_FirstRosterColumnIsTheStarFillerColumn() => _fx.Invoke(() =>
    {
        var win = BuildTriageWindow(new[] { "First name", "Control ID" }, suggested: false, ShortValue);
        try
        {
            ShowOffscreenAndDriveCurrent(win);
            var star = Assert.Single(win.Candidates.Columns.Where(c => c.Width.IsStar));
            Assert.Equal("First name", star.Header);
        }
        finally { win.Close(); }
    });

    /// <summary>The Critical from fix round 1, made concrete: the app's own
    /// default roster picks ("nothing ticked = the name and id columns" —
    /// two headers) reviewing a SUGGESTED item — the common queue, not an
    /// edge case — with every capped column and Why itself all fed enough
    /// content to reach their respective caps. This is the exact
    /// configuration the reviewer measured overflowing (422.5px against
    /// 416px) against the pre-fix budget.</summary>
    [Fact]
    public void Triage_DefaultTwoRosterColumnsWithWhyPresentStillFitWithNoHorizontalScrollbar() => _fx.Invoke(() =>
    {
        var win = BuildTriageWindow(new[] { "First name", "Control ID" }, suggested: true, VeryLongValue);
        try
        {
            ShowOffscreenAndDriveCurrent(win);
            // Why is actually present — otherwise this test would silently
            // stop meaning anything, the same way the pre-fix version did.
            Assert.Contains(win.Candidates.Columns, c => (string)c.Header == "Why");
            AssertNoHorizontalScrollbar(win, "Triage (2 roster columns, Why present)");
        }
        finally { win.Close(); }
    });

    /// <summary>The "as roster columns are added" half of the fix: a third
    /// header (someone ticked an extra optional roster column in "Show in
    /// Review matches") sharing the same panel with Why, both capped roster
    /// columns fed enough content to reach their now-smaller caps.</summary>
    [Fact]
    public void Triage_ThreeRosterColumnsWithWhyPresentStillFitWithNoHorizontalScrollbar() => _fx.Invoke(() =>
    {
        var win = BuildTriageWindow(new[] { "First name", "Control ID", "Extra field" }, suggested: true,
            VeryLongValue, VeryLongValue);
        try
        {
            ShowOffscreenAndDriveCurrent(win);
            Assert.Contains(win.Candidates.Columns, c => (string)c.Header == "Why");
            AssertNoHorizontalScrollbar(win, "Triage (3 roster columns, Why present)");
        }
        finally { win.Close(); }
    });

    /// <summary>Builds a real TriageWindow with one real MatchMerge.MatchResult
    /// (status "suggested" or "ambiguous", matching whether the caller wants
    /// Why present) and a real candidate row — headers[0] always gets a
    /// short, fixed value ("Pat", the filler column), and each subsequent
    /// header gets the corresponding entry from <paramref name="columnValues"/>.
    /// Dialogs is swapped to FakeDialogs defensively; the window is always
    /// Close()d in the caller's finally.</summary>
    private static TriageWindow BuildTriageWindow(string[] headers, bool suggested, params string[] columnValues)
    {
        var row = new Dictionary<string, string> { [headers[0]] = "Pat" };
        for (var i = 1; i < headers.Length; i++) row[headers[i]] = columnValues[i - 1];
        var candidate = new MatchMerge.Candidate("1", row);

        var item = suggested
            ? new MatchMerge.MatchResult("doc.pdf", "suggested",
                Suggestions: new List<MatchMerge.Suggestion> { new(candidate, "token match") })
            : new MatchMerge.MatchResult("doc.pdf", "ambiguous",
                Candidates: new List<MatchMerge.Candidate> { candidate });

        return new TriageWindow(new List<MatchMerge.MatchResult> { item }, headers)
        {
            Dialogs = new FakeDialogs(),
        };
    }

    /// <summary>Show()s off-screen (firing the real Loaded handler, and
    /// therefore starting a real, genuinely-async WebView2 init in the
    /// background — proven safe across repeated runs during fix round 1),
    /// then drives the SAME ShowCurrentAsync the app's own Loaded flow
    /// eventually calls, directly and deterministically, rather than waiting
    /// on that real init to resolve. ShowCurrentAsync awaits
    /// _pdf.ShowAsync(...), which no-ops synchronously (returns
    /// Task.CompletedTask) as long as the WebView2 was never actually
    /// initialized — true here, since that's the genuinely-async part still
    /// pending in the background — so GetAwaiter().GetResult() is
    /// synchronous-safe, not a deadlock risk: same reasoning
    /// TriageWindowDisposalTests' class doc already established for the
    /// identical pattern.</summary>
    private static void ShowOffscreenAndDriveCurrent(TriageWindow win)
    {
        win.WindowStartupLocation = WindowStartupLocation.Manual;
        win.Left = -20000;
        win.Top = 0;
        win.ShowActivated = false;
        win.Show();
#pragma warning disable xUnit1031
        win.ShowCurrentAsync().GetAwaiter().GetResult();
#pragma warning restore xUnit1031
        win.UpdateLayout();
        System.Windows.Threading.Dispatcher.CurrentDispatcher.Invoke(
            () => { }, System.Windows.Threading.DispatcherPriority.Render);
        win.UpdateLayout();
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
