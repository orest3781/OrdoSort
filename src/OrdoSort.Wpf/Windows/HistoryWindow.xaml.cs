using System.Windows;
using OrdoSort.Wpf.ViewModels;
using OrdoSort.Wpf.Views;

namespace OrdoSort.Wpf.Windows;

public partial class HistoryWindow : Window
{
    /// <summary>Share of this window's own declared Width that When/Name/
    /// Destination may grow to before ellipsizing — smaller than MatchMerge/
    /// BulkRename's 0.35 because these three hold much shorter, more tightly
    /// bounded values (a timestamp, a person's name, a route label) than a
    /// filename or a status phrase does. See the XAML's own comment above
    /// these columns for the measurement that kept Original/Filed-as on
    /// their existing star+MinWidth shape instead of joining them here.</summary>
    private const double ContentColumnShare = 0.18;

    public HistoryWindow(HistoryViewModel vm)
    {
        InitializeComponent();
        DataContext = vm;
        DataGridColumnCap.Apply(Width, ContentColumnShare, WhenColumn, NameColumn, DestinationColumn);
    }
}
