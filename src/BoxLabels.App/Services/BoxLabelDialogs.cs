using System.Windows;
using Microsoft.Win32;
using OrdoSort.Wpf.Services;
using OrdoSort.Wpf.Windows;

namespace BoxLabelsApp.Services;

/// <summary>The three modals the label maker opens, for this app.
///
/// Deliberately implements <see cref="ILabelDialogs"/> and not OrdoSort's
/// full IDialogService: that one also promises a password prompt and a
/// batch-date prompt, windows this app has no business owning. Implementing
/// them here to satisfy a signature would mean writing code that can only
/// ever throw.
///
/// MessageWindow rather than MessageBox.Show for the same reason OrdoSort
/// uses it: a Win32 message box is not a WPF Window, so it never sees the
/// theme and renders as a system-coloured box in front of a themed app.</summary>
public sealed class BoxLabelDialogs : ILabelDialogs
{
    private readonly Func<Window?> _owner;

    /// <summary>The owner is resolved per call, not captured: the main window
    /// does not exist yet when this is constructed — the first dialog it can
    /// raise is the one reporting that the settings file is unreadable.</summary>
    public BoxLabelDialogs(Func<Window?> owner) => _owner = owner;

    public void Warn(string message, string title) =>
        MessageWindow.Show(_owner(), message, title, MessageKind.Warning);

    public bool Confirm(string message, string title) =>
        Confirm(message, title, "Yes", "No");

    public bool Confirm(string message, string title, string yesLabel, string noLabel) =>
        MessageWindow.Confirm(_owner(), message, title, yesLabel, noLabel);

    public string? AskSaveFile(string filter, string suggestedName)
    {
        var dlg = new SaveFileDialog { Filter = filter, FileName = suggestedName };
        return dlg.ShowDialog(_owner()) == true ? dlg.FileName : null;
    }
}
