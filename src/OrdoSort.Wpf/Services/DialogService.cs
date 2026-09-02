using System.Windows;
using Microsoft.Win32;
using OrdoSort.Core;
using OrdoSort.Wpf.Windows;

namespace OrdoSort.Wpf.Services;

public sealed class DialogService : IDialogService
{
    private readonly Window _owner;

    public DialogService(Window owner) => _owner = owner;

    // MessageWindow, not MessageBox.Show: a Win32 message box is not a WPF
    // Window, so TitleBar.Hook never saw it, no Theme.* brush reached it, and
    // it ignored the configured app font — in the four dark schemes the app
    // opened a white dialog with a light title bar on top of a dark one
    // (UI-02). File and folder pickers below stay on the OS dialogs: those are
    // shell components the user already knows, and they follow the OS theme.

    public void Warn(string message, string title) =>
        MessageWindow.Show(_owner, message, title, MessageKind.Warning);

    public void Info(string message, string title) =>
        MessageWindow.Show(_owner, message, title, MessageKind.Info);

    public bool Confirm(string message, string title) =>
        Confirm(message, title, "Yes", "No");

    public bool Confirm(string message, string title, string yesLabel, string noLabel) =>
        MessageWindow.Confirm(_owner, message, title, yesLabel, noLabel);

    public string? AskSaveFile(string filter, string suggestedName)
    {
        var dlg = new SaveFileDialog { Filter = filter, FileName = suggestedName };
        return dlg.ShowDialog(_owner) == true ? dlg.FileName : null;
    }

    public string? AskOpenFile(string filter)
    {
        var dlg = new OpenFileDialog { Filter = filter };
        return dlg.ShowDialog(_owner) == true ? dlg.FileName : null;
    }

    // Same startAt-if-it-exists guard as BrowseFolder below: an initial
    // directory that no longer exists (a removable share, a moved config)
    // must not throw, it must just fall back to the shell's own default.
    public string? AskOpenFile(string filter, string? initialDirectory)
    {
        var dlg = new OpenFileDialog { Filter = filter };
        if (!string.IsNullOrWhiteSpace(initialDirectory) && Directory.Exists(initialDirectory))
            dlg.InitialDirectory = initialDirectory;
        return dlg.ShowDialog(_owner) == true ? dlg.FileName : null;
    }

    public string[] AskOpenFiles(string filter)
    {
        var dlg = new OpenFileDialog { Filter = filter, Multiselect = true };
        return dlg.ShowDialog(_owner) == true ? dlg.FileNames : Array.Empty<string>();
    }

    public string? AskFilePath(string filter, string suggestedName)
    {
        var dlg = new OpenFileDialog
        {
            Filter = filter,
            FileName = suggestedName,
            CheckFileExists = false,   // a NEW db path is a valid answer
        };
        return dlg.ShowDialog(_owner) == true ? dlg.FileName : null;
    }

    public string? BrowseFolder(string? startAt)
    {
        var dlg = new OpenFolderDialog();
        if (!string.IsNullOrWhiteSpace(startAt) && Directory.Exists(startAt))
            dlg.InitialDirectory = startAt;
        return dlg.ShowDialog(_owner) == true ? dlg.FolderName : null;
    }

    public string? AskPassword(PasswordRequest request) => PasswordWindow.Ask(_owner, request);

    public string? AskDate(string defaultDate, int fileCount) =>
        StandardiseDateWindow.Ask(_owner, defaultDate, fileCount);
}
