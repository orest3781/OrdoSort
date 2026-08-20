using System.Linq;
using System.Windows;
using OrdoSort.Wpf.ViewModels;

namespace OrdoSort.Wpf.Windows;

public partial class FilenameListWindow : Window
{
    private readonly FilenameListViewModel _vm;

    public FilenameListWindow(FilenameListViewModel vm)
    {
        InitializeComponent();
        _vm = vm;
        DataContext = vm;

        // DataGridColumn is not part of the visual or logical tree (it lives
        // in DataGrid.Columns, a plain object collection, not a Panel's
        // Children), so it has no DataContext and no ancestor a
        // RelativeSource could walk up to. A column's own
        // Visibility="{Binding ...}" therefore binds to nothing and fails
        // SILENTLY — the column just stays visible forever, which is worse
        // than a compile error because the XAML looks correct. So instead
        // the window pushes each Show* flag down onto its column by name,
        // the same shape of answer SelectedPaths uses below for
        // DataGrid.SelectedItems (also not bindable): sync once now so the
        // initial state matches Columns before any toggle happens, then
        // again every time one of the Show* properties changes.
        SyncColumnVisibility();
        _vm.PropertyChanged += OnVmPropertyChanged;
        Closed += (_, _) => _vm.PropertyChanged -= OnVmPropertyChanged;
    }

    private void OnVmPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(FilenameListViewModel.ShowNumber)
            or nameof(FilenameListViewModel.ShowSize)
            or nameof(FilenameListViewModel.ShowModified)
            or nameof(FilenameListViewModel.ShowFolder)
            or nameof(FilenameListViewModel.ShowFullPath))
        {
            SyncColumnVisibility();
        }
    }

    private void SyncColumnVisibility()
    {
        NumberColumn.Visibility = _vm.ShowNumber ? Visibility.Visible : Visibility.Collapsed;
        SizeColumn.Visibility = _vm.ShowSize ? Visibility.Visible : Visibility.Collapsed;
        ModifiedColumn.Visibility = _vm.ShowModified ? Visibility.Visible : Visibility.Collapsed;
        FolderColumn.Visibility = _vm.ShowFolder ? Visibility.Visible : Visibility.Collapsed;
        FullPathColumn.Visibility = _vm.ShowFullPath ? Visibility.Visible : Visibility.Collapsed;
    }

    // DataGrid.SelectedItems is not bindable, so the window pushes the
    // selection down rather than the view model reaching up for it.
    private void OnSelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e) =>
        _vm.SelectedPaths = NamesGrid.SelectedItems
            .OfType<OrdoSort.Core.FilenameList.FileRow>()
            .Select(r => r.FullPath)
            .ToList();

    private void OnGridKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key != System.Windows.Input.Key.Delete) return;
        _vm.RemoveSelectedCommand.Execute(null);
        e.Handled = true;
    }

    // CLIPBOARD RULE: System.Windows.Clipboard appears ONLY here, never in
    // the view model — Clipboard is a WPF/COM type the headless MTA tests
    // can't safely touch.
    private void OnCopy(object sender, RoutedEventArgs e)
    {
        var text = _vm.CopyText;
        if (text.Length == 0) return;   // nothing listed yet — Clipboard.SetText throws on ""
        try
        {
            Clipboard.SetText(text);
            _vm.NoteCopied();
        }
        catch (System.Runtime.InteropServices.COMException)
        {
            // another app is holding the clipboard right now — say so instead
            // of losing the failure silently
            _vm.NoteClipboardBusy();
        }
    }

    private void OnDragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop)
            ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private void OnDrop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.FileDrop) is string[] paths) _vm.AddPaths(paths);
    }
}
