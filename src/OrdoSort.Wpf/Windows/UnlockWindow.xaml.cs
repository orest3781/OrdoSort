using System.Windows;
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
