using System.Windows;
using OrdoSort.Wpf.ViewModels;
using OrdoSort.Wpf.Views;

namespace OrdoSort.Wpf.Windows;

public partial class HistoryWindow : Window
{
    // ContentColumnShare lived here — a flat fraction of the viewport
    // that this column's cap was set to. DataGridColumnCap now computes
    // the cap as what is actually left over instead, so there is no
    // share to tune: see that class's Track doc comment for the measured
    // reason a fixed fraction truncated content while the filler column
    // beside it held space nobody was using.

    public HistoryWindow(HistoryViewModel vm)
    {
        InitializeComponent();
        DataContext = vm;
        DataGridColumnCap.Track(HistoryGrid, NameColumn, DestinationColumn);
    }
}
