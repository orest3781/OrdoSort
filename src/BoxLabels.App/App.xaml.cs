using System.Globalization;
using System.Windows;
using System.Windows.Media;
using BoxLabelsApp.Services;
using BoxLabelsApp.Windows;
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
                "If this happened while printing or saving labels, the box numbers for them may " +
                "already have been used. Those numbers are skipped, never handed out twice.\n\n" +
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
        // below are real WPF windows and resolve Theme.* brushes. The choice
        // is made in Settings and remembered beside the exe.
        _settingsPath = LabelsFileSettings.PathIn(AppContext.BaseDirectory);
        ThemeManager.Start(this, LabelsFileSettings.ReadTheme(_settingsPath));
        // The XAML placeholder is replaced by the shared default, so the two
        // applications cannot drift apart on the font they fall back to.
        Resources["AppFontFamily"] = AppFonts.CreateDefault();
        // then the remembered font and size, set in Settings (2026-09-28)
        var (fontFamily, fontSize) = LabelsFileSettings.ReadFont(_settingsPath);
        AppFonts.Apply(this, fontFamily, fontSize);

        _dialogs = dialogs;
        _chooser = new LabelsFileChooser(dialogs, Title, AskForLabelsFile);
        if (_chooser.Resolve(e.Args, _settingsPath, LabelsFileSettings.FolderReachable,
                chosen => Remember(_settingsPath, chosen, dialogs)) is not { } labelsFile)
        {
            Shutdown(0);   // the user cancelled the picker; nothing to show
            return;
        }

        _labelsFile = labelsFile;
        ShowLabelMaker();
    }

    /// <summary>This app's own icon, the one BrandArt generates.</summary>
    internal static readonly Uri AppIconUri = new("pack://application:,,,/BoxLabels;component/app.ico");

    private BoxLabelDialogs _dialogs = null!;
    private LabelsFileChooser _chooser = null!;
    private string _settingsPath = "";
    private string _labelsFile = "";
    private LabelMakerViewModel? _labelMaker;

    /// <summary>Open the label maker on whatever store is current.</summary>
    private void ShowLabelMaker()
    {
        // migrationSeed is null: that migration reads a pre-split OrdoSort
        // config.json, and a machine running only this app has never had one.
        var vm = new LabelMakerViewModel(null, _labelsFile, _dialogs, Title);
        vm.UnexpectedError += ex => LogCrash(ex);
        _labelMaker = vm;
        var window = new LabelMakerWindow(vm, Title, $"{Title} — Print preview",
            standalone: true,
            standaloneMenu: new StandaloneMenu(_labelsFile, ChangeStoreFile, OpenSettings));
        // Set here, not in the shared XAML: OrdoSort opens the same window
        // off its Tools menu and must not wear the Box Labels icon.
        window.Icon = System.Windows.Media.Imaging.BitmapFrame.Create(AppIconUri);
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
        if (_chooser.PickAndCheck() is { } chosen) SwitchTo(chosen);
    }

    /// <summary>Close the window on the current store and reopen on
    /// <paramref name="chosen"/>, remembering it. File → Change labels file…
    /// and a file picked in Settings both end here.</summary>
    private void SwitchTo(string chosen)
    {
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

    /// <summary>Settings…: edit, then save each half and apply what saved. The
    /// label style is read from, and saved to, the labels file that will be
    /// in use after OK.</summary>
    private void OpenSettings()
    {
        var (family, size) = LabelsFileSettings.ReadFont(_settingsPath);
        BoxLabels.LabelStyle? style = null;
        var styleProblem = "";
        try { style = BoxLabelStore.Read(_labelsFile).Style; }
        catch (ConfigException ex) { styleProblem = ex.Message; }
        var vm = new BoxLabelsSettingsViewModel(_labelsFile, ThemeManager.Mode, family, size, style, styleProblem);
        var window = new BoxLabelsSettingsWindow(vm, () => _chooser.PickAndCheck()) { Owner = MainWindow };
        if (window.ShowDialog() != true) return;

        var outcome = BoxLabelsSettingsSaver.Save(_settingsPath, vm);
        if (outcome.AppearanceSaved)
        {
            ThemeManager.SetMode(this, vm.Theme);
            AppFonts.Apply(this, vm.UiFontFamily, vm.UiFontSize);
        }
        if (outcome.Failures.Count > 0)
            _dialogs.Warn(string.Join("\n\n", outcome.Failures), Title);
        if (!string.Equals(vm.LabelsFile, _labelsFile, StringComparison.OrdinalIgnoreCase))
            SwitchTo(vm.LabelsFile);          // the new window reads the style fresh
        else if (outcome.StyleSaved)
            _labelMaker?.ReloadStyle();
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
