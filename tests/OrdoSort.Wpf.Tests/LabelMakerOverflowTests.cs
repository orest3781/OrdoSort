using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using OrdoSort.Core;
using OrdoSort.Wpf.Services;
using OrdoSort.Wpf.Theme;
using OrdoSort.Wpf.ViewModels;
using OrdoSort.Wpf.Windows;

namespace OrdoSort.Wpf.Tests;

/// <summary>A no-op IDialogService — same file-scoped duplicate as
/// FocusRingCoverageTests carries, for the same reason (the fixture's own
/// copy is `file`-scoped and invisible from here).</summary>
file sealed class NoDialogs : IDialogService
{
    public void Warn(string message, string title) { }
    public void Info(string message, string title) { }
    public bool Confirm(string message, string title) => true;
    public string? AskSaveFile(string filter, string suggestedName) => null;
    public string? AskOpenFile(string filter) => null;
    public string? AskFilePath(string filter, string suggestedName) => null;
    public string? BrowseFolder(string? startAt) => null;
}

/// <summary>2026-08-16 wrap audit, user report: text in the Box labels window
/// (LabelMakerWindow) ran off screen. The window mixes fixed-width columns
/// (170px client list, 140/60px field cells, Width=680/MinWidth=620) with a
/// user-configurable app font (Config.UiFontSize, 6–72, default 14), so
/// content that fits at 14px marches past the window edge as the font grows —
/// and a WPF Grid does not clip, so there is no visual hint beyond the text
/// vanishing off the right edge.
///
/// This suite renders the REAL window off-screen (per FocusRingCoverageTests'
/// philosophy: prove pixels/geometry, not properties) and asserts every
/// visible TextBlock, RadioButton and Button lands inside the window's
/// content bounds. Guaranteed range, deliberately bounded: the default font
/// and 18px, the Settings Text tab's largest preset, at both the minimum
/// (620) and default (680) widths. 72px cannot be honoured by ANY fixed-width dialog and is out of
/// scope; past 18 the prose elements degrade by trimming/wrapping instead of
/// overflowing, which is what the fixes this suite pins actually changed.</summary>
[Collection(HighlightContrastTests.Name)]
public class LabelMakerOverflowTests : UiTest
{
    private readonly HighlightContrastFixture _fx;
    public LabelMakerOverflowTests(HighlightContrastFixture fx) : base(fx) => _fx = fx;

    /// <remarks>Both axes, at the window's declared size for each width: its
    /// MinHeight (512) with its MinWidth, its Height (572) with its Width.
    /// This used to check horizontally only, at whatever height the window
    /// opened, so a form row pushed off the bottom at the minimum size passed
    /// (Q2-15). 18px at the minimum width is the corner a large font on a
    /// window dragged small produces, which no case rendered (Q2-18). At the
    /// old 600 minimum "Reset to 1" ran about 14px past the right edge, so the
    /// minimum is 620. <paramref name="knownToEscape"/> marks a case expected
    /// to escape; none is now.</remarks>
    [Theory]
    [InlineData(14.0, 620.0, 512.0, false)]
    [InlineData(14.0, 680.0, 572.0, false)]
    [InlineData(18.0, 680.0, 572.0, false)]
    [InlineData(18.0, 620.0, 512.0, false)]
    public void EveryTextElementStaysInsideTheWindow(double fontSize, double width, double height,
        bool knownToEscape) => _fx.Invoke(() =>
    {
        ThemeManager.Apply(_fx.App, dark: false);
        var defaultFont = _fx.App.Resources["AppFontSize"];
        _fx.App.Resources["AppFontSize"] = fontSize;

        var boxLabelsPath = Path.Combine(Path.GetTempPath(), "ordo_test_boxlabels_" + Guid.NewGuid() + ".json");
        var vm = new LabelMakerViewModel(null, boxLabelsPath, new NoDialogs(), "Box labels", scheduler: new InlineWorkScheduler());
        vm.Clients.Add(new LabelClientVm { Id = "TESTCLNT", DestroyDaysText = "45", NextNumberText = "00000001" });
        vm.Selected = vm.Clients[0];
        var window = new LabelMakerWindow(vm, "Box labels", "Print preview")
        {
            Left = -20000, Top = 0, ShowActivated = false,
            WindowStartupLocation = WindowStartupLocation.Manual,
            Width = width,
            Height = height,
        };
        try
        {
            window.Show();
            window.UpdateLayout();
            PumpRender();

            var offenders = OverflowProbe.Escapees((FrameworkElement)window.Content, checkVertical: true, out var examined);
            // 36 judged at 620x512 and 38 at 680x572. The floor is three
            // quarters of the smaller, rounded up, WindowOverflowTests' rule
            // (Probe.MinExamined): the old floor of 10 still passed with
            // nearly three quarters of the window unmeasured (Q2-17)
            Assert.True(examined >= 27,
                $"the probe examined only {examined} elements — it is not measuring anything");
            if (knownToEscape)
            {
                Assert.True(offenders.Count > 0,
                    $"font {fontSize}, width {width}: the window fits now. Set knownToEscape to " +
                    "false for this case and drop the note about it in the remarks.");
                return;
            }
            Assert.True(offenders.Count == 0,
                $"font {fontSize}, width {width}: elements escape the window:\n  " +
                string.Join("\n  ", offenders));
        }
        finally
        {
            window.Close();
            _fx.App.Resources["AppFontSize"] = defaultFont;
            try { File.Delete(boxLabelsPath); } catch { /* best effort */ }
        }
    });

    /// <summary>The standalone adds a store bar across the top, and a fixed
    /// window height does not grow to meet it: the first build of it pushed the
    /// "Labels to print" row and the whole "Date bars" choice off the bottom
    /// edge, where nothing clips and nothing warns — the controls were simply
    /// not there.
    ///
    /// The sibling test above checks the horizontal axis, which is where this
    /// window's font-size defects live. This one checks the vertical axis,
    /// which is where ADDING A ROW puts them.</summary>
    /// <remarks><paramref name="expectsScrolling"/> is what keeps this honest
    /// in both directions: at the default font the form must fit with nothing
    /// hidden, and at 18px it must genuinely be scrolling — an assertion that
    /// only checked "nothing overlaps" would also pass if the form had
    /// silently collapsed to nothing.</remarks>
    [Theory]
    [InlineData(14.0, false)]
    // the form fits at 18px since the date-bars row moved to Settings
    // (2026-09-28); 24px still outgrows it, so scrolling stays exercised
    [InlineData(18.0, false)]
    [InlineData(24.0, true)]
    public void TheFormIsNeverPaintedOverByThePreview(double fontSize, bool expectsScrolling) => _fx.Invoke(() =>
    {
        ThemeManager.Apply(_fx.App, dark: false);
        var defaultFont = _fx.App.Resources["AppFontSize"];
        _fx.App.Resources["AppFontSize"] = fontSize;

        var boxLabelsPath = Path.Combine(Path.GetTempPath(), "ordo_test_boxlabels_" + Guid.NewGuid() + ".json");
        var vm = new LabelMakerViewModel(null, boxLabelsPath, new NoDialogs(), "Box Labels", scheduler: new InlineWorkScheduler());
        vm.Clients.Add(new LabelClientVm { Id = "TESTCLNT", DestroyDaysText = "45", NextNumberText = "00000001" });
        vm.Selected = vm.Clients[0];
        var window = new LabelMakerWindow(vm, "Box Labels", "Box Labels — Print preview",
            standalone: true,
            // with the menu and a long share path: the tallest the bar gets
            standaloneMenu: new StandaloneMenu(@"\\server\records\box-labels.json", () => { }, () => { }))
        {
            Left = -20000, Top = 0, ShowActivated = false,
            WindowStartupLocation = WindowStartupLocation.Manual,
        };
        try
        {
            window.Show();
            window.UpdateLayout();
            PumpRender();

            // The form outgrows its * Grid row as the font grows. A Grid
            // neither clips nor scrolls, so without a viewport the form's last
            // rows are arranged past the row and the preview section below is
            // painted over the top of them — the last row simply is not on
            // screen. The fix is the same one SettingsWindow and
            // UnlockWindow already use for forms that can outgrow their space.
            var content = (FrameworkElement)window.Content;
            var lastRow = FindLastFormRow(content);
            var summary = FindPrintsSummary(content);
            Assert.NotNull(lastRow);
            Assert.NotNull(summary);

            var scroller = FindAncestorScrollViewer(lastRow!);
            Assert.True(scroller is not null,
                "the form has no scrolling viewport, so anything it outgrows is "
                + "painted over the preview instead of being reachable");

            Rect BoundsOf(FrameworkElement e) =>
                e.TransformToAncestor(content).TransformBounds(new Rect(e.RenderSize));

            var form = BoundsOf(scroller!);
            var below = BoundsOf(summary!);
            Assert.True(form.Height > 0 && below.Height > 0,
                $"font {fontSize}: nothing was laid out (form {form}, summary {below})");

            Assert.True(form.Bottom <= below.Top + 0.5,
                $"font {fontSize}: the form runs to {form.Bottom:F0}px but the summary line "
                + $"starts at {below.Top:F0}px, so the two are drawn on top of each other.");

            if (!expectsScrolling)
            {
                // Nothing hidden AND no scrollbar at the default font. The
                // window carries 12px for exactly this: the form was 11px over
                // its row, which with a viewport means a scrollbar appears on a
                // window that never had one.
                Assert.True(scroller!.ScrollableHeight <= 0.5,
                    $"font {fontSize}: the form is {scroller.ScrollableHeight:F0}px over its "
                    + "viewport at the DEFAULT font, so a scrollbar shows on a window that "
                    + "never had one.");
            }
            else
            {
                // Non-vacuous the other way: at 18px the form genuinely must
                // exceed the viewport, or this case is proving nothing about
                // the scroller and the whole test would pass on a window that
                // had quietly collapsed.
                Assert.True(scroller!.ScrollableHeight > 0.5,
                    $"font {fontSize}: the form fits after all "
                    + $"(extent {scroller.ExtentHeight:F0}, viewport {scroller.ViewportHeight:F0}) "
                    + "— this case no longer exercises the scrolling it exists to check.");
            }
        }
        finally
        {
            window.Close();
            _fx.App.Resources["AppFontSize"] = defaultFont;
            try { File.Delete(boxLabelsPath); } catch { /* best effort */ }
        }
    });


    /// <summary>The "30" count preset in the "Labels to print" row — the last
    /// row of the form since the date-bars choice moved to Settings
    /// (2026-09-28), and so the first thing to disappear when the window runs
    /// short.</summary>
    private static FrameworkElement? FindLastFormRow(DependencyObject root)
    {
        if (root is Button { Tag: "30" } b) return b;
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
            if (FindLastFormRow(VisualTreeHelper.GetChild(root, i)) is { } found)
                return found;
        return null;
    }

    /// <summary>The "Prints ABCD00000001 — ..." line that opens the preview
    /// section, i.e. the first thing drawn BELOW the form.</summary>
    private static FrameworkElement? FindPrintsSummary(DependencyObject root)
    {
        if (root is TextBlock { Text: var t } tb && t.StartsWith("Prints ", StringComparison.Ordinal))
            return tb;
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
            if (FindPrintsSummary(VisualTreeHelper.GetChild(root, i)) is { } found)
                return found;
        return null;
    }

    private static ScrollViewer? FindAncestorScrollViewer(DependencyObject from)
    {
        for (var p = VisualTreeHelper.GetParent(from); p is not null; p = VisualTreeHelper.GetParent(p))
            if (p is ScrollViewer sv) return sv;
        return null;
    }
}
