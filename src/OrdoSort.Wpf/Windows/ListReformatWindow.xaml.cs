using System.Windows;
using OrdoSort.Wpf.ViewModels;

namespace OrdoSort.Wpf.Windows;

public partial class ListReformatWindow : Window
{
    private readonly ListReformatViewModel _vm;

    public ListReformatWindow(ListReformatViewModel vm)
    {
        InitializeComponent();
        _vm = vm;
        DataContext = vm;
    }

    // CLIPBOARD RULE: System.Windows.Clipboard appears ONLY here, never in
    // the view model — Clipboard is a WPF/COM type the headless MTA tests
    // can't safely touch.
    private void OnPasteAndCopy(object sender, RoutedEventArgs e)
    {
        try
        {
            _vm.InputText = Clipboard.GetText();
        }
        catch (System.Runtime.InteropServices.COMException)
        {
            // another app is holding the clipboard right now — say so instead
            // of losing the failure silently
            _vm.NoteClipboardBusy();
            return;
        }

        var text = _vm.OutputText;
        if (text.Length == 0)   // nothing pasted, or all-blank cells — Clipboard.SetText throws on ""
        {
            _vm.NoteNothingToCopy();
            return;
        }
        try
        {
            Clipboard.SetText(text);
            _vm.NoteCopied(converted: true);
        }
        catch (System.Runtime.InteropServices.COMException)
        {
            _vm.NoteClipboardBusy();
        }
    }

    private void OnCopyResult(object sender, RoutedEventArgs e)
    {
        var text = _vm.OutputText;
        if (text.Length == 0) { _vm.NoteNothingToCopy(); return; }   // Clipboard.SetText throws on ""
        try
        {
            Clipboard.SetText(text);
            _vm.NoteCopied(converted: false);
        }
        catch (System.Runtime.InteropServices.COMException)
        {
            _vm.NoteClipboardBusy();
        }
    }
}
