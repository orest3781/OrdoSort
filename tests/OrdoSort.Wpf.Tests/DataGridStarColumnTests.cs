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
/// interactively-shown window does NOT reproduce this at all — confirmed by
/// screenshotting the live app against demo-full, both with 0 and with 5
/// history rows, both windows resolved to their genuine fair share (~222px/
/// ~277px). So the 20px collapse is specific to a Show() that's never part of
/// a real desktop message loop (this app's own headless Screenshots.cs QA
/// tool; any other headless/CI render) — end users never see it. The fix
/// shipped is therefore the first half of the brief's preferred option: a
/// sensible explicit <c>MinWidth="120"</c> on each star column, so the one
/// path that IS affected still shows a legible filename prefix instead of
/// hiding it — not a code-behind "force recompute" trick, which was tried
/// (see above) and measured not to work. Full details, including the
/// negative results, in
/// .superpowers/sdd/2026-08-02-audit-remediation/task-1-report.md.
///
/// This suite still matters despite real users being unaffected: Screenshots.cs
/// (and this test) construct windows exactly the way that headless tool
/// does — so without the MinWidth floor, this project's OWN QA screenshots of
/// these two windows would keep silently showing hidden filenames, and any
/// future headless/CI rendering of them would too.
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
    private readonly HighlightContrastFixture _fx;
    public DataGridStarColumnTests(HighlightContrastFixture fx) => _fx = fx;

    [Theory]
    [InlineData("History")]
    [InlineData("BulkRename")]
    public void StarColumnsGetTheirShareOfWidth(string windowName) => _fx.Invoke(() =>
    {
        ThemeManager.Apply(_fx.App, dark: false);

        History? history = null;
        string? dbPath = null;
        Window win;
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

            foreach (var col in starColumns)
                Assert.True(col.ActualWidth > 100,
                    $"{windowName}: star column '{col.Header}' is {col.ActualWidth}px (MinWidth is {col.MinWidth})");

            win.Close();
        }
        finally
        {
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
