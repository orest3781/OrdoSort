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
        if (StartFolder(initialDirectory) is not { } start) return dlg.ShowDialog(_owner) == true ? dlg.FileName : null;
        dlg.InitialDirectory = start;
        try
        {
            return dlg.ShowDialog(_owner) == true ? dlg.FileName : null;
        }
        catch (ArgumentException)
        {
            // the shell refused the start folder: open in its default place
            var retry = new OpenFileDialog { Filter = filter };
            return retry.ShowDialog(_owner) == true ? retry.FileName : null;
        }
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
        if (StartFolder(startAt) is not { } start) return dlg.ShowDialog(_owner) == true ? dlg.FolderName : null;
        dlg.InitialDirectory = start;
        try
        {
            return dlg.ShowDialog(_owner) == true ? dlg.FolderName : null;
        }
        catch (ArgumentException)
        {
            // the shell refused the start folder: open in its default place
            var retry = new OpenFolderDialog();
            return retry.ShowDialog(_owner) == true ? retry.FolderName : null;
        }
    }

    /// <summary>The folder a picker starts in, in the form the Windows shell
    /// accepts, or null to let it open in its default place. A config can
    /// hold <c>routes/invoices</c>: resolved beside the config that becomes
    /// <c>A:\dev\routes/invoices</c>, which Directory.Exists accepts but the
    /// shell rejects ("Value does not fall within the expected range").
    /// Path.GetFullPath turns every separator into a backslash and drops
    /// <c>.</c> and <c>..</c> segments.</summary>
    /// <param name="folder">The folder asked for; may be blank or missing.</param>
    internal static string? StartFolder(string? folder)
    {
        if (string.IsNullOrWhiteSpace(folder)) return null;
        try
        {
            var full = Path.GetFullPath(folder.Trim());
            return Directory.Exists(full) ? full : null;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>The password prompt's Show toggle, carried from one prompt to
    /// the next for the life of this service — one per owning window, so
    /// one per run (UX-36).</summary>
    private bool _showPassword;

    public string? AskPassword(PasswordRequest request) =>
        PasswordWindow.Ask(_owner, request, ref _showPassword);
}
