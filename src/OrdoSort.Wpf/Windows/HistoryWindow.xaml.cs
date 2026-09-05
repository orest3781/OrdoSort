using System.Windows;
using OrdoSort.Wpf.ViewModels;
using OrdoSort.Wpf.Views;

namespace OrdoSort.Wpf.Windows;

public partial class HistoryWindow : Window
{
    public HistoryWindow(HistoryViewModel vm)
    {
        InitializeComponent();
        DataContext = vm;
        DataGridColumnCap.Track(HistoryGrid, NameColumn, DestinationColumn);

        // The window exists to find a filing, so the first keystroke goes
        // to the Find box without a Tab or a click (UX-37) — the same
        // Loaded-focus the password and date prompts already do.
        Loaded += (_, _) => FindBox.Focus();
    }
}
