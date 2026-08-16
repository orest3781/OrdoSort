using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using OrdoSort.Core;
using OrdoSort.Wpf;
using OrdoSort.Wpf.Theme;

namespace OrdoSort.Wpf.Tests;

/// <summary>The MainWindow header — the menu on the left, the monitored-folder
/// controls on the right — must stay ONE ROW at every width the app itself
/// allows, and it did not.
///
/// MainWindow.xaml's header Grid gave the Menu the <c>*</c> column and the
/// right-hand cluster <c>Auto</c>, so narrowing squeezed the menu and never the
/// toolbar. A WPF <see cref="Menu"/> hosts its items in a <b>WrapPanel</b> by
/// default, so the four top-level items reflowed onto a second row and the
/// header doubled in height.
///
/// This is not a hypothetical narrow window. EnterCompact (MainWindow.xaml.cs)
/// parks the Ready dashboard at <c>Width = 470</c> with <c>MinWidth = 400</c>,
/// and the untouched header needs roughly 470px for its own content — so the
/// wrap happened in the app's own compact mode, which is where it was reported
/// from.
///
/// 400px, the window's own declared floor, is therefore the acceptance
/// criterion these tests assert against, rather than a number picked to suit
/// the fix.</summary>
[Collection(HighlightContrastTests.Name)]
public class HeaderLayoutTests : IDisposable
{
    private readonly HighlightContrastFixture _fx;
    private readonly string _dir;

    public HeaderLayoutTests(HighlightContrastFixture fx)
    {
        _fx = fx;
        _dir = Path.Combine(Path.GetTempPath(), "ordo_headertest_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    /// <summary>A real, compiled MainWindow shown off-screen at a given width.
    /// One watch folder because the right-hand cluster binds its visibility to
    /// TileControlsVisible (<c>IsReady &amp;&amp; WatchFolders.Count > 0</c>) —
    /// without one the controls are collapsed and every assertion below would
    /// pass vacuously.</summary>
    private MainWindow OpenAtWidth(double width)
    {
        ThemeManager.Apply(_fx.App, dark: false);

        var watched = Path.Combine(_dir, "watched");
        Directory.CreateDirectory(watched);
        var cfg = new Config
        {
            Inbox = Path.Combine(_dir, "inbox"),
            Deferred = Path.Combine(_dir, "deferred"),
            HistoryDb = Path.Combine(_dir, "history.sqlite"),
        };
        cfg.WatchFolders.Add(new WatchFolder { Label = "Failed transfers", Path = watched, Filetypes = "pdf" });
        Directory.CreateDirectory(cfg.Inbox);
        Directory.CreateDirectory(cfg.Deferred);
        var cfgPath = Path.Combine(_dir, "config.json");

        var window = new MainWindow(cfg, cfgPath)
        {
            Left = -20000, Top = 0, ShowActivated = false,
            WindowStartupLocation = WindowStartupLocation.Manual,
        };
        window.Show();
        // EnterCompact runs in the ctor and parks the window at 470; the width
        // under test is applied after, the way a user's drag would.
        window.Width = width;
        window.UpdateLayout();
        PumpRender();
        window.UpdateLayout();
        return window;
    }

    private static Menu HeaderMenu(MainWindow window) =>
        FindDescendant<Menu>(window) ?? throw new InvalidOperationException("no Menu in MainWindow");

    /// <summary>Vertical offset of each top-level menu item within the Menu.
    /// One distinct value means one row; a wrap produces two.</summary>
    private static List<double> MenuItemRows(Menu menu) =>
        menu.Items.OfType<MenuItem>()
            .Select(mi => Math.Round(mi.TransformToAncestor(menu).Transform(new Point(0, 0)).Y))
            .Distinct()
            .ToList();

    private static TextBlock? TextNamed(DependencyObject root, string text) =>
        FindAllDescendants<TextBlock>(root).FirstOrDefault(t => t.Text == text);

    /// <summary>THE REPORTED BUG, at the window's own minimum width.</summary>
    [Fact]
    public void TheMenuStaysOnOneRowAtTheWindowsMinimumWidth() => _fx.Invoke(() =>
    {
        var window = OpenAtWidth(400);
        try
        {
            var menu = HeaderMenu(window);
            Assert.Equal(3, menu.Items.OfType<MenuItem>().Count());   // File, Tools, Help

            var rows = MenuItemRows(menu);
            Assert.True(rows.Count == 1,
                $"the header menu wrapped onto {rows.Count} rows at 400px (offsets: {string.Join(", ", rows)})");
        }
        finally { window.Close(); }
    });

    /// <summary>The same at the compact dashboard's own parked width, which is
    /// where this was actually seen.</summary>
    [Fact]
    public void TheMenuStaysOnOneRowAtTheCompactDashboardWidth() => _fx.Invoke(() =>
    {
        var window = OpenAtWidth(470);
        try
        {
            var rows = MenuItemRows(HeaderMenu(window));
            Assert.True(rows.Count == 1,
                $"the header menu wrapped onto {rows.Count} rows at the compact 470px width");
        }
        finally { window.Close(); }
    });

    /// <summary>Priority, not just survival: the toolbar gives up space before
    /// the menu does. The "Folders:" label goes first — the combo it labels
    /// carries the same explanation in its own tooltip.</summary>
    [Fact]
    public void TheFoldersLabelIsDroppedOnANarrowWindow() => _fx.Invoke(() =>
    {
        var window = OpenAtWidth(470);
        try
        {
            var label = TextNamed(window, "Folders:");
            Assert.True(label is null || !label.IsVisible,
                "the Folders: label should be dropped before the menu is squeezed");
        }
        finally { window.Close(); }
    });

    /// <summary>Next to go is the Refresh button's caption — its icon and its
    /// "Rescan the inbox now" tooltip both survive, so nothing is lost.</summary>
    [Fact]
    public void TheRefreshButtonGoesIconOnlyOnANarrowWindow() => _fx.Invoke(() =>
    {
        var window = OpenAtWidth(440);
        try
        {
            var caption = TextNamed(window, "Refresh");
            Assert.True(caption is null || !caption.IsVisible,
                "the Refresh caption should be dropped on a narrow window");
        }
        finally { window.Close(); }
    });

    /// <summary>The other side of that trade — a comfortable window still shows
    /// the full header. Without this, collapsing everything unconditionally
    /// would pass the three tests above.</summary>
    [Fact]
    public void TheFullHeaderShowsOnAComfortableWindow() => _fx.Invoke(() =>
    {
        var window = OpenAtWidth(900);
        try
        {
            var label = TextNamed(window, "Folders:");
            Assert.True(label is { IsVisible: true }, "the Folders: label should show at 900px");

            var caption = TextNamed(window, "Refresh");
            Assert.True(caption is { IsVisible: true }, "the Refresh caption should show at 900px");

            Assert.Single(MenuItemRows(HeaderMenu(window)));
        }
        finally { window.Close(); }
    });

    // ---- the two defences, past the point the ladder alone can cover -------
    // Measured, not assumed: with ONLY the width ladder in place (captions
    // dropped) the four tests above already pass, because a narrower toolbar
    // leaves the menu's column wide enough not to squeeze. So the column
    // priority and the non-wrapping ItemsPanel buy nothing at any width the app
    // currently permits — EnterCompact's MinWidth = 400 is simply too generous
    // to reach them.
    //
    // They are not decoration, though: they are what holds if MinWidth is ever
    // lowered, if a fifth top-level menu appears, or if the toolbar regrows.
    // These two tests reach that regime deliberately by clearing MinWidth, so
    // both changes are covered by something that fails without them rather than
    // by an argument.

    /// <summary>Squeezed past the point the width ladder can help, the menu
    /// still occupies ONE row — it clips instead of reflowing. Fails without
    /// the Menu.ItemsPanel override, which is the only thing that can hold
    /// here.</summary>
    [Fact]
    public void TheMenuNeverWrapsEvenBelowTheWindowsMinimumWidth() => _fx.Invoke(() =>
    {
        var window = OpenAtWidth(470);
        try
        {
            window.MinWidth = 0;   // past EnterCompact's floor, on purpose
            window.Width = 200;
            window.UpdateLayout();
            PumpRender();
            window.UpdateLayout();

            var rows = MenuItemRows(HeaderMenu(window));
            Assert.True(rows.Count == 1,
                $"the header menu wrapped onto {rows.Count} rows at 200px (offsets: {string.Join(", ", rows)})");
        }
        finally { window.Close(); }
    });

    /// <summary>...and it is the TOOLBAR that gives up the space, not the menu:
    /// the menu still gets its full desired width. Fails if the header Grid's
    /// columns go back to "* for the menu, Auto for the toolbar", which hands
    /// the entire squeeze to the one element that cannot absorb it.</summary>
    [Fact]
    public void TheToolbarYieldsTheSpaceRatherThanTheMenu() => _fx.Invoke(() =>
    {
        var window = OpenAtWidth(470);
        try
        {
            window.MinWidth = 0;
            window.Width = 200;
            window.UpdateLayout();
            PumpRender();
            window.UpdateLayout();

            // NOT DesiredSize: WPF clamps that to the constraint the parent
            // measured with, so a squeezed menu reports a smaller desire and
            // the comparison passes vacuously (measured — that version of this
            // test could not fail). The honest question is whether the LAST
            // item still fits inside the menu's own width, i.e. whether the
            // menu is being clipped.
            var menu = HeaderMenu(window);
            var last = menu.Items.OfType<MenuItem>().Last();
            var right = last.TransformToAncestor(menu).Transform(new Point(0, 0)).X + last.ActualWidth;

            Assert.True(right <= menu.ActualWidth + 0.5,
                $"the menu was clipped at {menu.ActualWidth:F0}px with its last item reaching "
                + $"{right:F0}px — the toolbar should give up that space, not the menu");
        }
        finally { window.Close(); }
    });

    // -------------------------------------------------------------- plumbing

    private static void PumpRender() =>
        Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.Render);

    private static T? FindDescendant<T>(DependencyObject root) where T : DependencyObject
    {
        var count = VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T match) return match;
            if (FindDescendant<T>(child) is { } nested) return nested;
        }
        return null;
    }

    private static List<T> FindAllDescendants<T>(DependencyObject root) where T : DependencyObject
    {
        var results = new List<T>();
        var count = VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T match) results.Add(match);
            results.AddRange(FindAllDescendants<T>(child));
        }
        return results;
    }
}
