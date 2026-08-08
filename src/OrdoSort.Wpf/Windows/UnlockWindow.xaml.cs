using System.Windows;
using System.Windows.Input;
using Microsoft.Win32;
using OrdoSort.Wpf.ViewModels;

namespace OrdoSort.Wpf.Windows;

public partial class UnlockWindow : Window
{
    private readonly UnlockViewModel _vm;

    public UnlockWindow(UnlockViewModel vm)
    {
        InitializeComponent();
        _vm = vm;
        DataContext = vm;
    }

    private void OnPasswordChanged(object sender, RoutedEventArgs e) => _vm.Password = PwBox.Password;

    /// <summary>Enter in the save offer's "save password as" box saves the
    /// password — it must never reach the Unlock button's
    /// <c>IsDefault="True"</c>.
    ///
    /// <see cref="UnlockViewModel.UnlockAsync"/> calls ResetBanner at the
    /// start of every run, so falling through re-runs the whole batch AND
    /// destroys the very offer being answered. Measured before this guard,
    /// with the offer up, "Acme scans" typed and focus in the box: unlocker
    /// invocations 1 -> 2, SaveBannerName back to "", 0 saved entries.
    ///
    /// PreviewKeyDown rather than a KeyBinding, and <c>Handled</c> set
    /// UNCONDITIONALLY — including when the name is still blank and
    /// SaveBannerCommand's CanExecute gate declines. A handled PreviewKeyDown
    /// is never promoted to KeyDown, so the key never reaches InputManager's
    /// POST-PROCESS stage, which is the only stage AccessKeyManager — where
    /// IsDefault registers "\r" — ever sees. That block is stated here, in
    /// our own code, and is what the tests assert directly.
    ///
    /// CORRECTED 2026-08-03: an earlier version of this comment justified the
    /// unconditional Handled by claiming CommandManager.TranslateInput only
    /// sets Handled when CanExecute is true, so a KeyBinding would have let
    /// the blank-name case fall through to the default button. That is FALSE
    /// and was disproved at opcode level: set_Handled sits OUTSIDE the
    /// CanExecute branch, and `continueRouting` (init false) is only
    /// reassigned inside the RoutedCommand branch — SaveBannerCommand is a
    /// RelayCommand, so Handled would have come back true either way.
    /// Measured: with a KeyBinding, CanExecute false still fired the bound
    /// command 0 times AND the default button 0 times (a control with no
    /// KeyBinding fired the default button 1 time, so the harness could see
    /// it). A KeyBinding would therefore have WORKED — but only via an
    /// undocumented detail of TranslateInput's non-routed branch. The reason
    /// to keep this handler is that it blocks the default button explicitly
    /// rather than by side effect. See task-8-report.md "Fix round 2".
    ///
    /// Re-running the batch is never what Enter in this box means, blank name
    /// or not. Only Enter is touched; every other key falls through to the
    /// TextBox unchanged.</summary>
    private void OnSaveNameKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        e.Handled = true;
        if (_vm.SaveBannerCommand.CanExecute(null)) _vm.SaveBannerCommand.Execute(null);
    }

    /// <summary>Keyboard focus must not be left standing on the save offer
    /// after the offer goes away. Both ways of answering it strand focus,
    /// measured against the build before this handler existed:
    /// <code>
    /// Enter in the save box:  focus stays on the now-Collapsed TextBox, whose
    ///                         PreviewKeyDown keeps swallowing Enter — the NEXT
    ///                         Enter does nothing at all (invoked 1 -> 1)
    /// Space on Save button:   the button collapses AND (SaveBannerName back to
    ///                         "") disables, so WPF punts focus to the Window —
    ///                         Enter still works, but focus is on no control
    /// </code>
    /// Hooking the banner's own visibility covers both, and does so at the one
    /// moment focus is still inside it: measured, IsVisibleChanged fires with
    /// the save box / Save button still focused and still a descendant, BEFORE
    /// the disable that would otherwise punt focus to the Window. It also
    /// covers the third route in — a fresh unlock run calling ResetBanner
    /// while focus sits in the offer.
    ///
    /// The password box is the right landing spot: it is where the next
    /// keystroke belongs and it makes Enter live again immediately.
    /// PwBox/PwPlain swap on the "Show" checkbox, so focus follows whichever
    /// is actually visible — Keyboard.Focus on a Collapsed element silently
    /// lands nowhere.</summary>
    private void OnSaveBannerVisibilityChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (SaveBanner.IsVisible) return;
        if (Keyboard.FocusedElement is not System.Windows.Media.Visual focused) return;
        if (!SaveBanner.IsAncestorOf(focused)) return;

        if (PwBox.IsVisible) Keyboard.Focus(PwBox);
        else if (PwPlain.IsVisible) Keyboard.Focus(PwPlain);
    }

    private void OnShowPw(object sender, RoutedEventArgs e)
    {
        var show = ShowPw.IsChecked == true;
        PwPlain.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        PwBox.Visibility = show ? Visibility.Collapsed : Visibility.Visible;
        if (show) PwPlain.Text = _vm.Password;
        else PwBox.Password = _vm.Password;
    }

    /// <summary>A closed window must not keep moving files: the batch keeps
    /// running otherwise, invisibly, because the work is async and owned by
    /// the view model rather than the window.</summary>
    protected override void OnClosed(EventArgs e)
    {
        _vm.CancelUnlock();
        _vm.CancelProbes();
        _vm.ResetBanner();
        base.OnClosed(e);
    }

    private void OnManageSaved(object sender, RoutedEventArgs e) =>
        new ManageSavedWindow(_vm) { Owner = this }.ShowDialog();

    private void OnAddFiles(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog { Filter = "PDF files (*.pdf)|*.pdf", Multiselect = true };
        // fire and forget: the checks are off-thread and the list updates when
        // they land, so the dialog closes immediately instead of hanging on a
        // File.Exists per file over the network
        if (dlg.ShowDialog(this) == true) _ = _vm.AddFilesAsync(dlg.FileNames);
    }

    private void OnRemoveSelected(object sender, RoutedEventArgs e) =>
        _vm.RemoveFiles(FileList.SelectedItems.Cast<UnlockFileRow>().Select(r => r.Path).ToList());

    private void OnDragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop)
            ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private void OnDrop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.FileDrop) is string[] paths) _ = _vm.AddFilesAsync(paths);
    }
}
