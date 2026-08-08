using System.Windows;
using OrdoSort.Wpf.ViewModels;
using OrdoSort.Wpf.Views;

namespace OrdoSort.Wpf.Windows;

public partial class HistoryWindow : Window
{
    /// <summary>Share of the space HistoryGrid's columns can actually occupy
    /// (DataGridColumnCap.Track's own live, vertical-scrollbar-aware
    /// viewport — see its class doc) that When/Name/Destination may grow to
    /// before ellipsizing. Smaller than MatchMerge/BulkRename's 0.35 because
    /// these three hold much shorter, more tightly bounded values (a
    /// timestamp, a person's name, a route label) than a filename or a
    /// status phrase does — and because Original/Filed-as' own 120px star
    /// floors PLUS Undone's own natural ~66px (both below, untouched by this
    /// share, and — unlike When/Name/Destination — neither one shrinks when
    /// a vertical scrollbar claims part of the grid's outer width) already
    /// claim a combined ~306px of the same viewport regardless of window
    /// size, leaving these three deliberately less room.
    ///
    /// Fix round 3: was 0.18, tuned only against a single-row grid (no
    /// vertical scrollbar, so the full outer ActualWidth was available).
    /// Measured with 60 rows (this window's own ManyRowCount test fixture)
    /// at this window's MinWidth (700, grid ActualWidth 656, viewport after
    /// reserving the scrollbar 639): 0.18 left the three capped columns'
    /// worst-case total 1.04px OVER the viewport once the 120+120+66=306px
    /// of scrollbar-oblivious fixed width was added back in — a visible
    /// horizontal scrollbar. 0.15 leaves ~45px of margin at that same
    /// worst case (3×0.15×639 + 306 = 593.55 vs. 639 available), comfortably
    /// covering font/theme variance in Undone's exact natural width without
    /// re-measuring every time it shifts by a few pixels. See the XAML's own
    /// comment above these columns for the measurement that kept
    /// Original/Filed-as on their existing star+MinWidth shape instead of
    /// joining them here.</summary>
    private const double ContentColumnShare = 0.15;

    public HistoryWindow(HistoryViewModel vm)
    {
        InitializeComponent();
        DataContext = vm;
        DataGridColumnCap.Track(HistoryGrid, ContentColumnShare, WhenColumn, NameColumn, DestinationColumn);
    }
}
