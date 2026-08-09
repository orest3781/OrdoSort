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
    }

    // CLIPBOARD RULE: System.Windows.Clipboard appears ONLY here, never in
    // the view model — Clipboard is a WPF/COM type the headless MTA tests
    // can't safely touch.
    private void OnCopy(object sender, RoutedEventArgs e)
    {
        var text = _vm.OutputText;
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
