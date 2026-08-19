using System.Reflection;
using System.Windows;
using OrdoSort.Wpf.Windows;

namespace OrdoSort.Wpf.Tests;

/// <summary>The gap this task was written to close: DataGridSelectionContrastTests
/// and DataGridNoteColourTests each ENUMERATE columns within windows they
/// already have a builder for — a window nobody added a builder for is
/// invisible to both, which is exactly how the five Tools-menu utilities
/// (Filename list, Zip, Merge PDFs from zip, Unzip, PDF page counts —
/// 2026-08-09) shipped a full day with zero coverage in either suite, and (as
/// this task's own investigation found — see DataGridSelectionContrastTests'
/// new builders) FOUR of those five windows' plain filler columns and one
/// window's status-tag column were all silently unreadable once selected,
/// the exact 4.5:1 failure GridCellTextSelectionAware exists to prevent.
///
/// This suite closes the DISCOVERY half of that gap, not the whole thing —
/// said plainly, not oversold: it does NOT write a new window's builder for
/// anyone. What it does do is make that builder's ABSENCE loud and specific
/// instead of silent. Two facts:
///
/// 1. <see cref="EveryWindowWithADataGridIsRegisteredForCoverage"/> finds
///    every concrete <see cref="Window"/> subtype under the
///    OrdoSort.Wpf.Windows namespace via REFLECTION over the shipped
///    assembly (not a hand-maintained list of window NAMES — the list of
///    windows to check is itself derived, so a new window file is
///    automatically a candidate the moment it's added), then reads that
///    window's OWN XAML source off disk and checks for a literal
///    "&lt;DataGrid" element — again not a hand-maintained "this window has
///    a DataGrid" table, which could itself go stale. A window that fails
///    BOTH those genuinely-automatic checks (declares a DataGrid, isn't in
///    <see cref="CoveredWindows"/>) fails this fact, BY NAME.
///
/// 2. <see cref="CoveredWindowsListNeverNamesAWindowWithoutADataGrid"/> is
///    the mirror check: every entry in <see cref="CoveredWindows"/> must
///    still have a real &lt;DataGrid&gt; in its own XAML, catching a STALE
///    entry (a window renamed, or one whose DataGrid was removed) before it
///    can quietly stop meaning anything.
///
/// <see cref="CoveredWindows"/> itself is still a hand-maintained set — there
/// is no way to verify FROM HERE that DataGridSelectionContrastTests'
/// builders genuinely exercise a window's every column without re-deriving
/// that suite's own logic, so the honest boundary of "automatic" drawn here
/// is: the next window with a DataGrid can no longer vanish from view the
/// way these five did — the first `dotnet test` after it's added fails,
/// naming it, until someone adds both a builder AND a line in
/// <see cref="CoveredWindows"/>. That is a materially smaller manual step
/// than "remember this suite exists and think to extend it," which is what
/// let the five-day gap happen in the first place.
///
/// Reads XAML SOURCE directly off disk (not the compiled BAML resource,
/// simpler and sufficient for a literal-substring check) by walking up from
/// <see cref="AppContext.BaseDirectory"/> to find OrdoSort.sln — this only
/// works with the repo checkout present alongside the built test assembly
/// (true for `dotnet test` run from within this repo, including a normal CI
/// checkout) and fails loudly, not silently, if the checkout can't be
/// found — a missing repo root must never be misread as "no window has a
/// DataGrid."</summary>
public class DataGridWindowCoverageTests
{
    /// <summary>Windows this task's own suites (DataGridSelectionContrastTests'
    /// builders, cross-checked against DataGridNoteColourTests for whichever
    /// of these also carry a status-coloured column) actually cover. Keep
    /// this in lock-step with DataGridSelectionContrastTests' own builder
    /// list by hand — see this class's own doc comment for why that's still
    /// the honest, unautomated half of this fix.</summary>
    private static readonly HashSet<string> CoveredWindows = new(StringComparer.Ordinal)
    {
        "MatchMergeWindow", "BulkRenameWindow", "HistoryWindow", "TriageWindow",
        // Tools windows added 2026-08-09 ("five Tools-menu utilities") —
        // this task's own coverage-gap fix. Zip, Unzip and Merge PDFs from
        // zip became ZipToolsWindow's two tabs on 2026-08-18; its builder
        // takes a tab flag and covers both grids' columns, so one entry here
        // stands for what used to be three.
        "FilenameListWindow", "PageCountsWindow", "ZipToolsWindow",
    };

    /// <summary>Walks up from the running test assembly's own directory to
    /// find the repo checkout — see this class's own doc comment for why a
    /// missing checkout must fail loudly rather than let the caller
    /// misinterpret "couldn't check" as "nothing to check."</summary>
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

    /// <summary>True if <paramref name="windowTypeName"/>'s own XAML source
    /// declares at least one real &lt;DataGrid&gt; element. A missing XAML
    /// file is itself a failure (thrown, not swallowed to false) — a Window
    /// type reflection found with no matching source file on disk means this
    /// check's own file-path assumption (Windows/{TypeName}.xaml) broke, not
    /// that the window has no DataGrid.</summary>
    private static bool XamlHasDataGrid(string windowTypeName)
    {
        var path = Path.Combine(FindRepoRoot(), "src", "OrdoSort.Wpf", "Windows", windowTypeName + ".xaml");
        if (!File.Exists(path))
            throw new InvalidOperationException(
                $"{windowTypeName}: no XAML source found at {path} — a Window type was found via " +
                "reflection but this suite's Windows/{TypeName}.xaml path assumption didn't resolve it");
        return File.ReadAllText(path).Contains("<DataGrid", StringComparison.Ordinal);
    }

    /// <summary>Every concrete, public Window subtype under the
    /// OrdoSort.Wpf.Windows namespace, found via reflection over the actual
    /// shipped assembly — not a list this suite maintains by hand, so a new
    /// window file is automatically a candidate the next time this runs.</summary>
    private static List<string> AllWindowTypeNames() =>
        typeof(UnlockWindow).Assembly.GetTypes()
            .Where(t => t.Namespace == "OrdoSort.Wpf.Windows"
                && t.IsClass && !t.IsAbstract && t.IsPublic
                && typeof(Window).IsAssignableFrom(t))
            .Select(t => t.Name)
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToList();

    [Fact]
    public void EveryWindowWithADataGridIsRegisteredForCoverage()
    {
        var windowTypes = AllWindowTypeNames();

        // Sanity on the reflection step itself: if this suite's own
        // namespace/assembly assumptions ever silently broke (a namespace
        // rename, a build config that hides types), an empty or tiny list
        // would make every fact below vacuously pass — proving nothing,
        // exactly the trap this whole task exists to close on the OTHER
        // suites. Fourteen window types is the actual count at the time this
        // suite was last updated, seven of which declare a DataGrid
        // (BulkRename/FilenameList/History/MatchMerge/PageCounts/Triage/
        // ZipTools — AboutWindow/LabelMakerWindow/ListReformatWindow/
        // ManageSavedWindow/PrintPreviewWindow/SettingsWindow/UnlockWindow
        // don't; Turnaround and Production, which also used to, were removed
        // along with the reports feature, and Zip/Unzip/ZipMerge became
        // ZipToolsWindow's two tabs), so eight is a safe floor that still
        // catches enumeration silently degrading without being so tight it
        // breaks on every ordinary new window.
        Assert.True(windowTypes.Count >= 8,
            $"only found {windowTypes.Count} Window types under OrdoSort.Wpf.Windows via reflection " +
            "— enumeration looks broken (namespace/assembly mismatch?), not that the app genuinely " +
            "shrank to that few windows");

        var missing = windowTypes.Where(name => XamlHasDataGrid(name) && !CoveredWindows.Contains(name)).ToList();

        Assert.True(missing.Count == 0,
            "these windows declare a <DataGrid> in their own XAML but are not in this file's own " +
            "CoveredWindows set — add a builder to DataGridSelectionContrastTests (and, if the grid " +
            "carries a status-coloured column, DataGridNoteColourTests) and register the window name " +
            "here, or this suite's own coverage silently stops at whatever window someone last " +
            "remembered, the exact gap this task exists to close: " + string.Join(", ", missing));
    }

    [Fact]
    public void CoveredWindowsListNeverNamesAWindowWithoutADataGrid()
    {
        // The mirror direction: a stale CoveredWindows entry (a window
        // renamed, or one whose only DataGrid was removed) would otherwise
        // sit there forever looking like real coverage while checking
        // nothing — the exact "test passes for the wrong reason" trap this
        // whole task's brief calls out.
        var stale = CoveredWindows.Where(name => !XamlHasDataGrid(name)).ToList();

        Assert.True(stale.Count == 0,
            "these windows are listed in CoveredWindows but their own XAML has no <DataGrid> any more " +
            "— stale entry, fix or remove it: " + string.Join(", ", stale));
    }
}
