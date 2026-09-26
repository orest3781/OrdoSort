using System.Windows;

namespace OrdoSort.Wpf.Services;

/// <summary>The geometry behind "fit the Processing window to the first
/// page when a session opens" (spec 2026-09-26). Pure, and separate from the
/// window for the same reason <see cref="PanMath"/> is: window code cannot be
/// unit-tested, and this is the part with the arithmetic in it. The window
/// measures its own chrome (its size minus the viewer pane's) and hands it
/// in, so no copy of the layout lives here.</summary>
public static class FitMath
{
    /// <summary>The Processing window's bounds when a session opens (spec
    /// 2026-09-26): the viewer's DOCUMENT area takes the page's shape at the
    /// full height of <paramref name="workArea"/>; if that is wider than the
    /// work area, the height shrinks instead. Centred on the work area, and
    /// never below the window's minimum size. Edge spends the pane's top
    /// <see cref="PanMath.ToolbarDip"/> on its toolbar and its right
    /// <see cref="PanMath.ScrollbarDip"/> on a scrollbar, so those are added
    /// around the page. Null for input that cannot give a sane size: a window
    /// that stays put beats one sized from garbage.</summary>
    public static Rect? SessionBounds(Rect workArea, double chromeWidth, double chromeHeight,
        double aspect, double minWidth, double minHeight)
    {
        if (aspect <= 0 || double.IsNaN(aspect) || double.IsInfinity(aspect)) return null;
        if (workArea.Width <= 0 || workArea.Height <= 0) return null;

        var height = workArea.Height;
        var documentHeight = height - chromeHeight - PanMath.ToolbarDip;
        if (documentHeight <= 0) return null;
        var width = chromeWidth + documentHeight * aspect + PanMath.ScrollbarDip;
        if (width > workArea.Width)
        {
            width = workArea.Width;
            var documentWidth = width - chromeWidth - PanMath.ScrollbarDip;
            if (documentWidth <= 0) return null;
            height = chromeHeight + PanMath.ToolbarDip + documentWidth / aspect;
        }
        width = Math.Max(width, Math.Min(minWidth, workArea.Width));
        height = Math.Max(height, Math.Min(minHeight, workArea.Height));
        return new Rect(workArea.Left + (workArea.Width - width) / 2,
            workArea.Top + (workArea.Height - height) / 2, width, height);
    }
}
