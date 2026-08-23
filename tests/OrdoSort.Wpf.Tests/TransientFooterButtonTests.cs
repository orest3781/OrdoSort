using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.Data.Sqlite;
using OrdoSort.Core;
using OrdoSort.Wpf.Theme;
using OrdoSort.Wpf.ViewModels;
using OrdoSort.Wpf.Windows;

namespace OrdoSort.Wpf.Tests;

/// <summary>Three footers hide a button until it has a job — Unlock's and Bulk
/// rename's Cancel (the batch's brake, shown only while a batch runs) and
/// History's "Show all". Each is bound through BooleanToVisibilityConverter,
/// which yields <c>Collapsed</c>: the button takes no space at rest, so the
/// moment it appears the row re-lays-out around it.
///
/// Whether that MOVES anything depends entirely on the panel, and the answer is
/// not guessable from reading the XAML — the 2026-08-22 UI audit got it wrong
/// twice by trying (UI-07). The rules that actually decide it:
///
///   * a LEFT-aligned StackPanel grows rightward, so inserting a child only
///     displaces the children AFTER it;
///   * a RIGHT-aligned StackPanel has its right edge pinned, so inserting a
///     child displaces the children BEFORE it, leftward;
///   * a DockPanel's docked children each take from the remaining rect in
///     declaration order, so a collapsed one costs its neighbours nothing and
///     the fill child absorbs the whole difference.
///
/// Unlock's footer is the right-aligned case AND its Cancel sits second of
/// four, which is the combination that bites: pressing Unlock starts the batch,
/// Cancel appears, and the primary button slides 104px left — leaving 87% of
/// the pixels under the cursor occupied by Cancel, on a button that is still
/// enabled. A double-click, or a slow release, cancels the batch it just
/// started.
///
/// This asserts the property that matters rather than the mechanism: when a
/// transient button appears, no OTHER button in that footer moves. It is
/// deliberately indifferent to how that is achieved, so a later re-layout that
/// keeps the guarantee does not have to update the test.
///
/// MainWindow's Refresh button is the fourth site the audit named and is not
/// covered here: showing MainWindow starts a real WebView2/Edge process on
/// Loaded (see HighlightContrastFixture's own notes), and a test that hangs on
/// browser teardown is worse than the defect it guards. Its header was measured
/// by hand instead — the combo beside Refresh is gated on TileControlsVisible
/// and goes at the same moment, so nothing is left to be displaced.</summary>
[Collection(HighlightContrastTests.Name)]
public class TransientFooterButtonTests
{
    private readonly HighlightContrastFixture _fx;
    public TransientFooterButtonTests(HighlightContrastFixture fx) => _fx = fx;

    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        var n = VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < n; i++)
        {
            var c = VisualTreeHelper.GetChild(root, i);
            yield return c;
            foreach (var d in Descendants(c)) yield return d;
        }
    }

    /// <summary>A stable, human-readable name for a button, so a failure says
    /// which one moved rather than quoting an index.</summary>
    private static string Label(Button b)
    {
        // AutomationProperties.Name defaults to "" rather than null, so a
        // plain ?? chain silently reports every unnamed button as blank.
        var auto = (string?)b.GetValue(AutomationProperties.NameProperty);
        if (!string.IsNullOrWhiteSpace(auto)) return auto;
        return b.Content as string is { Length: > 0 } content ? content : "<unnamed>";
    }

    public static TheoryData<string> Sites() => new() { "Unlock", "BulkRename", "History" };

    private static (Window Window, Button Transient, Action? Cleanup) Build(string site)
    {
        switch (site)
        {
            case "Unlock":
            {
                var vm = new UnlockViewModel(new Config(), () => true);
                vm.Files.Add(new UnlockFileRow(@"C:\inbox\a-locked-document.pdf"));
                var w = new UnlockWindow(vm);
                return (w, FindByContent(w, "Cancel"), null);
            }
            case "BulkRename":
            {
                var vm = new BulkRenameViewModel();
                vm.Preview.Add(new RenameRow(@"C:\inbox\before.pdf", "before.pdf", "after.pdf", "",
                    changed: true, manual: false, needsName: false, editSeed: "after.pdf",
                    noteIsProblem: false));
                var w = new BulkRenameWindow(vm);
                return (w, FindByContent(w, "Cancel"), null);
            }
            case "History":
            {
                var db = Path.Combine(Path.GetTempPath(), "ordo_transient_" + Guid.NewGuid() + ".sqlite");
                var history = new History(db);
                history.LogCommit(@"c:\in\a.pdf", "a.pdf", "A.pdf", "A",
                    "insert", "", "Invoices", @"c:\out", tagged: false, "");
                var vm = new HistoryViewModel(history, new FakeDialogs(), new InlineWorkScheduler());
                var w = new HistoryWindow(vm);
                return (w, FindByContent(w, "Show all"), () =>
                {
                    history.Dispose();
                    SqliteConnection.ClearAllPools();
                    try { File.Delete(db); } catch { /* best effort */ }
                });
            }
            default: throw new ArgumentOutOfRangeException(nameof(site), site, "unknown site");
        }
    }

    private static Button FindByContent(Window w, string content)
    {
        // the window has to be through a layout pass before its visual tree exists
        w.Left = -20000; w.Top = 0; w.ShowActivated = false;
        w.WindowStartupLocation = WindowStartupLocation.Manual;
        w.Show();
        w.UpdateLayout();
        return Descendants(w).OfType<Button>().FirstOrDefault(b => b.Content as string == content)
            ?? throw new InvalidOperationException(
                $"no button with Content=\"{content}\" in {w.GetType().Name} — the footer changed, " +
                "and this suite's premise with it.");
    }

    [Theory, MemberData(nameof(Sites))]
    public void ShowingATransientButtonMovesNoOtherButton(string site) => _fx.Invoke(() =>
    {
        ThemeManager.Apply(_fx.App, dark: false);
        var (w, transient, cleanup) = Build(site);
        try
        {
            Assert.Equal(Visibility.Collapsed, transient.Visibility);

            double X(Button b) => b.TransformToAncestor(w).Transform(new Point(0, 0)).X;
            var others = Descendants(w).OfType<Button>()
                .Where(b => !ReferenceEquals(b, transient) && b.IsVisible)
                .ToList();
            Assert.NotEmpty(others);
            var before = others.ToDictionary(b => b, X);

            transient.Visibility = Visibility.Visible;
            w.UpdateLayout();

            var moved = others
                .Select(b => (Label: Label(b), From: before[b], To: X(b)))
                .Where(t => Math.Abs(t.To - t.From) > 0.5)
                .Select(t => $"{t.Label}: {t.From:F1} -> {t.To:F1} ({t.To - t.From:+0.0;-0.0}px)")
                .ToList();

            Assert.True(moved.Count == 0,
                $"Showing \"{Label(transient)}\" in {w.GetType().Name} displaced {moved.Count} " +
                "other button(s). Whatever the user's cursor was resting on is now something " +
                "else, on a row where the thing that just appeared is a Cancel:\n  " +
                string.Join("\n  ", moved));
        }
        finally
        {
            w.Close();
            cleanup?.Invoke();
        }
    });
}
