using System.Globalization;
using System.Windows;
using System.Windows.Media;
using BoxLabelsApp.Services;
using Microsoft.Win32;
using OrdoSort.Core;
using OrdoSort.Wpf.Theme;
using OrdoSort.Wpf.ViewModels;
using OrdoSort.Wpf.Windows;

namespace BoxLabelsApp;

/// <summary>Startup: boot the theme, work out which box-labels.json to open,
/// and show the label maker as the main window.
///
/// There is no config.json here and no Config type in play. The one thing
/// this app remembers is the path to the shared store, and it opens that
/// store through the same BoxLabelStore OrdoSort uses — the exclusive
/// open-with-retries is what stops two stations issuing the same box
/// number.</summary>
public partial class App : Application
{
    /// <summary>What this application calls itself, everywhere the shared
    /// windows ask. It must not mention OrdoSort: the person given this app
    /// does not have OrdoSort and has no reason to have heard of it.</summary>
    internal const string Title = "Box Labels";

    private string _crashDir = ".";

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        _crashDir = AppContext.BaseDirectory;
        var dialogs = new BoxLabelDialogs(() => MainWindow);

        DispatcherUnhandledException += (_, ex) =>
        {
            var logged = LogCrash(ex.Exception);
            dialogs.Warn(
                "Box Labels hit a problem it wasn't expecting and stopped what it was doing.\n\n" +
                "No box number was lost: a number is only ever claimed when a sheet is actually " +
                "printed or saved, and that claim is written before anything is shown.\n\n" +
                (logged
                    ? "The technical details were written to crash.log, beside the program."
                    : "The technical details could not be written to crash.log — the folder the " +
                      "program is in may not be writable."),
                Title);
            ex.Handled = true;
            // Handled keeps a running app alive, which is right once there is
            // a window to go back to. Before then there is nothing to return
            // to and nothing that would ever close the process, so it would
            // linger invisibly.
            //
            // "is null" is not the test for that. WPF makes the first window
            // shown the MainWindow, so during startup this is the first-run
            // explanation dialog — and once the user has dismissed it, a
            // CLOSED window that is not null and cannot be returned to. Ask
            // whether there is a live window instead.
            if (MainWindow is not { IsLoaded: true }) Shutdown(1);
        };
        AppDomain.CurrentDomain.UnhandledException += (_, ex) =>
            LogCrash(ex.ExceptionObject as Exception);

        // Theme FIRST, before anything that can raise a dialog: the dialogs
        // below are real WPF windows and resolve Theme.* brushes. "auto"
        // follows the OS — this app has no settings page to override it from.
        ThemeManager.Start(this, "auto");
        // The XAML placeholder is replaced by the shared default, so the two
        // applications cannot drift apart on the font they fall back to.
        Resources["AppFontFamily"] = new FontFamily(AppFonts.DefaultChain);

        _dialogs = dialogs;
        _settingsPath = LabelsFileSettings.PathIn(AppContext.BaseDirectory);
        if (ResolveLabelsFile(e.Args, _settingsPath, dialogs) is not { } labelsFile)
        {
            Shutdown(0);   // the user cancelled the picker; nothing to show
            return;
        }

        _labelsFile = labelsFile;
        ShowLabelMaker();
    }

    private BoxLabelDialogs _dialogs = null!;
    private string _settingsPath = "";
    private string _labelsFile = "";

    /// <summary>Open the label maker on whatever store is current.</summary>
    private void ShowLabelMaker()
    {
        // migrationSeed is null: that migration reads a pre-split OrdoSort
        // config.json, and a machine running only this app has never had one.
        var vm = new LabelMakerViewModel(null, _labelsFile, _dialogs, Title);
        var window = new LabelMakerWindow(vm, Title, $"{Title} — Print preview",
            standalone: true, storeBar: new LabelStoreBar(_labelsFile, ChangeStoreFile));
        MainWindow = window;
        window.Show();
        // Only now is there a main window whose closing should end the process.
        // Until this point the app runs under OnExplicitShutdown — see App.xaml
        // for what happens otherwise.
        ShutdownMode = ShutdownMode.OnMainWindowClose;
    }

    /// <summary>The Change file… button: point this app at a different shared
    /// store and reopen on it.
    ///
    /// The window is rebuilt rather than rebound because the view model reads
    /// the store once, in its constructor, and holds the path for the life of
    /// the window — a store that could change underneath it would be a much
    /// larger change to code both applications share.</summary>
    private void ChangeStoreFile()
    {
        if (PickAndCheck(_dialogs) is not { } chosen) return;
        if (string.Equals(chosen, _labelsFile, StringComparison.OrdinalIgnoreCase)) return;

        var old = MainWindow;

        // Closing the main window would end the process, so hold that off for
        // the length of the swap and put it back afterwards either way.
        ShutdownMode = ShutdownMode.OnExplicitShutdown;
        try
        {
            var closed = false;
            void MarkClosed(object? _, EventArgs __) => closed = true;

            if (old is null) closed = true;
            else
            {
                old.Closed += MarkClosed;
                old.Close();          // runs TryPersist against the OLD store, which is right
                old.Closed -= MarkClosed;
            }

            // TryPersist refuses to close while two clients share an id. That
            // refusal has to abandon the change as well: carrying on would
            // discard the very edits it just protected, and repoint the app on
            // the way out.
            if (!closed) return;

            // Remembered only once the old window is safely closed, so an
            // abandoned change never leaves the setting pointing somewhere new.
            Remember(_settingsPath, chosen, _dialogs);
            _labelsFile = chosen;
            ShowLabelMaker();
        }
        finally
        {
            ShutdownMode = ShutdownMode.OnMainWindowClose;
        }
    }

    /// <summary>Which box-labels.json to open, asking the user when there is
    /// nothing remembered or what was remembered has gone away. Null means
    /// they cancelled and the app should close.
    ///
    /// The rules live in LabelsFileSettings.Decide, which is a pure function
    /// and tested. This is only the part that cannot be: showing a message and
    /// opening a picker.</summary>
    private string? ResolveLabelsFile(string[] args, string settingsPath, BoxLabelDialogs dialogs)
    {
        var decision = LabelsFileSettings.Decide(args, settingsPath, LabelsFileSettings.FolderReachable);
        if (decision.Prompt == LabelsFilePrompt.None) return decision.Path;

        var (message, kind) = LabelsFilePrompts.PromptFor(decision.Prompt, decision.Path);
        if (kind == MessageKind.Info) dialogs.Info(message, Title);
        else dialogs.Warn(message, Title);

        var chosen = PickAndCheck(dialogs);
        if (chosen is null) return null;

        // Honour the decision rather than always saving: a --file run is a
        // one-off and must leave the remembered path exactly as it was.
        if (decision.PersistChoice) Remember(settingsPath, chosen, dialogs);
        return chosen;
    }

    /// <summary>Pick a box-labels file and refuse the ones that are not one.
    /// Null means the user cancelled.
    ///
    /// Loops rather than closing on a bad pick: being told "that isn't it" and
    /// then having the app exit would leave someone with no way forward but to
    /// start it again and guess better.</summary>
    internal string? PickAndCheck(BoxLabelDialogs dialogs)
    {
        while (true)
        {
            var chosen = AskForLabelsFile();
            if (chosen is null) return null;

            switch (LabelsFileCheck.Classify(chosen))
            {
                case LabelsFileKind.Store:
                case LabelsFileKind.EmptyObject:
                    return chosen;

                // Legitimate for the first station ever to set this up, and
                // also exactly how someone starts a private list by mistake.
                // The buttons say which is which; a plain Yes/No here would
                // make the safe answer the one you have to think about.
                case LabelsFileKind.Missing when dialogs.Confirm(
                        $"There is no file there yet:\n\n{chosen}\n\n" +
                        "Box Labels will start a NEW, empty box-number list.\n\n" +
                        "If you meant to join the list the other stations already print from, " +
                        "choose that file instead — two separate lists will eventually put the " +
                        "same number on two different boxes.",
                        Title, "Start a new list", "Choose another file"):
                    return chosen;

                case LabelsFileKind.Missing:
                    break;   // they chose to look again

                case LabelsFileKind.NotAStore:
                    dialogs.Warn(
                        $"That does not look like a box labels file:\n\n{chosen}\n\n" +
                        "It is a settings file of some kind, but it has no client list in it — " +
                        "it may belong to another program. Using it would write box-label " +
                        "settings into it.\n\n" +
                        "Choose the shared box-labels.json instead.",
                        Title);
                    break;

                case LabelsFileKind.Unreadable:
                    dialogs.Warn(
                        $"That file cannot be read as a box labels file:\n\n{chosen}\n\n" +
                        "It may be damaged, empty, or not a settings file at all. If it is the " +
                        "right file and it is damaged, restore it from a backup before using it " +
                        "— the running box numbers live in it.",
                        Title);
                    break;
            }
        }
    }

    /// <summary>Save the choice for next launch. A failure here is not fatal —
    /// this run works fine — but it has to be said, or the app asks again on
    /// every launch and looks broken.</summary>
    private void Remember(string settingsPath, string chosen, BoxLabelDialogs dialogs)
    {
        try
        {
            LabelsFileSettings.Write(settingsPath, chosen);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            dialogs.Warn(
                "Box Labels can use that file now, but couldn't remember it for next time:\n\n" +
                ex.Message + "\n\nIt will ask again the next time it starts.",
                Title);
        }
    }

    /// <summary>CheckFileExists is off on purpose: the very first station to
    /// set this up names a box-labels.json that does not exist yet, and the
    /// store writes it on the first claim.</summary>
    private static string? AskForLabelsFile()
    {
        var dlg = new OpenFileDialog
        {
            Title = "Choose the shared box labels file",
            Filter = "Box labels file (*.json)|*.json|All files (*.*)|*.*",
            FileName = "box-labels.json",
            CheckFileExists = false,
            CheckPathExists = true,
        };
        return dlg.ShowDialog() == true ? dlg.FileName : null;
    }

    /// <summary>Appends to crash.log beside the program, and reports whether
    /// that worked — a dialog promising "the details are in crash.log" must
    /// not say so when the folder is read-only, which is exactly the case
    /// where an install under Program Files would fail.</summary>
    private bool LogCrash(Exception? ex)
    {
        if (ex is null) return false;
        try
        {
            // A stored record, not a display string: it must not change shape
            // with the station's locale.
            File.AppendAllText(Path.Combine(_crashDir, "crash.log"),
                $"[{DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)}] {ex}\n\n");
            return true;
        }
        catch (Exception) { return false; /* crash logging must never crash */ }
    }
}
