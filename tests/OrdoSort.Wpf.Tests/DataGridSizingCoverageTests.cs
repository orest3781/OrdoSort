using System.Text.RegularExpressions;
using System.Windows;
using OrdoSort.Wpf.Windows;

namespace OrdoSort.Wpf.Tests;

/// <summary>The sizing half of the gap DataGridWindowCoverageTests closed for
/// colour.
///
/// That suite makes a missing CONTRAST builder loud and specific. Nothing did
/// the same for column SIZING, and the consequences are on the record rather
/// than hypothetical: PageCounts/ZipMerge/Unzip's Note and Result columns
/// shipped with no cap, and five grids shipped a star filler with no
/// MinWidth so it silently carried WPF's 20px default. Every one of those
/// was found by a person reading code or looking at a window — none by a
/// test going red.
///
/// AutoFitColumnTests measures real widths, and measures them well, but it is
/// a hand-maintained list of per-window facts: it only ever proves the windows
/// somebody remembered to write a case for. FilenameListWindow has never
/// appeared in it, and neither did the old ZipWindow in its whole lifetime.
/// That is the same failure mode DataGridSelectionContrastTests' own class doc
/// calls out for columns, one level up.
///
/// So these two facts are deliberately DERIVED, never hand-listed:
///
/// 1. <see cref="EveryWindowWithADataGridIsRegisteredForSizingCoverage"/> —
///    reflection finds every Window under OrdoSort.Wpf.Windows, reads its own
///    XAML off disk for a literal "&lt;DataGrid", and fails BY NAME for any
///    that isn't registered here. A new grid window is a candidate the moment
///    it exists, without anyone updating a list of window names.
///
/// 2. <see cref="EveryStarColumnDeclaresItsOwnFloor"/> — scans every window's
///    XAML for star columns and fails BY FILE AND HEADER for any without a
///    MinWidth. This is a source-level invariant with no layout involved, so
///    it holds for windows nobody has written a builder for — which is
///    exactly the population that keeps shipping broken.
///
/// What this suite deliberately does NOT do, said plainly rather than
/// oversold: it does not measure anything. It cannot tell you a column is the
/// right width, only that the declarations a correct width depends on are
/// present. Measured behaviour is AutoFitColumnTests' job; this makes that
/// suite's ABSENCE for a given window visible instead of silent.</summary>
public class DataGridSizingCoverageTests
{
    /// <summary>Windows whose column sizing is actually exercised by a
    /// measured fact in AutoFitColumnTests. Registering a window here is a
    /// claim that such a fact exists — fact 1 fails by name for a grid window
    /// missing from this set, and
    /// <see cref="RegisteredWindowsAreRealGridWindows"/> is the mirror check
    /// that nothing here has gone stale.
    ///
    /// FilenameListWindow is deliberately NOT here — it is the one grid
    /// window left in the app with zero measured sizing coverage. It sits in
    /// <see cref="KnownUncovered"/> below instead, so the gap is recorded as
    /// debt rather than left as an unexplained red suite. That debt got
    /// materially worse on 2026-08-19 (Task 9, filename-list-manifest): the
    /// window went from one column to six (#, Name, Size, Modified, Folder,
    /// Full path — five of them optional, toggled via a Columns ▾ menu), so
    /// the "nothing competes for width" premise this entry used to rest on
    /// no longer holds. See KnownUncovered's own note.
    ///
    /// ZipToolsWindow stands for what used to be ZipWindow and UnzipWindow;
    /// MergePdfsWindow stands for what used to be ZipMergeWindow. Each has
    /// its own AutoFitColumnTests facts measuring its own Result column.</summary>
    private static readonly HashSet<string> SizingCovered = new(StringComparer.Ordinal)
    {
        "MatchMergeWindow", "BulkRenameWindow", "HistoryWindow", "TriageWindow",
        "PageCountsWindow", "ZipToolsWindow", "MergePdfsWindow",
    };

    /// <summary>Known-uncovered grid windows, listed so the gap is explicit
    /// and greppable rather than an unexplained red suite. Deleting an entry
    /// here without adding the window to <see cref="SizingCovered"/> makes
    /// fact 1 fail by name, which is the point: this list is a debt register,
    /// not a permanent exemption.
    ///
    /// FilenameListWindow: used to be a single column, so nothing competed
    /// for width and there was no cap to test. That is no longer true (Task
    /// 9, 2026-08-19): the window now has six DataGridTextColumns, five of
    /// them optional and Visibility-toggled from code-behind. Unlike its
    /// siblings — BulkRename, History, MatchMerge, PageCounts, Triage all
    /// opt into DataGridColumnCap from their own constructors via a
    /// DataGridColumnCap.Track(grid, …) call — FilenameListWindow.xaml.cs
    /// makes NO such call, and no column here declares a MaxWidth. So the
    /// real debt is not "capped but unmeasured" like a covered window would
    /// be if its AutoFitColumnTests fact went stale; it is "no cap at all":
    /// at MinWidth, six columns compete for a 480px window with no arithmetic
    /// keeping any of them in bounds beyond their own MinWidth floors. FIVE
    /// of the six are Width="Auto" — #, Size, Modified, Folder and Full path,
    /// every column except the File name star filler — but only two of those
    /// can actually run away: Folder and Full path hold paths of no bounded
    /// length. # counts rows, Size is a byte count, and Modified renders
    /// through a fixed StringFormat that is always exactly 16 characters
    /// wide, so all three are content-bounded in the sense
    /// DataGridColumnCap's "untracked Auto" entitlement assumes. Recorded as
    /// debt rather than silently promoted to SizingCovered, since doing that
    /// honestly needs both a DataGridColumnCap.Track call (deliberately NOT
    /// added here — deferred) and a real AutoFitColumnTests fact proving it
    /// holds.
    ///
    /// MEASURED 2026-08-19, off-screen, one fresh window per state, 60 rows,
    /// long Folder/Full path values — recorded here because the obvious
    /// one-line fix (Track(NamesGrid, FolderColumn, FullPathColumn) in the
    /// constructor) turns out to need two decisions this branch is not the
    /// place to make:
    ///
    /// 1. THE FLOORS ALREADY OVERFLOW. The six columns' own MinWidths sum to
    ///    630 (30 + 180 + 60 + 120 + 100 + 140). At this window's declared
    ///    480 MinWidth the grid measures 436 wide, and DataGridColumnCap caps
    ///    against 436 - 17 (the reserved vertical scrollbar) = 419. So with
    ///    every column on at MinWidth, NO cap can deliver the
    ///    no-horizontal-scrollbar invariant every other window's
    ///    AutoFitColumnTests fact asserts — the floors alone exceed the
    ///    viewport by 211px. Untracked, that state measures Folder 451px and
    ///    Full path 888px against a File name squeezed to its 180 floor, 1735px
    ///    of columns in a 436px grid, horizontal scrollbar visible. Tracked, it
    ///    measures 830px total with both path columns at their floors: much
    ///    better, still overflowing. Landing a green fact needs a decision
    ///    about the floors or about the window's MinWidth, not just a cap.
    ///
    /// 2. A COLLAPSED COLUMN IS NOT FREE. DataGridColumn.ActualWidth for a
    ///    Collapsed column is NOT 0 — measured, it reports its MinWidth
    ///    (30/60/120/100/140 for the five optional columns here). This is the
    ///    first grid in the app whose columns collapse and reappear at
    ///    runtime, so nothing has needed DataGridColumnCap.EntitlementOf to
    ///    have an opinion about that before, and it does not: an untracked
    ///    Auto column contributes column.ActualWidth whether or not it paints
    ///    anything. Cost, measured with only Folder and Full path on at the
    ///    default 640 width: the cap comes out 84.5px per column, where the
    ///    three collapsed columns contributing 0 would make it 189.5px. The
    ///    fix is a visibility test in EntitlementOf — inert for the six
    ///    windows using it today, none of which collapse a column — but it is
    ///    a change to shared arithmetic that has taken six measured fix rounds
    ///    to reach its current shape, and on its own it does not resolve (1).
    ///
    /// Where the formula does work, for the record: at a 1200px window with
    /// every column on, Track yields a 361.5px cap for each path column,
    /// 1137px of columns in a 1156px grid, and no horizontal scrollbar.
    ///
    /// Follow-up work this task did not include.</summary>
    private static readonly HashSet<string> KnownUncovered = new(StringComparer.Ordinal)
    {
        "FilenameListWindow",
    };

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "OrdoSort.sln")))
            dir = dir.Parent;
        if (dir is null)
            throw new InvalidOperationException(
                "couldn't find OrdoSort.sln walking up from " + AppContext.BaseDirectory +
                " — this suite reads each window's XAML source directly off disk and needs " +
                "the repo checkout present alongside the built test assembly");
        return dir.FullName;
    }

    private static string XamlPath(string windowTypeName) =>
        Path.Combine(FindRepoRoot(), "src", "OrdoSort.Wpf", "Windows", windowTypeName + ".xaml");

    private static string XamlOf(string windowTypeName)
    {
        var path = XamlPath(windowTypeName);
        if (!File.Exists(path))
            throw new InvalidOperationException(
                $"{windowTypeName}: no XAML source found at {path} — a Window type was found via " +
                "reflection but this suite's Windows/{TypeName}.xaml path assumption didn't resolve it");
        return File.ReadAllText(path);
    }

    private static bool XamlHasDataGrid(string windowTypeName) =>
        XamlOf(windowTypeName).Contains("<DataGrid", StringComparison.Ordinal);

    private static List<string> AllWindowTypeNames() =>
        typeof(UnlockWindow).Assembly.GetTypes()
            .Where(t => t.Namespace == "OrdoSort.Wpf.Windows"
                && t.IsClass && !t.IsAbstract && t.IsPublic
                && typeof(Window).IsAssignableFrom(t))
            .Select(t => t.Name)
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToList();

    [Fact]
    public void EveryWindowWithADataGridIsRegisteredForSizingCoverage()
    {
        var windowTypes = AllWindowTypeNames();

        // Same sanity floor, and for the same reason, as
        // DataGridWindowCoverageTests': if reflection silently broke, an
        // empty list would make this vacuously pass — which is precisely the
        // failure mode this suite exists to close elsewhere.
        Assert.True(windowTypes.Count >= 10,
            $"only found {windowTypes.Count} Window types under OrdoSort.Wpf.Windows via reflection " +
            "— enumeration looks broken (namespace/assembly mismatch?), not that the app genuinely " +
            "shrank to that few windows");

        var unaccounted = windowTypes
            .Where(XamlHasDataGrid)
            .Where(name => !SizingCovered.Contains(name) && !KnownUncovered.Contains(name))
            .ToList();

        Assert.True(unaccounted.Count == 0,
            "these windows declare a <DataGrid> but have no measured sizing coverage and are not "
            + "recorded as known-uncovered — add a fact to AutoFitColumnTests (a long value stops at "
            + "its cap, and no horizontal scrollbar at the window's MinWidth) and register the name in "
            + "SizingCovered, or add it to KnownUncovered with a reason: "
            + string.Join(", ", unaccounted));
    }

    /// <summary>Mirror check: an entry in either set must still name a real
    /// grid window, so a rename or a removed DataGrid can't leave a stale
    /// entry quietly meaning nothing.</summary>
    [Fact]
    public void RegisteredWindowsAreRealGridWindows()
    {
        var stale = SizingCovered.Concat(KnownUncovered)
            .Where(name => !File.Exists(XamlPath(name)) || !XamlHasDataGrid(name))
            .ToList();

        Assert.True(stale.Count == 0,
            "these names are registered in this file but no longer name a window with a <DataGrid> "
            + "— the window was renamed, removed, or lost its grid: " + string.Join(", ", stale));
    }

    /// <summary>A star column with no MinWidth silently carries WPF's 20px
    /// default. That was harmless while the cap was a flat share of the
    /// viewport, because the filler only ever absorbed leftover width. It
    /// stopped being harmless when the cap became "the viewport minus
    /// everyone else's floor" (DataGridColumnCap): the filler's floor is now
    /// load-bearing arithmetic, and a 20px one lets a filename column
    /// collapse to nothing while a long error message takes the room.
    ///
    /// Five grids shipped exactly that — PageCounts, Unzip, ZipMerge, Zip and
    /// FilenameList — and nothing caught it, because a missing attribute is
    /// invisible to a suite that only measures the windows it has builders
    /// for. This reads the XAML instead, so it covers every window including
    /// the ones nobody has written a test for.</summary>
    [Fact]
    public void EveryStarColumnDeclaresItsOwnFloor()
    {
        // Matches a DataGrid*Column element up to its closing bracket, so
        // attributes split across lines are still one match.
        var columnPattern = new Regex(@"<DataGrid\w*Column\b[^>]*", RegexOptions.Singleline);
        var floorless = new List<string>();
        var windowTypes = AllWindowTypeNames();

        // The same sanity floor its sibling above carries, and it belongs here
        // just as much: this fact reads the app entirely through that
        // reflection call, so a namespace or assembly mismatch would empty the
        // loop and pass vacuously.
        Assert.True(windowTypes.Count >= 10,
            $"only found {windowTypes.Count} Window types under OrdoSort.Wpf.Windows via reflection "
            + "— enumeration looks broken (namespace/assembly mismatch?), not that the app genuinely "
            + "shrank to that few windows");

        foreach (var window in windowTypes.Where(XamlHasDataGrid))
        {
            foreach (Match column in columnPattern.Matches(XamlOf(window)))
            {
                var declaration = column.Value;
                if (!declaration.Contains("Width=\"*\"", StringComparison.Ordinal)) continue;
                if (declaration.Contains("MinWidth=", StringComparison.Ordinal)) continue;

                var header = Regex.Match(declaration, @"Header=""([^""]*)""");
                floorless.Add($"{window}: {(header.Success ? header.Groups[1].Value : "<no header>")}");
            }
        }

        Assert.True(floorless.Count == 0,
            "these star (filler) columns declare no MinWidth, so they carry WPF's 20px default — "
            + "the cap formula treats a filler's floor as the space it is entitled to keep, so "
            + "without one it can be squeezed to nothing: " + string.Join(" · ", floorless));
    }
}
