using System.Windows;

namespace OrdoSort.Wpf.Services;

/// <summary>The geometry behind "size the viewer pane to the document when a
/// session starts". Pure, and separate from MainWindow for the same reason
/// <see cref="PanMath"/> is: window code cannot be unit-tested, and this is
/// the part with the arithmetic in it.
///
/// It works in DELTAS rather than absolute widths on purpose. The window's
/// width is its viewer, plus a splitter, plus the panel, plus a border, plus
/// whatever the shell adds around them; modelling that sum would be a second
/// copy of the layout that goes stale the first time a margin changes.
/// Measuring the viewer as it actually is and moving the window by the
/// difference needs no model at all.</summary>
public static class FitMath
{
    /// <summary>The window width that makes the viewer's DOCUMENT area — not
    /// its whole rectangle — match a page of the given aspect. Edge's PDF
    /// viewer spends the top <see cref="PanMath.ToolbarDip"/> of the pane on
    /// its toolbar and the right <see cref="PanMath.ScrollbarDip"/> on a
    /// scrollbar; fitting the outer rectangle instead would leave the page
    /// itself narrower than the pane by exactly that much.
    ///
    /// Returns <paramref name="windowWidth"/> unchanged for any input that
    /// cannot produce a sane answer (an unmeasured pane, a pane shorter than
    /// the toolbar, a nonsense aspect) — a window that stays put is always a
    /// better outcome here than one that jumps to a computed-from-garbage
    /// size.</summary>
    public static double WindowWidthFor(
        double windowWidth, double viewerWidth, double viewerHeight,
        double aspect, double minWidth, double maxWidth)
    {
        if (aspect <= 0 || double.IsNaN(aspect) || double.IsInfinity(aspect)) return windowWidth;
        if (viewerWidth <= 0 || viewerHeight <= 0) return windowWidth;

        var documentHeight = viewerHeight - PanMath.ToolbarDip;
        if (documentHeight <= 0) return windowWidth;

        var wantedViewer = documentHeight * aspect + PanMath.ScrollbarDip;
        var wanted = windowWidth + (wantedViewer - viewerWidth);
        // Math.Clamp throws when its bounds cross, which they do on a screen
        // narrower than MinWidth — a real case on a 1024-wide laptop.
        return Math.Clamp(wanted, minWidth, Math.Max(minWidth, maxWidth));
    }

    /// <summary>Where a window of the given width has to sit to stay inside
    /// the work area. Only pulls left when the window would hang off the
    /// right edge, and never past the left edge — a window too wide for the
    /// screen is pinned to the left rather than centred on nothing.</summary>
    public static double LeftFor(double left, double width, Rect workArea)
    {
        if (left + width <= workArea.Right) return left;
        return Math.Max(workArea.Left, workArea.Right - width);
    }
}
