using System.Globalization;
using System.Windows.Data;

namespace OrdoSort.Wpf.Views;

/// <summary>Ready dashboard tile grid, width-adaptive (Phase 3, Task 3.4,
/// approved mockup): the per-group tile <c>UniformGrid</c> in ReadyView.xaml
/// used to hard-code <c>Columns="2"</c> regardless of window width. This
/// keeps UniformGrid's equal-width cells (a plain WrapPanel would make tile
/// widths ragged, which the mockup doesn't show) while letting a 3rd column
/// appear once the window is widened.
///
/// Bound to the tiles <c>ItemsControl</c>'s own ActualWidth (not the
/// UniformGrid's — RelativeSource walks up from the UniformGrid, which is
/// that ItemsControl's ItemsPanel, so the two report the same number; going
/// through the ItemsControl avoids a same-element self-reference read). No
/// layout cycle: UniformGrid/ItemsControl default to
/// HorizontalAlignment="Stretch", so their ActualWidth comes from the space
/// their ANCESTOR hands down, never from their own column count.
///
/// Breakpoint math: MainWindow's EnterCompact (MainWindow.xaml.cs) parks the
/// compact dashboard at Window.Width=470, with PanelCol Star-sized and the
/// other two grid columns collapsed to 0 — so PanelCol, and therefore
/// ReadyView, receives the window's full content width, minus the 16px
/// left/right Margin on the ScrollViewer's inner Grid (MainWindow.xaml)
/// that actually hosts ReadyView, minus a ~16px vertical scrollbar when one
/// is showing. Measured directly, pixel-for-pixel, off a real
/// MainWindow-ready-graphite.png smoke screenshot at the compact 470-wide
/// window (OrdoSort.Smoke's `screenshots` command; the demo-full workbench's
/// "Failed transfers" tile's red background ran from x=16 to x=220 inclusive
/// — a 205px tile, i.e. a 211px UniformGrid cell, i.e. a 422px UniformGrid/
/// tiles-ItemsControl ActualWidth for 2 cells): comfortably under this
/// converter's 560px breakpoint (138px of headroom), so the compact window
/// stays 2-up. Each tile's own Button carries Margin="0,0,6,6"
/// (Views/ReadyView.xaml), so a 3-column row needs >=~588px
/// (3 * (190 MinWidth + 6 margin)) before a tile would drop under its
/// ~190px MinWidth; 560px was picked instead of 588px to let the 3rd column
/// appear a little before that floor is reached exactly (tiles settle
/// around 180-186px right at the breakpoint, still comfortably readable,
/// and are >=190px within a few px of widening past it) rather than only
/// once tiles are already cramped. At the same ~48px window-to-content
/// overhead (16+16 margin, ~16 scrollbar) measured above, a 560px content
/// width — and so the 3rd column — arrives around a ~608px-wide window,
/// matching the mockup's "~620px+" widened case.
///
/// See ReadyViewTileGridIsTwoColumnsCompactThreeColumnsWide
/// (HighlightContrastTests.cs) for the permanent regression test — it
/// renders ReadyView directly at explicit widths rather than re-deriving
/// this same MainWindow chrome math on every test run.</summary>
public sealed class WidthToColumnsConverter : IValueConverter
{
    public const double Breakpoint = 560;

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is double w && w >= Breakpoint ? 3 : 2;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
