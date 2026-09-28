using System.Globalization;
using System.Windows;
using OrdoSort.Core;

namespace OrdoSort.Wpf;

/// <summary>Startup: parse --config, load Config with a readable error dialog,
/// boot the theme, show the shell. Uncaught exceptions append to crash.log
/// in this PC's OrdoSort folder and surface as a dialog — the app survives.</summary>
public partial class App : Application
{
    private string _cfgPath = "";

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // 2026-08-02 audit finding I6: this used to say "Something went wrong"
        // and then paste the raw exception message in — a phrase that carries
        // no information and developer text a user cannot act on. The raw
        // detail isn't lost, it goes to crash.log (LogCrash, above); the
        // dialog now says what happened, what it means for the user's
        // documents, and where to find the detail. The reassurance is a real
        // guarantee, not a soothing noise: Commit only ever MOVES files —
        // never deletes, never overwrites (see its class doc).
        DispatcherUnhandledException += (_, ex) =>
        {
            var logged = LogCrash(ex.Exception);
            OrdoSort.Wpf.Windows.MessageWindow.Show(MainWindow,
                "OrdoSort hit a problem it wasn't expecting and stopped what it was doing.\n\n" +
                "No document was lost — OrdoSort only ever moves files, never deletes them, " +
                "so anything it was part-way through is either where it started or where it " +
                "was going.\n\n" +
                (logged
                    ? $"The technical details were written to {CrashLogPlace}."
                    : "The technical details could not be written to crash.log — the location " +
                      "may not be writable."),
                "OrdoSort — unexpected problem", OrdoSort.Wpf.Windows.MessageKind.Warning);
            ex.Handled = true;
            // Handled keeps a running app alive, which is right once there is
            // a window to go back to. Before MainWindow exists there is
            // nothing to return to, and ShutdownMode=OnMainWindowClose means
            // nothing will ever close the process — it just lingers invisibly
            // (2026-08-04 audit 3.1).
            if (MainWindow is null) Shutdown(1);
        };
        AppDomain.CurrentDomain.UnhandledException += (_, ex) =>
            LogCrash(ex.ExceptionObject as Exception);

        _cfgPath = e.Args.Length >= 2 && e.Args[0] == "--config"
            ? e.Args[1]
            : Path.Combine(AppContext.BaseDirectory, "config.json");
        _crashDir = DefaultCrashDir;

        // An unlock killed mid-run leaves a decrypted copy in %TEMP% (DW-22);
        // clear out old ones, off the UI thread so a big temp folder never
        // delays the window.
        _ = Task.Run(() => Unlock.SweepStaleTemps(Path.GetTempPath(), DateTime.UtcNow));

        // Theme FIRST, before anything that can raise a dialog. The app's
        // dialogs are real WPF windows now (MessageWindow, UI-02), so they
        // resolve Theme.* brushes and need those resources present — and the
        // failure below is the one most likely to be the first and only thing
        // a user ever sees. "auto" because the configured scheme lives in the
        // config file that has not been read yet, and following the OS is the
        // honest answer while we cannot know the preference; SetMode switches
        // to the real one once we do. Start (not SetMode) is called here
        // because it also installs the title-bar hook and the OS-preference
        // listener, and those must happen exactly once.
        Theme.ThemeManager.Start(this, "auto");

        Config cfg;
        try
        {
            cfg = Config.Load(_cfgPath);
        }
        catch (ConfigException ex)
        {
            // The parser's own Path/LineNumber/BytePositionInLine tail used to
            // be pasted straight at the user (UI-27). Config.Load's message is
            // written for a person to act on now; the raw detail rides on the
            // inner exception and goes to crash.log, where it is recoverable
            // without being in the way.
            var logged = LogCrash(ex);
            OrdoSort.Wpf.Windows.MessageWindow.Show(null,
                ex.Message + (logged
                    ? $"\n\nThe technical details were written to {CrashLogPlace}."
                    : ""),
                "OrdoSort — configuration problem", OrdoSort.Wpf.Windows.MessageKind.Warning);
            Shutdown(1);
            return;
        }

        Theme.ThemeManager.SetMode(this, cfg.Theme);
        ApplyFont(this, cfg);

        try
        {
            var window = new MainWindow(cfg, _cfgPath);
            MainWindow = window;
            window.Show();
        }
        catch (Exception ex)
        {
            // the shell ctor opens SQLite and takes the daily backup — a locked
            // or corrupt history DB must fail with a dialog, not a silent crash
            LogCrash(ex);
            OrdoSort.Wpf.Windows.MessageWindow.Show(null, "OrdoSort couldn't start:\n\n" + ex.Message,
                "OrdoSort", OrdoSort.Wpf.Windows.MessageKind.Warning);
            Shutdown(1);
        }
    }

    /// <summary>ui_font_family / ui_font_size land in the AppFontFamily and
    /// AppFontSize resources every window's style consumes. The default face
    /// lives on <see cref="Theme.AppFonts"/> in OrdoSort.Ui —
    /// BoxLabels.exe shares these styles and needs the same fallback.</summary>
    public static void ApplyFont(Application app, Config cfg)
    {
        Theme.AppFonts.Apply(app, cfg.UiFontFamily, cfg.UiFontSize);
    }

    /// <summary>Where crash.log goes: <see cref="DefaultCrashDir"/>. Static so the shell can
    /// route an unexpected filing-loop exception here too. Internal (not
    /// private) only so tests can redirect it to a throwaway temp directory
    /// instead of writing crash.log into the test binary's working directory
    /// — the same "settable only by tests" pattern as
    /// <see cref="Unlock.LargeFileThresholdBytes"/>.</summary>
    internal static string _crashDir = ".";

    /// <summary>This PC's own OrdoSort folder, never the shared config's
    /// (QC-21): crash.log records full exception text, document names and
    /// paths included, and the config folder is one every station reads.</summary>
    internal static string DefaultCrashDir { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "OrdoSort");

    /// <summary>Where crash.log is, as the app's messages name it.</summary>
    internal const string CrashLogPlace = @"crash.log, in this PC's OrdoSort folder (%LOCALAPPDATA%\OrdoSort)";

    /// <summary>Size at which crash.log is set aside as crash.old.log and a
    /// fresh one started (R4).</summary>
    internal const long MaxCrashLogBytes = 1024 * 1024;

    internal const int CrashLogRetryMs = 20;

    /// <summary>The wait between tries at a crash.log another process is
    /// writing. Settable only by tests.</summary>
    internal static Action CrashLogRetryWait = () => Thread.Sleep(CrashLogRetryMs);

    /// <summary>Appends <paramref name="ex"/> to crash.log in <see cref="_crashDir"/>.
    /// Returns whether the write actually succeeded, so callers that promise
    /// the user "the details are in crash.log" (the DispatcherUnhandledException
    /// dialog) can tell the truth when that same unwritable location that
    /// caused the crash also blocks the log (2026-08-04 audit 3.1).</summary>
    internal static bool LogCrash(Exception? ex)
    {
        if (ex is null) return false;
        try
        {
            var dir = _crashDir;
            Directory.CreateDirectory(dir);   // a PC that never ran the preview has no folder yet
            var log = Path.Combine(dir, "crash.log");
            // Invariant: a stored record (a crash.log line), not a display
            // string — must not shift shape with the station's locale.
            var entry = $"[{DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)}] {ex}\n\n";
            // A write holds crash.log exclusively, so a second OrdoSort on
            // this PC crashing at the same moment finds it busy. That entry
            // used to be dropped (DW-37); wait briefly for the other writer.
            for (var attempt = 1; ; attempt++)
            {
                try
                {
                    StartFreshWhenFull(log);
                    File.AppendAllText(log, entry);
                    return true;
                }
                catch (IOException) when (attempt < 5) { CrashLogRetryWait(); }
            }
        }
        catch (Exception) { return false; /* crash logging must never crash */ }
    }

    /// <summary>crash.log was appended to forever, so a recurring fault grew
    /// it without limit (R4). At <see cref="MaxCrashLogBytes"/> it becomes
    /// crash.old.log, replacing the one before, and a fresh log starts: the
    /// newest entries are always kept, and the two files stay near twice the
    /// cap.</summary>
    private static void StartFreshWhenFull(string log)
    {
        var info = new FileInfo(log);
        if (!info.Exists || info.Length < MaxCrashLogBytes) return;
        File.Move(log, Path.Combine(info.DirectoryName!, "crash.old.log"), overwrite: true);
    }
}
