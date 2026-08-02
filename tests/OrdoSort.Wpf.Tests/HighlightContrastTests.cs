using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using OrdoSort.Wpf.Theme;

namespace OrdoSort.Wpf.Tests;

/// <summary>Hosts the one WPF <see cref="Application"/> a process may create,
/// with the real <c>Theme/Styles.xaml</c> merged in exactly as <c>App.xaml</c>
/// does — so every test in this class resolves the SAME DynamicResource
/// brushes (Theme.Text, Theme.Accent, …) the shipped app does. Deliberately
/// does NOT instantiate <see cref="App"/> itself: that subclass's
/// <c>OnStartup</c> override fires the moment anything pumps this thread's
/// dispatcher (confirmed in the 2026-08-02 Task 1 investigation) and tries to
/// load a config + open a real MainWindow/SQLite history — none of which this
/// suite needs just to read a resolved brush, and all of which would turn one
/// contrast assertion into a fragile, slow integration test forever. A bare
/// <see cref="Application"/> only raises the (unhandled, so harmless) base
/// <c>Startup</c> event.
///
/// Everything runs on one dedicated, persistent STA thread: merely
/// constructing a <see cref="System.Windows.Controls.Control"/> touches
/// <c>InputManager</c>/<c>KeyboardNavigation</c>, which throws
/// "The calling thread must be STA" on an ordinary xunit worker thread even
/// with no Window ever shown — confirmed empirically here (not assumed).
/// One thread for the whole fixture (not one per test) because
/// <see cref="Application"/> may only be constructed once per process.</summary>
public sealed class HighlightContrastFixture : IDisposable
{
    private readonly Thread _thread;
    public Application App { get; }
    public Dispatcher Dispatcher { get; }

    public HighlightContrastFixture()
    {
        Application? app = null;
        Dispatcher? dispatcher = null;
        using var ready = new ManualResetEventSlim();
        _thread = new Thread(() =>
        {
            app = Application.Current ?? new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            if (!app.Resources.MergedDictionaries.Any(d =>
                    d.Source is { } src && src.OriginalString.Contains("Theme/Styles.xaml")))
            {
                app.Resources.MergedDictionaries.Add(new ResourceDictionary
                {
                    // AssemblyName is "OrdoSort" (OrdoSort.Wpf.csproj), not "OrdoSort.Wpf".
                    Source = new Uri("pack://application:,,,/OrdoSort;component/Theme/Styles.xaml"),
                });
            }
            dispatcher = Dispatcher.CurrentDispatcher;
            // ready must be set from ON this thread, after Dispatcher.CurrentDispatcher
            // exists, but BEFORE Dispatcher.Run() blocks it.
            ready.Set();
            Dispatcher.Run();
        })
        {
            IsBackground = true,
        };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();
        ready.Wait();
        App = app!;
        Dispatcher = dispatcher!;
    }

    /// <summary>Marshal a test body onto the fixture's STA thread and rethrow
    /// there with the original type/stack intact, so a failing xunit
    /// Assert.True surfaces as this test's own failure, not a wrapped one.</summary>
    public void Invoke(Action body)
    {
        ExceptionDispatchInfo? captured = null;
        Dispatcher.Invoke(() =>
        {
            try { body(); }
            catch (Exception ex) { captured = ExceptionDispatchInfo.Capture(ex); }
        });
        captured?.Throw();
    }

    public void Dispose()
    {
        Dispatcher.InvokeShutdown();
        _thread.Join(TimeSpan.FromSeconds(2));
    }
}

/// <summary>The highlighted-row text-contrast bug, reproduced directly against
/// the real <c>Theme/Styles.xaml</c> (no scratch harness, no screenshots): a
/// container's trigger correctly flips the CONTAINER's own Foreground DP on
/// IsHighlighted/IsSelected (e.g. ComboBoxItem -&gt; Theme.AccentText), but
/// WPF's plain-string Content auto-wrap builds its TextBlock via an internal,
/// special-cased template that consults the APPLICATION-level implicit
/// TextBlock style (Styles.xaml, Foreground=Theme.Text) instead of
/// inheriting the container's flipped value: a Style Setter always outranks
/// property-value inheritance. Measured in Task 1 (2026-08-02): ComboBoxItem
/// 1.35 light / 1.27 dark — both well under the WCAG AA floor of 4.5. See
/// .superpowers/sdd/2026-08-02-highlight-text-contrast/task-1-report.md.
///
/// Two independently-broken shapes exist (Task 1, Step 3): a plain-string
/// ComboBoxItem (fixable centrally, in Styles.xaml's ComboBoxItem style) and
/// an ItemTemplate-based one (NOT reachable from Styles.xaml at all —
/// ItemsControl assigns ItemTemplate to the generated container's
/// ContentTemplate as a LOCAL value, which outranks any Style Setter, so the
/// call site's own template needs the same fix). Both are covered below —
/// and for the ItemTemplate shape, by resolving the REAL production
/// templates (<c>KvpValueTemplate</c>/<c>FontChoiceTemplate</c>, both moved
/// into Theme/Styles.xaml specifically so this suite can load them by key)
/// rather than a hand-authored stand-in, so a future accidental revert of
/// either template's Foreground binding fails this suite, not just a
/// hand-copied duplicate of it.</summary>
public class HighlightContrastTests : IClassFixture<HighlightContrastFixture>
{
    private readonly HighlightContrastFixture _fx;
    public HighlightContrastTests(HighlightContrastFixture fx) => _fx = fx;

    public static IEnumerable<object[]> ComboBoxShapes()
    {
        foreach (var dark in new[] { false, true })
        {
            yield return new object[] { "plain-string", dark };
            yield return new object[] { "KvpValueTemplate", dark };
            yield return new object[] { "FontChoiceTemplate", dark };
        }
    }

    /// <summary>Covers all shapes Task 1 found broken. "plain-string" mirrors
    /// MainWindow.xaml's/BulkRenameWindow.xaml's `&lt;ComboBoxItem
    /// Content="…"/&gt;` (no ItemTemplate — the fix lives in Styles.xaml's
    /// ComboBoxItem style, so this case exercises the REAL Styles.xaml,
    /// merged above, unmodified by this test file). "KvpValueTemplate" and
    /// "FontChoiceTemplate" are resolved BY KEY straight out of that same
    /// loaded <c>Theme/Styles.xaml</c> — the actual `DataTemplate` resources
    /// SettingsWindow.xaml's naming-mode/process-order pickers and font
    /// picker use via `ItemTemplate="{StaticResource …}"` — and applied as a
    /// standalone ComboBoxItem's local `ContentTemplate` (the exact same WPF
    /// property-value precedence `ItemsControl.PrepareContainerForItemOverride`
    /// creates internally) rather than via a real ComboBox+Popup, since the
    /// bug is a pure DependencyProperty-precedence conflict that doesn't care
    /// who set the local value. Because these are the PRODUCTION template
    /// objects (not a copy), stripping either one's Foreground binding in
    /// SettingsWindow.xaml/Styles.xaml — Task 1's most important finding,
    /// the call-site shape a Styles.xaml-only fix cannot reach — fails this
    /// test directly; proven by temporarily doing exactly that (see the
    /// task-2 follow-up report for the pasted failing output).</summary>
    [Theory, MemberData(nameof(ComboBoxShapes))]
    public void HighlightedComboBoxItemTextMeetsWcagAa(string shape, bool dark) => _fx.Invoke(() =>
    {
        ThemeManager.Apply(_fx.App, dark);

        var container = new ComboBoxItem();
        if (shape == "plain-string")
        {
            container.Content = "Failed queues";
        }
        else
        {
            // shape is a resource key ("KvpValueTemplate"/"FontChoiceTemplate")
            // in the real, loaded Theme/Styles.xaml -- not a copy.
            container.Content = new KeyValuePair<string, string>("k", "Failed queues");
            container.ContentTemplate = (DataTemplate)_fx.App.Resources[shape];
        }

        Realize(container);
        ForceHighlighted(container);
        Realize(container);

        var text = FindTextElement(container)
            ?? throw new InvalidOperationException("no TextBlock/AccessText descendant under ComboBoxItem");
        var bd = FindDescendant<Border>(container)
            ?? throw new InvalidOperationException("no Border ('Bd') descendant under ComboBoxItem");

        var fg = ToRgb(ForegroundOf(text));
        var bg = ToRgb(bd.Background);
        var ratio = ThemePalette.ContrastRatio(fg, bg);
        Assert.True(ratio >= 4.5,
            $"ComboBoxItem {shape} ({(dark ? "dark" : "light")}): {fg} on {bg} = {ratio:F2}");
    });

    /// <summary>UNRESOLVED-CONFLICT investigation (Task 2 brief), later
    /// CORRECTED after independent re-verification: re-measure the MenuItem
    /// submenu Header in a REAL menu, declared the way the app declares it
    /// (MainWindow.xaml: an access-keyed `Header="_Tools"` TopLevelHeader
    /// containing a `Header="_…"` SubmenuItem), rather than trusting Task 1's
    /// synthetic harness or the 2026-08-01 QC round.
    ///
    /// Establishing which element actually paints the glyphs (per the brief)
    /// took an extra step, and an earlier version of this file got it wrong
    /// in a way worth recording: an access-keyed Header resolves to
    /// <see cref="System.Windows.Controls.AccessText"/>, which does NOT
    /// derive from TextBlock. A naive "first TextBlock descendant" walk
    /// silently drills PAST the real AccessText into a private,
    /// always-empty child TextBlock it builds internally, reading that
    /// decoy's unrelated (Theme.Text-pinned) Foreground instead of the real
    /// one — which is what the ORIGINAL version of this test did, producing
    /// a false "1.35/1.27, FAILS" reading that looked identical to the real
    /// ComboBoxItem bug and led to an unnecessary `HeaderTemplate` Setter
    /// being added to Styles.xaml's MenuItem style. Independent re-verification
    /// against a build with zero occurrences of "HeaderTemplate" in the
    /// compiled OrdoSort.dll (i.e. provably pre-fix) — using the CORRECTED
    /// <see cref="FindTextElement"/> below, which stops at EITHER a TextBlock
    /// or an AccessText instead of always drilling to a TextBlock leaf —
    /// found the auto-generated AccessText was ALREADY correct:
    /// 12.89:1 light / 11.48:1 dark, PASSING. Root cause of why it was never
    /// broken: Styles.xaml has no AccessText-specific implicit style, and
    /// implicit-style lookup only walks an element's own .NET base-type
    /// chain — AccessText's doesn't include TextBlock — so the "Setter
    /// outranks inheritance" trap that breaks ComboBoxItem's plain-string
    /// Content has no mechanism to reach AccessText at all; its Foreground
    /// simply inherits from the MenuItem, which the MenuSubmenuItem
    /// template's IsHighlighted trigger already flips correctly. The
    /// `HeaderTemplate` Setter was reverted from Styles.xaml as a no-op; this
    /// test case is kept as a regression guard and passes with or without
    /// it (verified both ways).</summary>
    [Theory, MemberData(nameof(Palettes))]
    public void HighlightedMenuSubmenuHeaderTextMeetsWcagAa(bool dark) => _fx.Invoke(() =>
    {
        ThemeManager.Apply(_fx.App, dark);

        var menu = new Menu();
        var topLevel = new MenuItem { Header = "_Tools" };
        var child = new MenuItem { Header = "_Unlock PDFs…" };
        topLevel.Items.Add(child);
        menu.Items.Add(topLevel);

        // A real off-screen Window + a genuinely OPENED submenu Popup (not a
        // "loose" element, unlike the ComboBoxItem cases above): MenuItem's
        // Header ContentPresenter only finishes generating/binding its
        // content once the submenu is actually open in a live PresentationSource.
        var window = new Window
        {
            Content = menu, Width = 300, Height = 200,
            Left = -20000, Top = 0, ShowActivated = false,
            WindowStartupLocation = WindowStartupLocation.Manual,
        };
        try
        {
            window.Show();
            window.UpdateLayout();
            topLevel.IsSubmenuOpen = true;
            PumpRender();
            window.UpdateLayout();
            ForceHighlighted(child);
            PumpRender();
            window.UpdateLayout();

            // Sanity: same Role MainWindow.xaml's own nested MenuItems resolve to
            // (a TopLevelHeader's non-header child), so this is really exercising
            // the MenuSubmenuItem ControlTemplate — not, say, TopLevelItem.
            Assert.Same(_fx.App.Resources["MenuSubmenuItem"], child.Template);

            var text = FindTextElement(child)
                ?? throw new InvalidOperationException("no TextBlock/AccessText descendant under submenu MenuItem");
            var bd = FindDescendant<Border>(child)
                ?? throw new InvalidOperationException("no Border ('Bd') descendant under submenu MenuItem");

            var fg = ToRgb(ForegroundOf(text));
            var bg = ToRgb(bd.Background);
            var ratio = ThemePalette.ContrastRatio(fg, bg);
            Assert.True(ratio >= 4.5,
                $"MenuItem submenu Header ({(dark ? "dark" : "light")}): {fg} on {bg} = {ratio:F2}");
        }
        finally
        {
            window.Close();
        }
    });

    private static void PumpRender() =>
        Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.Render);

    public static IEnumerable<object[]> Palettes()
    {
        yield return new object[] { false };
        yield return new object[] { true };
    }

    // -------------------------------------------------------------- plumbing

    /// <summary>Force full template/content generation on a "loose" element —
    /// no Window, no Show(), no STA thread needed: ApplyTemplate + Measure +
    /// Arrange is exactly what materializes a ControlTemplate's named parts
    /// and a ContentPresenter's chosen DataTemplate/auto-wrap, and none of
    /// that requires a live PresentationSource. DynamicResource/implicit-style
    /// lookups still resolve because they fall back to Application.Current's
    /// resources regardless of how disconnected the element's own tree is.</summary>
    private static void Realize(FrameworkElement el)
    {
        el.ApplyTemplate();
        el.Measure(new Size(400, 200));
        el.Arrange(new Rect(0, 0, 400, 200));
        el.UpdateLayout();
    }

    /// <summary>IsHighlighted is a read-only DP on both ComboBoxItem and
    /// MenuItem, normally flipped only by real mouse/keyboard interaction.
    /// Task 1 proved forcing it via reflection on the private
    /// `IsHighlightedPropertyKey` + the public
    /// `DependencyObject.SetValue(DependencyPropertyKey,object)` overload
    /// reproduces the identical state WPF's own hover/keyboard-navigation
    /// logic sets naturally (cross-checked there against a live dropdown) —
    /// deterministic and repeatable where real input isn't.</summary>
    private static void ForceHighlighted(DependencyObject item)
    {
        var field = item.GetType().GetField("IsHighlightedPropertyKey",
                BindingFlags.Static | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException(
                $"{item.GetType()} has no private static IsHighlightedPropertyKey field");
        var key = (DependencyPropertyKey)field.GetValue(null)!;
        item.SetValue(key, true);
    }

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

    /// <summary>Like <see cref="FindDescendant{T}"/>, but stops at EITHER a
    /// TextBlock or an AccessText — whichever is found first — instead of
    /// only a TextBlock. AccessText does not derive from TextBlock and
    /// internally builds its own private (always-empty) child TextBlock, so
    /// a TextBlock-only search silently drills past the real element that
    /// paints an access-keyed Header into that decoy (see the class-level
    /// comment on the MenuItem test for how this was found).</summary>
    private static DependencyObject? FindTextElement(DependencyObject root)
    {
        var count = VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is AccessText or TextBlock) return child;
            if (FindTextElement(child) is { } nested) return nested;
        }
        return null;
    }

    private static Brush? ForegroundOf(DependencyObject el) => el switch
    {
        AccessText at => at.Foreground,
        TextBlock tb => tb.Foreground,
        _ => throw new InvalidOperationException($"not a text element: {el.GetType()}"),
    };

    private static Rgb ToRgb(Brush? brush) => brush switch
    {
        SolidColorBrush s => new Rgb(s.Color.R, s.Color.G, s.Color.B),
        _ => throw new InvalidOperationException(
            $"expected a resolved SolidColorBrush, got {brush?.GetType().Name ?? "null"}"),
    };
}
