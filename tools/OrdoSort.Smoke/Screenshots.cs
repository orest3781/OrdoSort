using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using OrdoSort.Core;
using OrdoSort.Wpf;
using OrdoSort.Wpf.Services;
using OrdoSort.Wpf.Theme;
using OrdoSort.Wpf.ViewModels;
using OrdoSort.Wpf.Windows;

/// <summary>Renders every app window off-screen to PNGs — the before/after
/// evidence for the UI/UX refresh. Construction follows <see cref="DialogCheck"/>
/// (the same nine-ish windows, against the demo-full workbench for realistic
/// data) but shows each window instead of just Measuring it, so a real render
/// pass happens, and rasterizes it with RenderTargetBitmap.</summary>
public static class Screenshots
{
    public static int Run(string[] args) => SmokeUi.RunSta(() => Drive(args),
        "SCREENSHOTS OK", "SCREENSHOTS FAIL:");

    private static List<string> Drive(string[] args)
    {
        var notes = new List<string>();
        if (args.Length < 2)
        {
            notes.Add("usage: screenshots <outdir> [light|dark|both]");
            return notes;
        }
        var outdir = args[1];
        var themeArg = args.Length > 2 ? args[2].ToLowerInvariant() : "both";
        var themes = themeArg switch
        {
            "light" => new[] { false },
            "dark" => new[] { true },
            "both" => new[] { false, true },
            _ => Array.Empty<bool>(),
        };
        if (themes.Length == 0)
        {
            notes.Add($"usage: unknown theme '{themeArg}' — expected light/dark/both");
            return notes;
        }

        var demoRoot = Path.Combine(Directory.GetCurrentDirectory(), "demo-full");
        var cfgPath = Path.Combine(demoRoot, "config.json");
        if (!File.Exists(cfgPath))
        {
            notes.Add($"SKIP everything: {cfgPath} not found — run " +
                      "`dotnet run --project tools/OrdoSort.Smoke -- demo-full` first");
            return notes;
        }

        Directory.CreateDirectory(outdir);
        var app = SmokeUi.Boot();
        // App.xaml declares ShutdownMode="OnMainWindowClose"; InitializeComponent()
        // (inside Boot()) re-applies that XAML value over whatever the object
        // initializer set, and WPF auto-assigns Application.MainWindow to the
        // first window Show()n. Left alone, closing that first captured
        // window would tear down the whole Application mid-run — every
        // window after it would come back a blank 0x0. This tool shows and
        // closes many windows in one process, so shutdown must stay opt-in.
        app.ShutdownMode = ShutdownMode.OnExplicitShutdown;

        // Application's own base constructor queues a ONE-TIME dispatcher
        // operation that invokes the real App.OnStartup the first time
        // anything ever pumps this thread's dispatcher. SmokeUi.Boot() never
        // pumps (so DialogCheck, which only Measures, never wakes it) — but
        // this tool's Pump() does. Left alone it fires mid-capture: parses
        // (nonexistent) --config args, loads a throwaway default Config
        // beside the built exe, calls ThemeManager.Start(this, "auto") —
        // which reads the REAL OS dark-mode preference and stomps whatever
        // theme this pass just forced — and even shows its own phantom
        // MainWindow. Flushing it here, once, before the loop's first Apply,
        // means it has already fired (and can't fire again) by the time any
        // theme forcing matters.
        FlushPendingAppStartup(app);

        var dialogs = new RecordingDialogs();

        // LabelMakerWindow persists box-labels.json on Closing (real usage —
        // it is the tool's own save path, not a bug). Screenshotting it
        // against demo-full's actual file would quietly rewrite the
        // workbench every run, so it gets its own scratch copy instead.
        var scratch = Directory.CreateTempSubdirectory("ordo_screenshots").FullName;
        var boxLabelsScratch = Path.Combine(scratch, "box-labels.json");
        try { File.Copy(Path.Combine(demoRoot, "box-labels.json"), boxLabelsScratch, overwrite: true); }
        catch (Exception ex) { notes.Add($"SKIP LabelMaker (both themes): couldn't stage box-labels.json: {ex.Message}"); }

        foreach (var dark in themes)
        {
            var theme = dark ? "dark" : "light";

            // Force the theme: Apply(app, dark) is the exact primitive
            // ThemeManager.Start/SetMode reduce to once "auto" is resolved —
            // it never reads the OS registry itself, so calling it directly
            // is the force path the OS-following SetMode("auto") would need
            // a real Windows preference to reach. SmokeUi.Boot() already
            // uses it for its one-shot light default; here it's re-applied
            // per theme, before any window in that pass is constructed, so
            // every window resolves its DynamicResource brushes correctly
            // from birth (not just after a later re-apply).
            ThemeManager.Apply(app, dark);

            Capture(notes, outdir, theme, "Unlock", () =>
                new UnlockWindow(new UnlockViewModel(Config.Load(cfgPath), () => { })));
            Capture(notes, outdir, theme, "ManageSaved", () =>
                new ManageSavedWindow(new UnlockViewModel(Config.Load(cfgPath), () => { })));
            Capture(notes, outdir, theme, "BulkRename", () =>
                new BulkRenameWindow(new BulkRenameViewModel()));
            Capture(notes, outdir, theme, "MatchMerge", () =>
                new MatchMergeWindow(new MatchMergeViewModel(Config.Load(cfgPath), _ => { }, dialogs)));
            Capture(notes, outdir, theme, "Settings", () =>
                new SettingsWindow(new SettingsViewModel(Config.Load(cfgPath), dialogs,
                    () => ThemeManager.Current, cfgPath, new SoundService())));

            if (File.Exists(boxLabelsScratch))
            {
                Capture(notes, outdir, theme, "LabelMaker", () =>
                    new LabelMakerWindow(new LabelMakerViewModel(Config.Load(cfgPath), boxLabelsScratch, dialogs)));
            }
            else
            {
                notes.Add($"SKIP LabelMaker-{theme}: box-labels.json wasn't staged (see earlier note)");
            }

            CaptureHistory(notes, outdir, theme, cfgPath, dialogs);
            CaptureMainWindow(notes, outdir, theme, cfgPath);
            CaptureMainWindowDone(notes, outdir, theme);
        }

        try { Directory.Delete(scratch, true); } catch { /* best effort */ }
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        return notes;
    }

    /// <summary>Let WPF's one-time queued Application startup operation run
    /// (see the call site's comment), then discard whatever it left behind —
    /// its phantom MainWindow and its OS-driven theme — so the rest of this
    /// run starts from a clean, fully-under-our-control state.</summary>
    private static void FlushPendingAppStartup(App app)
    {
        Pump(() => false, 1000);
        foreach (var w in app.Windows.Cast<Window>().ToList())
        {
            try { w.Close(); } catch { /* best effort — it only exists to be discarded */ }
        }
    }

    // ------------------------------------------------------------ plumbing
    /// <summary>Show off-screen (a real render pass, not just Measure), wait
    /// on <paramref name="ready"/> if the window kicks off async work from its
    /// constructor/Loaded (e.g. HistoryViewModel's initial load), then
    /// rasterize and close. Anything that throws — or a ready-condition that
    /// times out — is recorded as a skip, never silently dropped.</summary>
    private static void Capture(List<string> notes, string outdir, string theme, string name,
        Func<Window> make, Func<bool>? ready = null)
    {
        Window? win = null;
        try
        {
            win = make();
            ShowOffscreen(win);
            if (ready is not null && !Pump(ready, 8000))
                notes.Add($"NOTE {name}-{theme}: async content didn't settle within 8s — captured anyway");
            win.UpdateLayout();
            Save(win, outdir, name, theme);
        }
        catch (Exception ex)
        {
            notes.Add($"SKIP {name}-{theme}: {ex.Message}");
        }
        finally
        {
            try { win?.Close(); } catch { /* best effort */ }
        }
    }

    private static void ShowOffscreen(Window win)
    {
        win.WindowStartupLocation = WindowStartupLocation.Manual;
        win.Left = -20000;
        win.Top = 0;
        win.ShowActivated = false;
        win.Show();
        win.UpdateLayout();
    }

    private static void Save(Window win, string outdir, string name, string theme)
    {
        var w = (int)Math.Ceiling(win.ActualWidth);
        var h = (int)Math.Ceiling(win.ActualHeight);
        if (w <= 0 || h <= 0)
            throw new InvalidOperationException($"zero-size window ({w}x{h}) — can't render headlessly");
        var bmp = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
        bmp.Render(win);
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(bmp));
        using var fs = File.Create(Path.Combine(outdir, $"{name}-{theme}.png"));
        enc.Save(fs);
    }

    /// <summary>Pump this thread's dispatcher (via a nested frame, not
    /// InvokeShutdown — the tool needs to do this many times over, and a shut
    /// -down dispatcher can never Run() again) until <paramref name="ready"/>
    /// is true or <paramref name="timeoutMs"/> elapses. Lets async
    /// continuations posted to the UI SynchronizationContext (WebView2 init,
    /// the initial folder scan, History's async load…) actually run.
    ///
    /// <paramref name="kickoff"/>, when given, is queued via BeginInvoke
    /// instead of called inline before the pump starts. PushFrame installs a
    /// DispatcherSynchronizationContext only for the frame it's running —
    /// between two Pump calls there is none. A Shell method invoked directly
    /// from this file (StartProcessing, OnRouteAsync) has its first `await`
    /// capture whatever context is ambient AT THAT CALL; call it between
    /// pumps and the continuation resumes on a bare thread-pool thread, which
    /// crashes the moment it touches a bound ObservableCollection
    /// (WPF's CollectionView enforces thread affinity — the exact failure
    /// this hunted down while building the Processing/Done captures). Queuing
    /// it as kickoff instead means it runs once THIS pump's frame is already
    /// live, so its await correctly captures this pump's context.</summary>
    private static bool Pump(Func<bool> ready, int timeoutMs, Action? kickoff = null)
    {
        if (kickoff is null && ready()) return true;
        var frame = new DispatcherFrame();
        var deadline = Environment.TickCount64 + timeoutMs;
        var success = false;
        var timer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(50),
        };
        timer.Tick += (_, _) =>
        {
            if (ready()) { success = true; frame.Continue = false; }
            else if (Environment.TickCount64 >= deadline) { frame.Continue = false; }
        };
        if (kickoff is not null) Dispatcher.CurrentDispatcher.BeginInvoke(kickoff);
        timer.Start();
        Dispatcher.PushFrame(frame);
        timer.Stop();
        return success;
    }

    // --------------------------------------------------------------- History
    private static void CaptureHistory(List<string> notes, string outdir, string theme,
        string cfgPath, IDialogService dialogs)
    {
        try
        {
            var cfg = Config.Load(cfgPath);
            var dbPath = ShellViewModel.ResolvePath(cfg.HistoryDb, cfgPath);
            using var history = new History(dbPath);
            var vm = new HistoryViewModel(history, dialogs);
            Capture(notes, outdir, theme, "History", () => new HistoryWindow(vm),
                ready: () => vm.FooterText.Length > 0);
        }
        catch (Exception ex)
        {
            notes.Add($"SKIP History-{theme}: {ex.Message}");
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        }
    }

    // ------------------------------------------------------------ MainWindow
    /// <summary>Ready (default dashboard) and Processing (a session started
    /// against the demo-full inbox, borrowing Reentrancy's wait-for-WebView2
    /// -then-wait-for-the-first-document approach) — both from one window, off
    /// -screen the whole time.</summary>
    private static void CaptureMainWindow(List<string> notes, string outdir, string theme, string cfgPath)
    {
        MainWindow? window = null;
        try
        {
            window = new MainWindow(Config.Load(cfgPath), cfgPath);
            window.Dialogs = new RecordingDialogs();
            ShowOffscreen(window);

            if (!Pump(() => window.Pdf.Ready || window.Pdf.InitError != null, 20_000))
            {
                notes.Add($"SKIP MainWindow-ready-{theme}, MainWindow-processing-{theme}: WebView2 never finished initializing");
                return;
            }
            if (!Pump(() => window.Shell.CountLine.Length > 0, 20_000))
            {
                notes.Add($"SKIP MainWindow-ready-{theme}, MainWindow-processing-{theme}: the initial demo-full inbox scan never completed");
                return;
            }
            window.Left = -20000; window.Top = 0;   // re-assert: EnterCompact/EnterNormal reposition the window
            window.UpdateLayout();
            Save(window, outdir, "MainWindow-ready", theme);

            if (!Pump(() => window.Shell.CurrentFilename.Length > 0, 15_000,
                    kickoff: () => window.Shell.StartProcessing()))
            {
                notes.Add($"SKIP MainWindow-processing-{theme}: the session never loaded its first document");
                return;
            }
            // give the just-grown layout (compact -> normal) a beat to settle
            Pump(() => false, 500);
            window.Left = -20000; window.Top = 0;
            window.UpdateLayout();
            Save(window, outdir, "MainWindow-processing", theme);
            notes.Add($"NOTE MainWindow-processing-{theme}: the PDF pane itself renders blank — " +
                      "WebView2 draws into its own child HWND, which RenderTargetBitmap can't capture " +
                      "(a WPF/airspace limitation, not a bug in this capture).");
        }
        catch (Exception ex)
        {
            notes.Add($"SKIP MainWindow-{theme}: {ex.Message}");
        }
        finally
        {
            try { window?.Shell.Dispose(); } catch { /* best effort */ }
        }
    }

    /// <summary>Done is not cheaply reachable against demo-full (its inbox
    /// holds ~300 files — draining the whole queue would be slow AND would
    /// consume/mutate the real workbench, filing files into its routes for
    /// real). Instead build a tiny disposable one-file session — the same
    /// shape Program.cs's default smoke run and Reentrancy use — and file
    /// it to Done. Cheap, deterministic, and never touches demo-full.</summary>
    private static void CaptureMainWindowDone(List<string> notes, string outdir, string theme)
    {
        MainWindow? window = null;
        string? root = null;
        try
        {
            root = Directory.CreateTempSubdirectory("ordo_screenshots_done").FullName;
            var inbox = Path.Combine(root, "inbox");
            var dest = Path.Combine(root, "dest");
            var deferred = Path.Combine(root, "deferred");
            foreach (var d in new[] { inbox, dest, deferred }) Directory.CreateDirectory(d);
            MinimalPdf.Write(Path.Combine(inbox, "20240101--1111111111.pdf"), "DONE STATE SAMPLE");

            var cfgPath = Path.Combine(root, "config.json");
            File.WriteAllText(cfgPath, JsonSerializer.Serialize(new
            {
                inbox = inbox.Replace('\\', '/'),
                deferred = deferred.Replace('\\', '/'),
                history_db = "history.sqlite",
                naming_mode = "replace",
                sort = "filename_asc",
                uppercase_names = true,
                routes = new[] { new { label = "Filed", path = dest.Replace('\\', '/'), hotkey = "Ctrl+1" } },
            }));

            window = new MainWindow(Config.Load(cfgPath), cfgPath);
            window.Dialogs = new RecordingDialogs();
            ShowOffscreen(window);

            if (!Pump(() => window.Pdf.Ready || window.Pdf.InitError != null, 20_000))
                throw new TimeoutException("WebView2 never finished initializing");
            if (!Pump(() => window.Shell.CountLine.Length > 0, 20_000))
                throw new TimeoutException("the initial scan never completed");

            if (!Pump(() => window.Shell.CurrentFilename.Length > 0, 15_000,
                    kickoff: () => window.Shell.StartProcessing()))
                throw new TimeoutException("the session never loaded its one document");

            if (!Pump(() => window.Shell.IsDone, 15_000,
                    kickoff: () =>
                    {
                        window.Shell.TypedName = "SAMPLE DONE";
                        _ = window.Shell.OnRouteAsync(0);
                    }))
                throw new TimeoutException("the session never reached Done");

            // the Done summary fades its text/buttons in; give the animation
            // a beat so the capture isn't a half-opacity mid-transition frame
            Pump(() => false, 800);
            window.Left = -20000; window.Top = 0;
            window.UpdateLayout();
            Save(window, outdir, "MainWindow-done", theme);
        }
        catch (Exception ex)
        {
            notes.Add($"SKIP MainWindow-done-{theme}: {ex.Message}");
        }
        finally
        {
            // Closing while Done cancels itself (Shell.RescanCommand instead)
            // — not a real close, so clean up the view model directly rather
            // than fight that guard.
            try { window?.Shell.Dispose(); } catch { /* best effort */ }
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            if (root is not null) { try { Directory.Delete(root, true); } catch { /* best effort */ } }
        }
    }
}
