using System.Windows;
using System.Windows.Controls;
using Microsoft.Data.Sqlite;
using OrdoSort.Core;
using OrdoSort.Wpf.Services;
using OrdoSort.Wpf.Theme;
using OrdoSort.Wpf.ViewModels;
using OrdoSort.Wpf.Views;
using OrdoSort.Wpf.Windows;

namespace OrdoSort.Wpf.Tests;

/// <summary>A no-op IDialogService — same file-scoped duplicate as
/// FocusRingCoverageTests carries, for the same reason.</summary>
file sealed class NoDialogs : IDialogService
{
    public void Warn(string message, string title) { }
    public void Info(string message, string title) { }
    public bool Confirm(string message, string title) => true;
    public string? AskSaveFile(string filter, string suggestedName) => null;
    public string? AskOpenFile(string filter) => null;
    public string? AskFilePath(string filter, string suggestedName) => null;
    public string? BrowseFolder(string? startAt) => null;
}

/// <summary>LabelMakerOverflowTests, generalized to the rest of the app after
/// the Box labels report proved the failure mode is invisible to property
/// assertions (WPF paints text past its layout slot; Grids don't clip; the
/// text simply leaves the screen). Every registered window is rendered for
/// real, off-screen, twice per run: at its MinWidth with the default 14px app
/// font, and at its default Width with 18px — the largest size the Settings
/// Text tab offers as a preset. OverflowProbe then walks the visual tree and
/// fails, by element text and coordinates, if anything escapes horizontally.
///
/// Each builder deliberately seeds the state that turns conditional UI on
/// (rows added, a row selected, a Problem string forced) because an empty
/// window trivially fits. The registry is hand-maintained; the coverage gap
/// that leaves is the same one DataGridWindowCoverageTests documents for its
/// own suite. LabelMakerWindow is covered by LabelMakerOverflowTests and not
/// repeated here.
///
/// Special cases:
/// - SettingsWindow probes all seven tabs in one pass (only the selected
///   tab's content exists in the visual tree).
/// - TriageWindow used to be exempt from Show(), its content root
///   measured/arranged by hand instead, on the grounds that Loaded starts a
///   real WebView2/Edge init. That exemption made its two cases measure
///   NOTHING: UIElement.IsVisible is false for any tree with no
///   PresentationSource, so OverflowProbe skipped every candidate and both
///   cases passed over an empty list (QC-09). It is Show()n like the rest now
///   — AutoFitColumnTests' ShowOffscreenAndDriveCurrent has been doing
///   exactly that, off-screen and with the real init left running in the
///   background, since fix round 1. Its builder still drives ShowCurrentAsync
///   itself, before Show(), rather than waiting on that init to resolve.
/// - MainWindow's ctor parks the window at 470 wide (EnterCompact), so the
///   width under test is applied AFTER Show(), the way a user's drag would
///   (HeaderLayoutTests' pattern).</summary>
[Collection(HighlightContrastTests.Name)]
public class WindowOverflowTests
{
    private readonly HighlightContrastFixture _fx;
    public WindowOverflowTests(HighlightContrastFixture fx) => _fx = fx;

    /// <param name="MinExamined">How many text-bearing elements this window
    /// must put in front of OverflowProbe. Required, not defaulted, so a new
    /// registry entry cannot quietly arrive unguarded — and per-window rather
    /// than one shared number because the registry spans MainWindow's 11
    /// elements and SettingsWindow's 326 across seven tabs: a floor low enough
    /// for the smallest window proves almost nothing about the largest, which
    /// is QC-09's blindness back in partial form on exactly the windows with
    /// the most to check. Each value is three quarters of the count measured
    /// on this machine, rounded up (the count is in the comment beside it) —
    /// tight enough that losing a quarter of a window's traversal fails,
    /// loose enough to survive ordinary copy edits. Re-measure by raising the
    /// assertion below and reading the counts out of the failure messages.</param>
    internal sealed record Probe(
        double MinWidth,
        double DefaultWidth,
        double MinHeight,
        double DefaultHeight,
        Func<(Window window, Action? cleanup)> Build,
        int MinExamined,
        bool SetWidthAfterShow = false,
        bool ProbeEveryTab = false);

    /// <summary>Internal, not private: AccessibleNameTests builds the same
    /// windows, with the same realistic content, and duplicating thirteen
    /// builders would guarantee the two drift.</summary>
    internal static Dictionary<string, Probe> Registry() => new()
    {
        ["BulkRenameWindow"] = new(700, 820, 520, 640, () =>
        {
            var vm = new BulkRenameViewModel();
            vm.Preview.Add(new RenameRow(@"C:\inbox\old-name-before-review.pdf", "old-name-before-review.pdf",
                "20240101-SMITH-JOHN.pdf", "edited by hand",
                changed: true, manual: true, needsName: false, editSeed: "20240101-SMITH-JOHN.pdf",
                noteIsProblem: false));
            return (new BulkRenameWindow(vm), null);
        }, MinExamined: 40),   // 53 measured

        ["FilenameListWindow"] = new(480, 640, 400, 560, () =>
        {
            var vm = new FilenameListViewModel(new NoDialogs())
            {
                // every column on, a filter typed, Z to A ticked: the longest
                // the toolbar and the counts line ever get
                Columns = FilenameList.Columns.Number | FilenameList.Columns.Size
                        | FilenameList.Columns.Modified | FilenameList.Columns.Folder
                        | FilenameList.Columns.FullPath,
                NameFilter = "invoice",
                Descending = true,
            };
            // A widened Columns set with no ROW is an empty grid trivially
            // fitting at MinWidth — this class's own doc warns about exactly
            // that. Folder and FullPath get the long values here: they are
            // the two columns that can actually run away (Width="Auto", no
            // MinWidth cap and, as of this window, no DataGridColumnCap
            // either — see DataGridSizingCoverageTests' KnownUncovered note).
            vm.Rows.Add(new FilenameList.FileRow(
                "a-long-enough-filename-to-matter.pdf", 123456789,
                new DateTime(2026, 8, 19, 14, 30, 0),
                @"C:\inbox\a-long-enough-folder-path-to-matter-at-minwidth",
                @"C:\inbox\a-long-enough-folder-path-to-matter-at-minwidth\a-long-enough-filename-to-matter.pdf"));
            return (new FilenameListWindow(vm), null);
        }, MinExamined: 33),   // 44 measured

        ["HistoryWindow"] = new(700, 980, 400, 640, () =>
        {
            var dbPath = Path.Combine(Path.GetTempPath(), "ordo_test_overflow_" + Guid.NewGuid() + ".sqlite");
            var history = new History(dbPath);
            history.LogCommit(@"c:\in\a.pdf", "a.pdf", "A.pdf", "A",
                "insert", "", "Invoices", @"c:\out", tagged: false, "");
            var vm = new HistoryViewModel(history, new FakeDialogs(), new InlineWorkScheduler());
            return (new HistoryWindow(vm), () =>
            {
                history.Dispose();
                SqliteConnection.ClearAllPools();
                try { File.Delete(dbPath); } catch { /* best effort */ }
            });
        }, MinExamined: 20),   // 26 measured

        ["ListReformatWindow"] = new(480, 620, 400, 520, () =>
            (new ListReformatWindow(new ListReformatViewModel
            {
                // blanks + a repeat + the custom shape: the widest the counts
                // line and the picker row ever get ("N items · N blank rows
                // removed · N duplicates dropped", plus the delimiter box)
                InputText = "alpha\n\nbravo\n\ncharlie\n\nalpha",
                Dedupe = true,
                Shape = ListReformat.OutputShape.CustomDelimiter,
            }), null), MinExamined: 17),   // 22 measured

        ["ManageSavedWindow"] = new(380, 420, 360, 420, () =>
        {
            var vm = new UnlockViewModel(new Config(), () => true);
            vm.Saved.Add(new SavedPassword { Label = "Test client", Password = "hunter2" });
            return (new ManageSavedWindow(vm), null);
        }, MinExamined: 10),   // 13 measured

        ["MatchMergeWindow"] = new(720, 840, 520, 640, () =>
        {
            var vm = new MatchMergeViewModel(new Config(), _ => { }, new FakeDialogs());
            vm.Rows.Add(new MatchRow(@"C:\inbox\a-long-enough-filename-to-matter.pdf",
                "a-long-enough-filename-to-matter.pdf", "SMITH, JOHN — 1234567890.pdf",
                "3 candidates — decide in Review matches", "ambiguous"));
            return (new MatchMergeWindow(vm), null);
        }, MinExamined: 26),   // 34 measured

        // SizeToContent: heights are 0 so the probe leaves Height alone and
        // only drives Width between MinWidth and MaxWidth. A long item name
        // inside a long archive name, with the failed line showing, is the
        // widest this window gets.
        ["PasswordWindow"] = new(380, 520, 0, 0, () => (PasswordWindow.Build(null,
            new PasswordRequest("a-long-enough-document-name-to-matter.pdf",
                "a-long-enough-archive-name-to-matter.zip", true)), null), MinExamined: 8),   // 10 measured

        ["PageCountsWindow"] = new(580, 700, 440, 560, () =>
        {
            var vm = new PageCountsViewModel(new FakeDialogs());
            var row = new PageCountRow(@"C:\inbox\a-long-enough-filename-to-matter.pdf");
            row.Apply(new PageCounts.CountResult(row.Path, null,
                "password-protected or unreadable — couldn't count"));
            vm.Rows.Add(row);
            return (new PageCountsWindow(vm), null);
        }, MinExamined: 19),   // 25 measured

        ["PrintPreviewWindow"] = new(680, 900, 560, 840, () =>
        {
            var doc = LabelPrinting.BuildDocument(
                BoxLabels.Batch("ABCD", 1, 12, new DateTime(2026, 7, 25), 30));
            return (new PrintPreviewWindow(doc, "test", _ => { }), null);
        }, MinExamined: 18),   // 24 measured

        ["SettingsWindow"] = new(760, 880, 560, 820, () =>
        {
            var cfg = new Config();
            cfg.Routes.Add(new Route { Label = "Invoices", Path = @"C:\dest", Hotkey = "Ctrl+1" });
            // blank Path answers "no destination path configured" synchronously,
            // so the Problem warning row + Create-it button are VISIBLE
            cfg.Routes.Add(new Route { Label = "Broken route", Path = "" });
            cfg.WatchFolders.Add(new WatchFolder { Label = "Broken folder", Path = "", Filetypes = "pdf" });
            var cfgPath = Path.Combine(Path.GetTempPath(), "ordo_test_overflow_" + Guid.NewGuid(), "config.json");
            var vm = new SettingsViewModel(cfg, new NoDialogs(),
                () => ThemePalette.Light, cfgPath,
                uiContext: SynchronizationContext.Current);
            return (new SettingsWindow(vm), null);
        }, MinExamined: 245, ProbeEveryTab: true),   // 326 measured

        ["TriageWindow"] = new(900, 1150, 560, 720, () =>
        {
            var item = new MatchMerge.MatchResult(@"C:\inbox\doc.pdf", "suggested", "SMITH", "JOHN",
                Suggestions: new List<MatchMerge.Suggestion>
                {
                    new(new MatchMerge.Candidate("1", new Dictionary<string, string> { ["A"] = "x" }),
                        "token match on last name"),
                });
            var win = new TriageWindow(new List<MatchMerge.MatchResult> { item }, new[] { "A" })
            {
                Dialogs = new FakeDialogs(),
            };
#pragma warning disable xUnit1031 // safe: WebViewPdfViewer._ready is false pre-Show, ShowAsync no-ops
            win.ShowCurrentAsync().GetAwaiter().GetResult();
#pragma warning restore xUnit1031
            return (win, null);
        }, MinExamined: 14),   // 18 measured

        ["UnlockWindow"] = new(540, 620, 560, 660, () =>
        {
            var vm = new UnlockViewModel(new Config(), () => true);
            var row = new UnlockFileRow(@"C:\inbox\20240101--1111111111-long-descriptive-scan-name.pdf");
            row.SetProbeResult(ReadinessStatus.NeedsPassword,
                "This PDF needs a password none of the saved ones supply.");
            vm.Files.Add(row);
            return (new UnlockWindow(vm), null);
        }, MinExamined: 17),   // 22 measured

        // Both tabs are seeded and both are probed (ProbeEveryTab): only the
        // selected tab's content exists in the visual tree, and an empty list
        // is the one state that trivially fits — see this class's own doc.
        // Each list gets a failed row because the Result column's own error
        // message is the widest thing either grid ever shows.
        ["ZipToolsWindow"] = new(580, 700, 420, 520, () =>
        {
            var vm = new ZipToolsViewModel(new FakeDialogs());
            var archive = new ZipItemRow(@"C:\inbox\a-long-enough-filename-to-matter.zip", "zip");
            archive.Apply(new Zipper.UnzipResult(archive.Path, "error", null,
                "not a valid zip archive — a long enough exception message to matter"));
            vm.ZipExtract.Rows.Add(archive);
            vm.ZipExtract.Rows.Add(new ZipItemRow(@"C:\inbox\a-long-enough-filename-to-matter.pdf", "file"));

            var toMerge = new ZipItemRow(@"C:\inbox\a-long-enough-filename-to-matter.zip", "zip");
            toMerge.Apply(new PdfMerge.MergeResult(toMerge.Path, "error",
                Message: "couldn't read 'entry.pdf' inside the zip — a long enough exception message to matter"));
            vm.MergePdfs.Rows.Add(toMerge);
            return (new ZipToolsWindow(vm), null);
        }, MinExamined: 38, ProbeEveryTab: true),   // 50 measured

        // A failed zip and a locked PDF: the Result column's messages are the
        // widest thing this grid ever shows.
        ["MergePdfsWindow"] = new(580, 700, 420, 520, () =>
        {
            var vm = new MergePdfsViewModel(new FakeDialogs(), Array.Empty<string>());
            var toMerge = new ZipItemRow(@"C:\inbox\a-long-enough-filename-to-matter.zip", "zip");
            toMerge.Apply(new PdfMerge.MergeResult(toMerge.Path, "error",
                Message: "couldn't read 'entry.pdf' inside the zip — a long enough exception message to matter"));
            vm.Rows.Add(toMerge);
            var locked = new ZipItemRow(@"C:\inbox\a-long-enough-filename-to-matter.pdf", "pdf");
            locked.Mark(ZipItemRowStatus.NeedsPassword, "needs a password");
            vm.Rows.Add(locked);
            return (new MergePdfsWindow(vm), null);
        }, MinExamined: 19),   // 25 measured

        ["MainWindow"] = new(400, 470, 0, 0, () =>
        {
            var dir = Path.Combine(Path.GetTempPath(), "ordo_test_overflow_" + Guid.NewGuid());
            var watched = Path.Combine(dir, "watched");
            Directory.CreateDirectory(watched);
            var cfg = new Config
            {
                Inbox = Path.Combine(dir, "inbox"),
                Deferred = Path.Combine(dir, "deferred"),
                HistoryDb = Path.Combine(dir, "history.sqlite"),
            };
            cfg.WatchFolders.Add(new WatchFolder { Label = "Failed transfers", Path = watched, Filetypes = "pdf" });
            Directory.CreateDirectory(cfg.Inbox);
            Directory.CreateDirectory(cfg.Deferred);
            var window = new MainWindow(cfg, Path.Combine(dir, "config.json"));
            return (window, () =>
            {
                SqliteConnection.ClearAllPools();
                for (var i = 0; i < 10; i++)
                {
                    try { Directory.Delete(dir, recursive: true); break; }
                    catch (IOException) { Thread.Sleep(50); }
                    catch (UnauthorizedAccessException) { Thread.Sleep(50); }
                }
            });
        }, MinExamined: 9, SetWidthAfterShow: true),   // 11 measured
    };

    /// <summary>The default face. Named rather than inlined so the family axis
    /// below reads as a deliberate list including the default, not a special
    /// case bolted beside it.</summary>
    private const string DefaultFamily = App.DefaultFontChain;

    /// <summary>A monospace face, which is the axis this suite was missing.
    ///
    /// Font FAMILY is a first-class Appearance setting with a free-text picker,
    /// and until 2026-08-23 every case here ran in the default Segoe UI
    /// Variable — so the suite varied size and never varied width-per-character.
    /// Consolas at the same nominal size is substantially wider, and running the
    /// app under it is what exposed a destination path rendering 11 of its 38
    /// characters and a hotkey hint wrapping to four lines (UI-25, UI-26,
    /// UI-29). Neither is exotic: the demo workbench ships with
    /// ui_font_family set to exactly this.
    ///
    /// Consolas specifically because it is present on every supported Windows
    /// (it has shipped in-box since Vista), so this cannot become a test that
    /// passes only on the machine that wrote it.</summary>
    private const string WideFamily = "Consolas";

    public static TheoryData<string, double, string, bool> Cases()
    {
        var data = new TheoryData<string, double, string, bool>();
        foreach (var name in Registry().Keys)
        {
            data.Add(name, 14.0, DefaultFamily, true);    // default font, MinWidth
            data.Add(name, 18.0, DefaultFamily, false);   // large preset font, default Width
            // The harshest combination anyone can actually configure: the wider
            // face at the window's own minimum width.
            data.Add(name, 14.0, WideFamily, true);
        }
        return data;
    }

    [Theory, MemberData(nameof(Cases))]
    public void NoTextElementEscapesTheWindow(
        string windowName, double fontSize, string fontFamily, bool atMinWidth) => _fx.Invoke(() =>
    {
        var probe = Registry()[windowName];
        var width = atMinWidth ? probe.MinWidth : probe.DefaultWidth;
        var height = atMinWidth ? probe.MinHeight : probe.DefaultHeight;
        ThemeManager.Apply(_fx.App, dark: false);
        var defaultFont = _fx.App.Resources["AppFontSize"];
        var defaultFamily = _fx.App.Resources["AppFontFamily"];
        _fx.App.Resources["AppFontSize"] = fontSize;
        _fx.App.Resources["AppFontFamily"] = new System.Windows.Media.FontFamily(fontFamily);

        var (window, cleanup) = probe.Build();
        window.Left = -20000; window.Top = 0; window.ShowActivated = false;
        window.WindowStartupLocation = WindowStartupLocation.Manual;
        if (!probe.SetWidthAfterShow)
        {
            window.Width = width;
            if (height > 0) window.Height = height;
        }
        try
        {
            var offenders = new List<string>();
            var examined = 0;
            window.Show();
            if (probe.SetWidthAfterShow)
            {
                window.Width = width;
                if (height > 0) window.Height = height;
            }
            window.UpdateLayout();
            OverflowProbe.PumpRender();
            window.UpdateLayout();

            var content = (FrameworkElement)window.Content;
            if (probe.ProbeEveryTab)
            {
                var tabControl = FindDescendant<TabControl>(content)
                    ?? throw new InvalidOperationException("ProbeEveryTab set but no TabControl found");
                foreach (var tab in tabControl.Items.Cast<TabItem>())
                {
                    tabControl.SelectedItem = tab;
                    window.UpdateLayout();
                    OverflowProbe.PumpRender();
                    window.UpdateLayout();
                    offenders.AddRange(
                        OverflowProbe.Escapees(content, checkVertical: true, out var tabExamined)
                            .Select(o => $"[tab {tab.Header}] {o}"));
                    examined += tabExamined;
                }
            }
            else
            {
                offenders.AddRange(OverflowProbe.Escapees(content, checkVertical: true, out examined));
            }

            // Per-window (see Probe.MinExamined). What this proves is that
            // the traversal still reaches roughly as much of THIS window as it
            // did when the floors were measured — not that it reached every
            // element: the deliberate quarter of slack means a window could
            // lose up to a quarter of its controls to a Collapsed binding and
            // still pass. Guarding that last quarter would mean a number that
            // fails on every copy edit, and a floor that gets tuned away is
            // worth less than one that holds.
            Assert.True(examined >= probe.MinExamined,
                $"{windowName} at {fontFamily} {fontSize}, width {width}: the probe examined {examined} " +
                $"elements, below this window's floor of {probe.MinExamined} — it is measuring " +
                "less of the window than it used to, so the assertion below proves less than it " +
                "appears to");
            Assert.True(offenders.Count == 0,
                $"{windowName} at font {fontSize}, width {width}: elements escape the window:\n  " +
                string.Join("\n  ", offenders));
        }
        finally
        {
            window.Close();
            _fx.App.Resources["AppFontSize"] = defaultFont;
            _fx.App.Resources["AppFontFamily"] = defaultFamily;
            cleanup?.Invoke();
        }
    });

    /// <summary>ProcessingView never appears outside MainWindow's right panel
    /// (PanelCol: Width 430, MinWidth 370, user-draggable), so it is probed
    /// standalone at that panel's minimum — the prose-heaviest of the three
    /// views. The duck-typed stub is HighlightContrastTests' pattern: WPF
    /// resolves bindings by reflection, and unresolved ones default silently.</summary>
    [Theory]
    [InlineData(14.0)]
    [InlineData(18.0)]
    public void ProcessingViewFitsTheParkedPanel(double fontSize) => _fx.Invoke(() =>
    {
        ThemeManager.Apply(_fx.App, dark: false);
        var defaultFont = _fx.App.Resources["AppFontSize"];
        _fx.App.Resources["AppFontSize"] = fontSize;
        Window? host = null;
        try
        {
            var view = new ProcessingView
            {
                DataContext = new ProcessingViewStub
                {
                    Preview = "20240101-SMITH-JOHN-a-preview-line-long-enough-to-matter.pdf",
                    CurrentFilename = "a-long-enough-original-filename-to-matter.pdf",
                },
            };
            host = HostOffscreen(view, 370);

            var offenders = OverflowProbe.HorizontalEscapees(view, out var examined);
            // 16 judged with this stub — the eight unconditional TextBlocks,
            // three buttons and their generated labels; the route list and the
            // last-action card are the parts the stub deliberately leaves empty
            Assert.True(examined >= 8,
                $"the probe examined only {examined} elements — it is not measuring anything");
            Assert.True(offenders.Count == 0,
                $"ProcessingView at font {fontSize}, panel width 370: elements escape:\n  " +
                string.Join("\n  ", offenders));
        }
        finally
        {
            host?.Close();
            _fx.App.Resources["AppFontSize"] = defaultFont;
        }
    });

    private sealed class ProcessingViewStub
    {
        public string Preview { get; init; } = "";
        public string CurrentFilename { get; init; } = "";
    }

    /// <summary>ReadyView with a REAL ShellViewModel and four seeded watch
    /// folders (HighlightContrastTests' ShellFixture pattern), so the tile
    /// dashboard actually renders. 422 is the compact-parked panel width; 620
    /// is past the WidthToColumnsConverter breakpoint (560), exercising the
    /// multi-column tile layout.</summary>
    [Theory]
    [InlineData(14.0, 422.0)]
    [InlineData(18.0, 422.0)]
    [InlineData(14.0, 620.0)]
    public void ReadyViewTilesFitThePanel(double fontSize, double width) => _fx.Invoke(() =>
    {
        ThemeManager.Apply(_fx.App, dark: false);
        var defaultFont = _fx.App.Resources["AppFontSize"];
        _fx.App.Resources["AppFontSize"] = fontSize;
        Window? host = null;
        try
        {
            using var fx = new ShellFixture(cfg =>
            {
                for (var i = 0; i < 4; i++)
                {
                    var path = Path.Combine(cfg.Inbox, "..", $"watch{i}");
                    Directory.CreateDirectory(path);
                    cfg.WatchFolders.Add(new WatchFolder { Label = $"Folder {i}", Path = path, Filetypes = "pdf" });
                    File.WriteAllText(Path.Combine(path, "a.pdf"), "x");
                }
            });
            fx.Shell.Initialize();

            var view = new ReadyView { DataContext = fx.Shell };
            host = HostOffscreen(view, width);

            var offenders = OverflowProbe.HorizontalEscapees(view, out var examined);
            // 23 judged: four seeded watch-folder tiles (icon, label and count
            // apiece) above the big count, its caption, the detail line and the
            // two bottom buttons — at every width in the theory data
            Assert.True(examined >= 10,
                $"the probe examined only {examined} elements — it is not measuring anything");
            Assert.True(offenders.Count == 0,
                $"ReadyView at font {fontSize}, panel width {width}: elements escape:\n  " +
                string.Join("\n  ", offenders));
        }
        finally
        {
            host?.Close();
            _fx.App.Resources["AppFontSize"] = defaultFont;
        }
    });

    /// <summary>DoneView's static layout at the panel minimum — built with no
    /// DataContext at all (CopyAndTerminologyTests' ReadyView pattern:
    /// unresolved bindings default silently), so only the static prose and
    /// buttons are probed. Its bound texts are short status lines covered by
    /// the MainWindow probe.</summary>
    [Theory]
    [InlineData(14.0)]
    [InlineData(18.0)]
    public void DoneViewFitsTheParkedPanel(double fontSize) => _fx.Invoke(() =>
    {
        ThemeManager.Apply(_fx.App, dark: false);
        var defaultFont = _fx.App.Resources["AppFontSize"];
        _fx.App.Resources["AppFontSize"] = fontSize;
        Window? host = null;
        try
        {
            var view = new DoneView();
            host = HostOffscreen(view, 370);

            var offenders = OverflowProbe.HorizontalEscapees(view, out var examined);
            // 8 judged: with no DataContext the log line and its trail stay
            // collapsed, leaving the count/detail/status lines, both buttons
            // and their labels
            Assert.True(examined >= 6,
                $"the probe examined only {examined} elements — it is not measuring anything");
            Assert.True(offenders.Count == 0,
                $"DoneView at font {fontSize}, panel width 370: elements escape:\n  " +
                string.Join("\n  ", offenders));
        }
        finally
        {
            host?.Close();
            _fx.App.Resources["AppFontSize"] = defaultFont;
        }
    });

    /// <summary>The three views above are probed standalone rather than
    /// inside MainWindow, and each used to be laid out by hand — which meant
    /// they were not probed at all. UIElement.IsVisible is false for any tree
    /// whose root has no PresentationSource, OverflowProbe skips !IsVisible
    /// candidates, and so all seven cases asserted "no offenders" over an
    /// empty candidate list: a MinWidth="2000" TextBlock injected into
    /// DoneView, 2000px inside a 370px panel, still passed (QC-09). Hosting
    /// the view in a shown off-screen window is what LabelMakerOverflowTests
    /// always did and the reason that suite was never blind.
    ///
    /// The panel size is pinned on the VIEW, not on the host window, for two
    /// reasons: the probe then measures the exact width the test names rather
    /// than that width minus the host's chrome, and the view lays out at the
    /// same generous height the hand-arranged pass gave it, so only the
    /// hosting changed and nothing is newly squeezed.</summary>
    private static Window HostOffscreen(FrameworkElement view, double width)
    {
        view.Width = width;
        view.Height = 2000;
        var host = new Window
        {
            Content = view,
            Left = -20000, Top = 0, ShowActivated = false,
            WindowStartupLocation = WindowStartupLocation.Manual,
            // the host's own size decides nothing — the view is pinned above
            Width = width, Height = 600,
        };
        host.Show();
        host.UpdateLayout();
        OverflowProbe.PumpRender();
        host.UpdateLayout();
        return host;
    }

    private static T? FindDescendant<T>(DependencyObject node) where T : DependencyObject
    {
        for (var i = 0; i < System.Windows.Media.VisualTreeHelper.GetChildrenCount(node); i++)
        {
            var child = System.Windows.Media.VisualTreeHelper.GetChild(node, i);
            if (child is T hit) return hit;
            if (FindDescendant<T>(child) is { } deeper) return deeper;
        }
        return null;
    }
}
