using System.Windows;
using System.Windows.Input;
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

    /// <summary>Enter in either entry field means "add this one", not "close
    /// the dialog". Close carries <c>IsDefault</c> and this window is shown
    /// with <c>ShowDialog</c>, so before this handler existed an unhandled
    /// Enter clicked Close and discarded whatever had been typed — with no
    /// warning and nothing saved (UI-08).
    ///
    /// Handled is set even when the add is REFUSED. The refusal already says
    /// what is wrong ("Give it a name and a password first."), and letting the
    /// key fall through in exactly that case would shut the dialog at the one
    /// moment the user is mid-correction — the worst possible time to lose the
    /// half-filled form.
    ///
    /// Scoped to the two fields rather than a window-level Return binding, so
    /// Enter anywhere else in the dialog still reaches Close.
    /// ManageSavedEnterKeyTests pins both halves.</summary>
    private void OnEntryKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        e.Handled = true;
        OnAddPassword(sender, e);
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
