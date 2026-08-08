using System.Windows;
using OrdoSort.Wpf.ViewModels;
using OrdoSort.Wpf.Views;

namespace OrdoSort.Wpf.Windows;

public partial class HistoryWindow : Window
{
    /// <summary>Share of HistoryGrid's own LIVE ActualWidth that When/Name/
    /// Destination may grow to before ellipsizing — tracked continuously
    /// (not baked in once from a declared Width), so it stays correct as the
    /// window is resized. Smaller than MatchMerge/BulkRename's 0.35 because
    /// these three hold much shorter, more tightly bounded values (a
    /// timestamp, a person's name, a route label) than a filename or a
    /// status phrase does — and because Original/Filed-as' own 120px star
    /// floors (below, untouched by this share) already claim a fixed amount
    /// of the same grid regardless of window size, leaving these three
    /// deliberately less room. See the XAML's own comment above these
    /// columns for the measurement that kept Original/Filed-as on their
    /// existing star+MinWidth shape instead of joining them here.</summary>
    private const double ContentColumnShare = 0.18;

    public HistoryWindow(HistoryViewModel vm)
    {
        InitializeComponent();
        DataContext = vm;
        DataGridColumnCap.Track(HistoryGrid, ContentColumnShare, WhenColumn, NameColumn, DestinationColumn);
    }
}
