using System.Windows;
using OrdoSort.Wpf.ViewModels;

namespace OrdoSort.Wpf.Windows;

/// <summary>Small modal, owned by the Unlock window: add/remove saved
/// passwords directly against the Unlock VM's <c>Saved</c> collection —
/// exactly the manager the Settings page used to host on its sixth tab,
/// relocated here now that Unlock tries every saved password automatically.</summary>
public partial class ManageSavedWindow : Window
{
    private readonly UnlockViewModel _vm;

    public ManageSavedWindow(UnlockViewModel vm)
    {
        InitializeComponent();
        _vm = vm;
        DataContext = vm;
    }

    private void OnAddPassword(object sender, RoutedEventArgs e)
    {
        if (!_vm.AddSavedPassword(NewPwLabel.Text, NewPwValue.Password))
        {
            PwHint.Text = "Give it a name and a password first.";
            return;
        }
        PwHint.Text = "";
        NewPwLabel.Text = "";
        NewPwValue.Password = "";
    }
}
