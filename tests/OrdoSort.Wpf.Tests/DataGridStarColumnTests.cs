using System.Windows;
using System.Windows.Controls;
using OrdoSort.Core;
using OrdoSort.Wpf.Theme;
using OrdoSort.Wpf.ViewModels;
using OrdoSort.Wpf.Windows;

namespace OrdoSort.Wpf.Tests;

/// <summary>2026-08-02 audit-remediation, Task 1: HistoryWindow's Original/
/// Filed-as columns and BulkRenameWindow's Current-name/New-name columns all
/// declared <c>Width="*"</c> with no explicit <c>MinWidth</c> — so
/// <see cref="DataGridColumn.MinWidth"/> fell back to WPF's own default of
/// 20px. Measured (headless, matching how <c>tools/OrdoSort.Smoke</c>'s
/// Screenshots.cs shows every window it captures): a single Show()+
/// UpdateLayout() pass off-screen leaves every star column frozen at exactly
/// 20px, hiding most of the original/new filename. Eight further pumped
/// UpdateLayout() calls (and, independently re-tried while building this
/// suite: toggling each column's Width and nudging Window.Width by ±1px, both
/// from code, both on- and off-screen) all left them frozen too — apparently
/// WPF's DataGrid only recomputes star widths in response to something a
/// genuine, OS-driven resize supplies that no in-process property change
/// reproduces here.
///
/// IMPORTANT CONTEXT this task's on-screen investigation established: a REAL
/// interactively-shown window does NOT reproduce the 20px collapse at all —
/// confirmed by screenshotting the live app against demo-full, both with 0
/// and with 5 history rows, both windows resolved to their genuine fair share
/// (~222px/~277px) at the default window size. So the 20px collapse is
/// specific to a Show() that's never part of a real desktop message loop
/// (this app's own headless Screenshots.cs QA tool; any other headless/CI
/// render) — at the DEFAULT window size, end users never see it.
///
/// The MinWidth="120" floor is NOT merely a QA-harness accommodation, though —
/// a follow-up on-screen measurement (real app, real UI Automation
/// TransformPattern.Resize, HistoryWindow shrunk to its own declared
/// MinWidth="700") found it also protects a real user at the narrow end:
/// pre-fix, shrinking a real, on-screen History window to 700px wide fair-
/// shares Original/Filed-as down to 82px each (measured via each
/// DataGridColumnHeader's UI-Automation BoundingRectangle) — legible, but
/// noticeably tight. Post-fix, the identical real resize clamps them to the
/// 120px floor instead (a genuine ~46% improvement at the narrowest real
/// window size), at the honest cost of proportionally compressing the
/// "fixed"-width columns to still fit 700px (When 140→121, Name 160→141,
/// Route 120→101, Undone 70→51 — enough that Undone's own header text
/// visibly truncates to "Undo" at this extreme). So real users who work with
/// a shrunk History window benefit from this floor too, not just this
/// project's own screenshot tooling.
///
/// The fix shipped is therefore the first half of the brief's preferred
/// option: a sensible explicit MinWidth="120" on each star column — not a
/// code-behind "force recompute" trick, which was tried (see above) and
/// measured not to work. Full details, including the negative results and
/// the narrow-window measurement, in
/// .superpowers/sdd/2026-08-02-audit-remediation/task-1-report.md.
///
/// This suite still matters despite real users being unaffected at the
/// default window size: Screenshots.cs (and this test) construct windows
/// exactly the way that headless tool does — so without the MinWidth floor,
/// this project's OWN QA screenshots of these two windows would keep
/// silently showing hidden filenames, and any future headless/CI rendering
/// of them would too.
///
/// NAMING NOTE (QC follow-up): because the fix makes headless ActualWidth
/// deterministically exactly MinWidth, an assertion like "ActualWidth > 100"
/// is mathematically equivalent to "is MinWidth set above 100" — a
/// compile-time XAML fact, not a runtime layout verification. It still
/// catches a real regression (MinWidth accidentally lowered or removed), so
/// it isn't vacuous, but a name like "StarColumnsGetTheirShareOfWidth" would
/// overstate what it checks: this suite CANNOT assert genuine fair-share
/// resolution headlessly (that's the whole point above — it doesn't happen
/// here), only that the floor holds. Named and pinned accordingly below.
///
/// [Collection(HighlightContrastTests.Name)], NOT its own
/// IClassFixture&lt;HighlightContrastFixture&gt;: a second, independently-
/// constructed instance races/collides with HighlightContrastTests' — see
/// HighlightContrastFixture's own class doc for the two distinct crashes that
/// reproduced empirically here before this was joined to the shared
/// collection.</summary>
[Collection(HighlightContrastTests.Name)]
public class DataGridStarColumnTests
{
    /// <summary>The exact pixel floor HistoryWindow.xaml/BulkRenameWindow.xaml
    /// set via <c>MinWidth="120"</c> on their star columns. Asserted for
    /// equality (not just "&gt; 100") so this test fails with a concrete,
    /// honest number if that floor is ever lowered, removed, or the headless
    /// clamp behaviour this suite documents ever changes — see the class doc
    /// above for why genuine fair-share resolution can't be asserted here.</summary>
    private const double ExpectedFloorPx = 120;

    private readonly HighlightContrastFixture _fx;
    public DataGridStarColumnTests(HighlightContrastFixture fx) => _fx = fx;

    [Theory]
    [InlineData("History")]
    [InlineData("BulkRename")]
    public void StarColumnsNeverCollapseBelowTheirMinimum(string windowName) => _fx.Invoke(() =>
    {
        ThemeManager.Apply(_fx.App, dark: false);

        History? history = null;
        string? dbPath = null;
        Window? win = null;
        try
        {
            if (windowName == "History")
            {
                dbPath = Path.Combine(Path.GetTempPath(), "ordo_test_history_" + Guid.NewGuid() + ".sqlite");
                history = new History(dbPath);
                // Five populated rows — the brief's own measurement found the
                // collapse identical with 0 and with 5 rows, but real filenames
                // exercise the exact shape a user would actually see.
                for (var i = 0; i < 5; i++)
                {
                    history.LogCommit(
                        originalPath: $@"C:\inbox\2026010{i}--{i}{i}{i}{i}{i}{i}{i}{i}{i}{i}.pdf",
                        originalName: $"2026010{i}--{i}{i}{i}{i}{i}{i}{i}{i}{i}{i}.pdf",
                        newName: $"MAXIMILLIAN-STRAVINSKY-ORLOWSKI-{i}.pdf",
                        nameEntered: $"MAXIMILLIAN STRAVINSKY ORLOWSKI {i}",
                        namingMode: "replace", suffixApplied: "",
                        routeLabel: "Filed", routePath: @"C:\dest\filed",
                        tagged: false, collisionSuffix: "");
                }
                // InlineWorkScheduler: HistoryViewModel's constructor kicks off
                // an async LoadAsync — inline makes it finish synchronously
                // (an already-completed awaited Task resumes without yielding),
                // so Rows/FooterText are populated the moment this returns, no
                // pumping needed.
                var vm = new HistoryViewModel(history, new FakeDialogs(), new InlineWorkScheduler());
                win = new HistoryWindow(vm);
            }
            else
            {
                win = new BulkRenameWindow(new BulkRenameViewModel());
            }

            // Exactly Screenshots.cs's ShowOffscreen: off the visible desktop,
            // not activated, one Show() + one UpdateLayout() — the precise
            // shape that measured 20px, not a synthetic worst case.
            win.WindowStartupLocation = WindowStartupLocation.Manual;
            win.Left = -20000;
            win.Top = 0;
            win.ShowActivated = false;
            win.Show();
            win.UpdateLayout();

            var grid = FindDescendant<DataGrid>(win)
                ?? throw new InvalidOperationException($"{windowName}: no DataGrid descendant found");
            var starColumns = grid.Columns.Where(c => c.Width.IsStar).ToList();
            Assert.True(starColumns.Count > 0,
                $"{windowName}: no star (Width=\"*\") columns found — test setup doesn't match the XAML");

            // Pinned to the exact expected floor, not a loose ">100": headless
            // Show()+UpdateLayout() never resolves genuine fair-share width
            // (see class doc) — it deterministically clamps every star column
            // to MinWidth. Asserting that exact value catches a real
            // regression (the floor lowered or dropped) with a concrete
            // number, rather than a vague threshold a much smaller floor
            // could also satisfy.
            foreach (var col in starColumns)
                Assert.True(col.ActualWidth == ExpectedFloorPx,
                    $"{windowName}: star column '{col.Header}' is {col.ActualWidth}px " +
                    $"(expected exactly {ExpectedFloorPx}px, its MinWidth — MinWidth is {col.MinWidth})");
        }
        finally
        {
            try { win?.Close(); } catch { /* best effort */ }
            history?.Dispose();
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            if (dbPath is not null) { try { File.Delete(dbPath); } catch { /* best effort */ } }
        }
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
