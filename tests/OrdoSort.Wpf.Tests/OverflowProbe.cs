using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;

namespace OrdoSort.Wpf.Tests;

/// <summary>The geometry half of LabelMakerOverflowTests, extracted so every
/// window can be probed the same way: WPF TextBlocks PAINT their full text
/// past their layout slot unless they wrap or trim, and a Grid doesn't clip —
/// so "text off the screen" leaves no trace in any property assertion. The
/// only honest check is geometric: render the real window off-screen and
/// verify every visible text-bearing element's bounds land inside the
/// window's content bounds.</summary>
internal static class OverflowProbe
{
    private static void FindAll<T>(DependencyObject node, List<T> into) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(node); i++)
        {
            var child = VisualTreeHelper.GetChild(node, i);
            if (child is T hit) into.Add(hit);
            FindAll(child, into);
        }
    }

    /// <summary>Every visible TextBlock/RadioButton/CheckBox/Button whose
    /// bounds escape <paramref name="contentRoot"/> horizontally, described
    /// with its text and coordinates; empty when the layout is clean.
    /// Elements inside an open Popup/ToolTip layer are naturally excluded —
    /// the walk covers only the window's own visual tree.</summary>
    /// <param name="examined">See <see cref="Escapees"/> — assert a floor on
    /// it, always.</param>
    public static List<string> HorizontalEscapees(FrameworkElement contentRoot, out int examined) =>
        Escapees(contentRoot, checkVertical: false, out examined);

    /// <summary>Both axes. Vertical escape is the same defect family seen
    /// top-to-bottom: a window that opens shorter than its content cuts the
    /// bottom controls off with no scrollbar and no visual hint. Elements
    /// inside a vertically-scrollable region (a list, a tab's ScrollViewer)
    /// are reachable and exempt, mirroring the horizontal rule.</summary>
    /// <param name="examined">How many candidates survived the IsVisible /
    /// ActualWidth filter below and were actually judged. It is an out
    /// parameter rather than a convenience because an empty candidate list
    /// makes "no offenders" mean nothing, and this suite shipped exactly that
    /// for months: UIElement.IsVisible is false for any tree whose root has no
    /// PresentationSource, so four call sites that Measure/Arrange a view by
    /// hand and never Show() it skipped EVERY candidate. A MinWidth="2000"
    /// TextBlock injected into DoneView — 2000px inside a 370px panel — still
    /// gave "Failed: 0, Passed: 7" (QC-09). Every call site must assert a
    /// floor on this.</param>
    public static List<string> Escapees(FrameworkElement contentRoot, bool checkVertical, out int examined)
    {
        var probes = new List<FrameworkElement>();
        var texts = new List<TextBlock>(); FindAll(contentRoot, texts); probes.AddRange(texts);
        var toggles = new List<ToggleButton>(); FindAll(contentRoot, toggles); probes.AddRange(toggles);
        var buttons = new List<ButtonBase>(); FindAll(contentRoot, buttons);
        probes.AddRange(buttons.Where(b => b is not ToggleButton));

        var offenders = new List<string>();
        examined = 0;
        foreach (var e in probes)
        {
            if (!e.IsVisible || e.ActualWidth == 0) continue;
            examined++;
            var bounds = e.TransformToAncestor(contentRoot)
                .TransformBounds(new Rect(0, 0, e.ActualWidth, e.ActualHeight));
            // half-pixel tolerance for layout rounding; an element inside a
            // scrollable region (a DataGrid or list's internal ScrollViewer,
            // a tab's ScrollViewer) is reachable by scrolling on that axis,
            // not lost off screen — the defect this probe hunts
            if ((bounds.Right > contentRoot.ActualWidth + 0.5 || bounds.Left < -0.5)
                && !HasScrollableAncestor(e, contentRoot, horizontal: true))
                offenders.Add(
                    $"{e.GetType().Name} \"{Describe(e)}\" spans {bounds.Left:F0}..{bounds.Right:F0} " +
                    $"but the window content ends at {contentRoot.ActualWidth:F0}");
            else if (checkVertical
                && (bounds.Bottom > contentRoot.ActualHeight + 0.5 || bounds.Top < -0.5)
                && !HasScrollableAncestor(e, contentRoot, horizontal: false))
                offenders.Add(
                    $"{e.GetType().Name} \"{Describe(e)}\" spans rows {bounds.Top:F0}..{bounds.Bottom:F0} " +
                    $"but the window content ends at row {contentRoot.ActualHeight:F0}");
        }
        return offenders;
    }

    private static bool HasScrollableAncestor(DependencyObject e, FrameworkElement contentRoot, bool horizontal)
    {
        for (var node = VisualTreeHelper.GetParent(e);
             node is not null && !ReferenceEquals(node, contentRoot);
             node = VisualTreeHelper.GetParent(node))
        {
            if (node is ScrollViewer sv && (horizontal
                    ? sv.HorizontalScrollBarVisibility is not ScrollBarVisibility.Disabled
                    : sv.VerticalScrollBarVisibility is not ScrollBarVisibility.Disabled))
                return true;
        }
        return false;
    }

    private static string Describe(FrameworkElement e) => e switch
    {
        TextBlock t => t.Text,
        ContentControl c => c.Content as string ?? c.Content?.GetType().Name ?? "",
        _ => "",
    };

    public static void PumpRender()
    {
        var frame = new System.Windows.Threading.DispatcherFrame();
        System.Windows.Threading.Dispatcher.CurrentDispatcher.BeginInvoke(
            System.Windows.Threading.DispatcherPriority.Loaded,
            new Action(() => frame.Continue = false));
        System.Windows.Threading.Dispatcher.PushFrame(frame);
    }
}
