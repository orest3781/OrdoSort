using System.Windows;
using OrdoSort.Wpf.ViewModels;

namespace OrdoSort.Wpf.Windows;

public partial class ReportsWindow : Window
{
    private readonly ReportsViewModel _vm;

    public ReportsWindow(ReportsViewModel vm)
    {
        InitializeComponent();
        _vm = vm;
        DataContext = vm;
        _vm.Turnaround.CopyTextRequested += OnCopyTextRequested;
        Closed += (_, _) => _vm.Turnaround.CopyTextRequested -= OnCopyTextRequested;
    }

    public void ShowPage(int index) => _vm.ShowPage(index);

    // CLIPBOARD RULE: System.Windows.Clipboard appears ONLY here, never in
    // the view model — Clipboard is a WPF/COM type the headless MTA tests
    // can't safely touch. TurnaroundPageViewModel raises CopyTextRequested
    // with the text to copy instead of touching the clipboard itself; this
    // handler performs the actual Clipboard.SetText and reports back via
    // NoteCopied()/NoteClipboardBusy(), the same pattern FilenameListWindow
    // and PageCountsWindow already use for their own Copy buttons.
    private void OnCopyTextRequested(string text)
    {
        if (text.Length == 0) return;   // nothing to copy — Clipboard.SetText throws on ""
        try
        {
            Clipboard.SetText(text);
            _vm.Turnaround.NoteCopied();
        }
        catch (System.Runtime.InteropServices.COMException)
        {
            // another app is holding the clipboard right now — say so instead
            // of losing the failure silently
            _vm.Turnaround.NoteClipboardBusy();
        }
    }
}
