using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using OrdoSort.Core;
using OrdoSort.Smoke.E2E;
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
            notes.Add("usage: screenshots <outdir> [light|dark|both|all|<scheme key>]");
            return notes;
        }
        var outdir = args[1];
        var themeArg = args.Length > 2 ? args[2].ToLowerInvariant() : "both";
        // Legacy light/dark/both keep their old output filenames (the website
        // asset pipeline names shots -light/-dark); scheme keys and "all" name
        // shots by registry key.
        var themes = themeArg switch
        {
            "light" => new[] { ("light", ThemePalette.FindScheme("paper")!) },
            "dark" => new[] { ("dark", ThemePalette.FindScheme("graphite")!) },
            "both" => new[]
            {
                ("light", ThemePalette.FindScheme("paper")!),
                ("dark", ThemePalette.FindScheme("graphite")!),
            },
            "all" => ThemePalette.Schemes.Select(s => (s.Key, s)).ToArray(),
            _ when ThemePalette.FindScheme(themeArg) is { } s => new[] { (s.Key, s) },
            _ => Array.Empty<(string, ThemeScheme)>(),
        };
        if (themes.Length == 0)
        {
            notes.Add($"usage: unknown theme '{themeArg}' — expected light/dark/both/all or a scheme key");
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
        // this tool's use of E2EPump.Until() does. Left alone it fires mid-capture: parses
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

        foreach (var (theme, scheme) in themes)
        {
            // Force the theme: Apply(app, scheme) is the exact primitive
            // ThemeManager.Start/SetMode reduce to once "auto" is resolved —
            // it never reads the OS registry itself, so calling it directly
            // is the force path the OS-following SetMode("auto") would need
            // a real Windows preference to reach. SmokeUi.Boot() already
            // uses the bool overload for its one-shot light default; here the
            // scheme is re-applied per pass, before any window in that pass
            // is constructed, so every window resolves its DynamicResource
            // brushes correctly from birth (not just after a later re-apply).
            ThemeManager.Apply(app, scheme);

            Capture(notes, outdir, theme, "Unlock", () =>
                new UnlockWindow(new UnlockViewModel(Config.Load(cfgPath), () => true)));
            Capture(notes, outdir, theme, "ManageSaved", () =>
                new ManageSavedWindow(new UnlockViewModel(Config.Load(cfgPath), () => true)));
            Capture(notes, outdir, theme, "BulkRename", () =>
                new BulkRenameWindow(new BulkRenameViewModel()));
            Capture(notes, outdir, theme, "MatchMerge", () =>
                new MatchMergeWindow(new MatchMergeViewModel(Config.Load(cfgPath), _ => { }, dialogs)));

            // Turnaround and Production were added after this tool was written
            // and had never been captured, despite the class doc above
            // claiming every window. Both are shown with a source root already
            // listed and a SECOND add refused, so the AddNote they gained is
            // actually on screen — an empty note would prove nothing about how
            // that row lays out next to Browse/Include subfolders/Clear/Status.
            // Default width, added ONCE: no note, so its row must collapse to
            // nothing. Compare against the -narrow pair below, which add twice
            // and therefore show it. Between them the two states are covered.
            Capture(notes, outdir, theme, "Turnaround", () =>
            {
                var vm = new TurnaroundViewModel(Config.Load(cfgPath), dialogs, null);
                vm.AddPaths(new[] { demoRoot });
                return new TurnaroundWindow(vm);
            });
            Capture(notes, outdir, theme, "Production", () =>
            {
                var vm = new ProductionViewModel(Config.Load(cfgPath), dialogs, null);
                vm.AddPaths(new[] { demoRoot });
                return new ProductionWindow(vm);
            });
            Capture(notes, outdir, theme, "Production-narrow", () =>
            {
                var vm = new ProductionViewModel(Config.Load(cfgPath), dialogs, null);
                vm.AddPaths(new[] { demoRoot });
                vm.AddPaths(new[] { demoRoot });
                var w = new ProductionWindow(vm) { Width = 760 };   // MinWidth
                return w;
            });
            Capture(notes, outdir, theme, "Turnaround-narrow", () =>
            {
                var vm = new TurnaroundViewModel(Config.Load(cfgPath), dialogs, null);
                vm.AddPaths(new[] { demoRoot });
                vm.AddPaths(new[] { demoRoot });
                return new TurnaroundWindow(vm) { Width = 740 };    // MinWidth
            });

            CaptureTriage(notes, outdir, theme);
            Capture(notes, outdir, theme, "Settings", () =>
                new SettingsWindow(new SettingsViewModel(Config.Load(cfgPath), dialogs,
                    () => ThemeManager.Current, cfgPath, new SoundService())));
            // Second Settings pass parked on the Appearance tab — the scheme
            // picker is the thing these captures exist to show per scheme.
            Capture(notes, outdir, theme, "Settings-appearance", () =>
            {
                var w = new SettingsWindow(new SettingsViewModel(Config.Load(cfgPath), dialogs,
                    () => ThemeManager.Current, cfgPath, new SoundService()));
                if (FindTabControl(w) is { } tabs)
                    tabs.SelectedIndex = 5; // General/Filing/Destinations/Monitored/Alerts & polling/Appearance
                return w;
            });

            if (File.Exists(boxLabelsScratch))
            {
                Capture(notes, outdir, theme, "LabelMaker", () =>
                    new LabelMakerWindow(new LabelMakerViewModel(Config.Load(cfgPath), boxLabelsScratch, dialogs)));
            }
            else
            {
                notes.Add($"SKIP LabelMaker-{theme}: box-labels.json wasn't staged (see earlier note)");
            }
            CapturePrintPreview(notes, outdir, theme);

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
        E2EPump.Until(() => false, 1000);
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
    /// <summary>First TabControl in the logical tree — safe pre-render, unlike
    /// a visual-tree walk. Used to park the Settings capture on a chosen tab.</summary>
    private static System.Windows.Controls.TabControl? FindTabControl(DependencyObject d) =>
        d is System.Windows.Controls.TabControl t
            ? t
            : System.Windows.LogicalTreeHelper.GetChildren(d).OfType<DependencyObject>()
                .Select(FindTabControl).FirstOrDefault(x => x is not null);

    private static void Capture(List<string> notes, string outdir, string theme, string name,
        Func<Window> make, Func<bool>? ready = null)
    {
        Window? win = null;
        try
        {
            win = make();
            E2EPump.ShowOffscreen(win);
            if (ready is not null && !E2EPump.Until(ready, 8000))
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

    // ---------------------------------------------------------------- Triage
    /// <summary>Constructed the way DialogCheck builds it (a small in-memory
    /// MatchMerge result set, no real MatchMerge run needed), but shown
    /// rather than Measured so a real render happens.
    ///
    /// TriageWindow's constructor wires Loaded to `await _pdf.InitAsync();
    /// await ShowCurrentAsync();` — the same WebView2 dependency
    /// CaptureMainWindow works around, and the same "the PDF pane itself
    /// renders blank" limitation noted there applies here too (a WPF/airspace
    /// limitation, not something this capture can fix). Rather than pump the
    /// dispatcher and hope a real Edge environment comes up in time (as
    /// CaptureMainWindow does, up to 20s), this calls ShowCurrentAsync
    /// directly first: with the viewer not yet ready, its only await
    /// (_pdf.ShowAsync) no-ops and returns an already-completed task, so the
    /// whole call runs synchronously start to finish — exactly the shape
    /// DialogCheck's own duplicate-header test already proves works. That
    /// deterministically populates the progress line and candidate grid
    /// (the content that actually matters for this screenshot) without ever
    /// depending on WebView2 succeeding, or waiting on it at all.
    ///
    /// Show() below still fires the window's own Loaded handler, which will
    /// (redundantly, harmlessly) repeat the same population once/if a real
    /// WebView2 environment comes up in the background — by which point this
    /// capture has already rasterized and closed the window. Worst case that
    /// stray continuation lands on a closed window and no-ops or throws
    /// inside InitAsync's own try/catch; nothing here observes or waits on
    /// it.</summary>
    private static void CaptureTriage(List<string> notes, string outdir, string theme)
    {
        string? root = null;
        try
        {
            root = Directory.CreateTempSubdirectory("ordo_screenshots_triage").FullName;
            var pdfPath = Path.Combine(root, "20240101--1111111111.pdf");
            MinimalPdf.Write(pdfPath, "TRIAGE SAMPLE");

            var headers = new[] { "Last", "First", "Control" };
            var candidates = new[]
            {
                new MatchMerge.Candidate("111", new Dictionary<string, string>
                    { ["Last"] = "EVANS", ["First"] = "FRANK", ["Control"] = "111" }),
                new MatchMerge.Candidate("112", new Dictionary<string, string>
                    { ["Last"] = "EVANS", ["First"] = "FRANCES", ["Control"] = "112" }),
            };
            var items = new List<MatchMerge.MatchResult>
            {
                new(pdfPath, "ambiguous", "EVANS", "FRANK", Candidates: candidates),
            };

            Capture(notes, outdir, theme, "Triage", () =>
            {
                var win = new TriageWindow(items, headers);
                win.ShowCurrentAsync().GetAwaiter().GetResult();
                return win;
            });
        }
        catch (Exception ex)
        {
            notes.Add($"SKIP Triage-{theme}: {ex.Message}");
        }
        finally
        {
            if (root is not null) { try { Directory.Delete(root, true); } catch { /* best effort */ } }
        }
    }

    // ---------------------------------------------------------- PrintPreview
    /// <summary>Constructed exactly the way DialogCheck builds it — a real
    /// FixedDocument from a sample label batch, no scratch file needed (unlike
    /// LabelMaker, nothing here persists to box-labels.json). LoadPrinters()
    /// only enumerates the local print spooler (read-only) and Loaded only
    /// adjusts zoom — both synchronous, so no readiness gate is needed.</summary>
    private static void CapturePrintPreview(List<string> notes, string outdir, string theme) =>
        Capture(notes, outdir, theme, "PrintPreview", () => new PrintPreviewWindow(
            OrdoSort.Wpf.Views.LabelPrinting.BuildDocument(
                BoxLabels.Batch("ABCD", 1, 12, new DateTime(2026, 7, 25), 30)),
            "smoke", _ => { }));

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
            E2EPump.ShowOffscreen(window);

            if (!E2EPump.Until(() => window.Pdf.Ready || window.Pdf.InitError != null, 20_000))
            {
                notes.Add($"SKIP MainWindow-ready-{theme}, MainWindow-processing-{theme}: WebView2 never finished initializing");
                return;
            }
            if (!E2EPump.Until(() => window.Shell.CountLine.Length > 0, 20_000))
            {
                notes.Add($"SKIP MainWindow-ready-{theme}, MainWindow-processing-{theme}: the initial demo-full inbox scan never completed");
                return;
            }
            window.Left = -20000; window.Top = 0;   // re-assert: EnterCompact/EnterNormal reposition the window
            window.UpdateLayout();
            Save(window, outdir, "MainWindow-ready", theme);

            if (!E2EPump.Until(() => window.Shell.CurrentFilename.Length > 0, 15_000,
                    kickoff: () => window.Shell.StartProcessing()))
            {
                notes.Add($"SKIP MainWindow-processing-{theme}: the session never loaded its first document");
                return;
            }
            // give the just-grown layout (compact -> normal) a beat to settle
            E2EPump.Until(() => false, 500);
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
            E2EPump.ShowOffscreen(window);

            if (!E2EPump.Until(() => window.Pdf.Ready || window.Pdf.InitError != null, 20_000))
                throw new TimeoutException("WebView2 never finished initializing");
            if (!E2EPump.Until(() => window.Shell.CountLine.Length > 0, 20_000))
                throw new TimeoutException("the initial scan never completed");

            if (!E2EPump.Until(() => window.Shell.CurrentFilename.Length > 0, 15_000,
                    kickoff: () => window.Shell.StartProcessing()))
                throw new TimeoutException("the session never loaded its one document");

            if (!E2EPump.Until(() => window.Shell.IsDone, 15_000,
                    kickoff: () =>
                    {
                        window.Shell.TypedName = "SAMPLE DONE";
                        _ = window.Shell.OnRouteAsync(0);
                    }))
                throw new TimeoutException("the session never reached Done");

            // the Done summary fades its text/buttons in; give the animation
            // a beat so the capture isn't a half-opacity mid-transition frame
            E2EPump.Until(() => false, 800);
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
