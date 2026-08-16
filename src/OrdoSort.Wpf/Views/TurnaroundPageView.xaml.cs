using System.Windows;
using System.Windows.Controls;
using OrdoSort.Wpf.ViewModels;

namespace OrdoSort.Wpf.Views;

public partial class TurnaroundPageView : UserControl
{
    public TurnaroundPageView() => InitializeComponent();

    private void OnSetAsideChipClick(object sender, RoutedEventArgs e)
    {
        if (DataContext is TurnaroundPageViewModel vm &&
            sender is FrameworkElement { Tag: string key })
            vm.InspectSetAside(key);
    }
}
