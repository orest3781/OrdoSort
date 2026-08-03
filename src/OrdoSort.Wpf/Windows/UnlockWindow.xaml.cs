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
    /// PreviewKeyDown rather than KeyDown, and <c>Handled</c> set
    /// UNCONDITIONALLY — including when the name is still blank, where
    /// SaveBannerCommand's CanExecute gate declines: a handled PreviewKeyDown
    /// is never promoted to KeyDown, which is what keeps AccessKeyManager
    /// (where IsDefault registers "\r") from seeing the key at all. Re-running
    /// the batch is never what Enter in this box means, blank name or not.
    /// Only Enter is touched; every other key falls through to the TextBox
    /// unchanged.</summary>
    private void OnSaveNameKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        e.Handled = true;
        if (_vm.SaveBannerCommand.CanExecute(null)) _vm.SaveBannerCommand.Execute(null);
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
        _vm.RemoveFiles(FileList.SelectedItems.Cast<string>().ToList());

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
