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
/// - TriageWindow is never Show()n — Loaded would start a real WebView2/Edge
///   init the test host cannot complete (see TriageWindowDisposalTests) — so
///   its content root is measured/arranged by hand at the target size.
/// - MainWindow's ctor parks the window at 470 wide (EnterCompact), so the
///   width under test is applied AFTER Show(), the way a user's drag would
///   (HeaderLayoutTests' pattern).</summary>
[Collection(HighlightContrastTests.Name)]
public class WindowOverflowTests
{
    private readonly HighlightContrastFixture _fx;
    public WindowOverflowTests(HighlightContrastFixture fx) => _fx = fx;

    private sealed record Probe(
        double MinWidth,
        double DefaultWidth,
        double MinHeight,
        double DefaultHeight,
        Func<(Window window, Action? cleanup)> Build,
        bool Show = true,
        bool SetWidthAfterShow = false,
        bool ProbeEveryTab = false);

    private static Dictionary<string, Probe> Registry() => new()
    {
        ["AboutWindow"] = new(340, 380, 220, 240, () => (new AboutWindow(), null)),

        ["BulkRenameWindow"] = new(700, 820, 520, 640, () =>
        {
            var vm = new BulkRenameViewModel();
            vm.Preview.Add(new RenameRow(@"C:\inbox\old-name-before-review.pdf", "old-name-before-review.pdf",
                "20240101-SMITH-JOHN.pdf", "edited by hand",
                changed: true, manual: true, needsName: false, editSeed: "20240101-SMITH-JOHN.pdf",
                noteIsProblem: false));
            return (new BulkRenameWindow(vm), null);
        }),

        ["FilenameListWindow"] = new(480, 640, 400, 560, () =>
        {
            var vm = new FilenameListViewModel(new FakeDialogs());
            vm.Rows.Add("a-long-enough-filename-to-matter.pdf");
            return (new FilenameListWindow(vm), null);
        }),

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
        }),

        ["ListReformatWindow"] = new(480, 620, 400, 520, () =>
            (new ListReformatWindow(new ListReformatViewModel
            {
                InputText = "alpha\nbravo\ncharlie\nalpha",
            }), null)),

        ["ManageSavedWindow"] = new(380, 420, 360, 420, () =>
        {
            var vm = new UnlockViewModel(new Config(), () => true);
            vm.Saved.Add(new SavedPassword { Label = "Test client", Password = "hunter2" });
            return (new ManageSavedWindow(vm), null);
        }),

        ["MatchMergeWindow"] = new(720, 840, 520, 640, () =>
        {
            var vm = new MatchMergeViewModel(new Config(), _ => { }, new FakeDialogs());
            vm.Rows.Add(new MatchRow(@"C:\inbox\a-long-enough-filename-to-matter.pdf",
                "a-long-enough-filename-to-matter.pdf", "SMITH, JOHN — 1234567890.pdf",
                "3 candidates — decide in Review matches", "ambiguous"));
            return (new MatchMergeWindow(vm), null);
        }),

        ["PageCountsWindow"] = new(580, 700, 440, 560, () =>
        {
            var vm = new PageCountsViewModel(new FakeDialogs());
            var row = new PageCountRow(@"C:\inbox\a-long-enough-filename-to-matter.pdf");
            row.Apply(new PageCounts.CountResult(row.Path, null,
                "password-protected or unreadable — couldn't count"));
            vm.Rows.Add(row);
            return (new PageCountsWindow(vm), null);
        }),

        ["PrintPreviewWindow"] = new(680, 900, 560, 840, () =>
        {
            var doc = LabelPrinting.BuildDocument(
                BoxLabels.Batch("ABCD", 1, 12, new DateTime(2026, 7, 25), 30));
            return (new PrintPreviewWindow(doc, "test", _ => { }), null);
        }),

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
        }, ProbeEveryTab: true),

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
        }, Show: false),

        ["UnlockWindow"] = new(540, 620, 560, 660, () =>
        {
            var vm = new UnlockViewModel(new Config(), () => true);
            var row = new UnlockFileRow(@"C:\inbox\20240101--1111111111-long-descriptive-scan-name.pdf");
            row.SetProbeResult(ReadinessStatus.NeedsPassword,
                "This PDF needs a password none of the saved ones supply.");
            vm.Files.Add(row);
            return (new UnlockWindow(vm), null);
        }),

        ["UnzipWindow"] = new(500, 640, 380, 500, () =>
        {
            var vm = new UnzipViewModel(new FakeDialogs());
            var row = new UnzipRow(@"C:\inbox\a-long-enough-filename-to-matter.zip");
            row.Apply(new Zipper.UnzipResult(row.Path, "error", null,
                "not a valid zip archive — a long enough exception message to matter"));
            vm.Rows.Add(row);
            return (new UnzipWindow(vm), null);
        }),

        ["ZipWindow"] = new(500, 640, 380, 500, () =>
        {
            var vm = new ZipViewModel(new FakeDialogs());
            vm.Rows.Add(new PathRow(@"C:\inbox\a-long-enough-filename-to-matter.pdf", "file"));
            return (new ZipWindow(vm), null);
        }),

        ["ZipMergeWindow"] = new(580, 700, 420, 520, () =>
        {
            var vm = new ZipMergeViewModel(new FakeDialogs());
            var row = new ZipRow(@"C:\inbox\a-long-enough-filename-to-matter.zip");
            row.Apply(new ZipMerge.MergeResult(row.Path, "error",
                Message: "couldn't read 'entry.pdf' inside the zip — a long enough exception message to matter"));
            vm.Rows.Add(row);
            return (new ZipMergeWindow(vm), null);
        }),

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
        }, SetWidthAfterShow: true),
    };

    public static TheoryData<string, double, bool> Cases()
    {
        var data = new TheoryData<string, double, bool>();
        foreach (var name in Registry().Keys)
        {
            data.Add(name, 14.0, true);    // default font, MinWidth
            data.Add(name, 18.0, false);   // large preset font, default Width
        }
        return data;
    }

    [Theory, MemberData(nameof(Cases))]
    public void NoTextElementEscapesTheWindow(string windowName, double fontSize, bool atMinWidth) => _fx.Invoke(() =>
    {
        var probe = Registry()[windowName];
        var width = atMinWidth ? probe.MinWidth : probe.DefaultWidth;
        var height = atMinWidth ? probe.MinHeight : probe.DefaultHeight;
        ThemeManager.Apply(_fx.App, dark: false);
        var defaultFont = _fx.App.Resources["AppFontSize"];
        _fx.App.Resources["AppFontSize"] = fontSize;

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
            if (probe.Show)
            {
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
                        offenders.AddRange(OverflowProbe.Escapees(content, checkVertical: true)
                            .Select(o => $"[tab {tab.Header}] {o}"));
                    }
                }
                else
                {
                    offenders.AddRange(OverflowProbe.Escapees(content, checkVertical: true));
                }
            }
            else
            {
                // never Show()n (WebView2 — see class doc): lay the content
                // root out by hand at the target size instead
                var root = (FrameworkElement)window.Content;
                root.Measure(new Size(width, height));
                root.Arrange(new Rect(0, 0, width, height));
                root.UpdateLayout();
                offenders.AddRange(OverflowProbe.Escapees(root, checkVertical: true));
            }

            Assert.True(offenders.Count == 0,
                $"{windowName} at font {fontSize}, width {width}: elements escape the window:\n  " +
                string.Join("\n  ", offenders));
        }
        finally
        {
            window.Close();
            _fx.App.Resources["AppFontSize"] = defaultFont;
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
            view.Measure(new Size(370, 2000));
            view.Arrange(new Rect(0, 0, 370, 2000));
            view.UpdateLayout();

            var offenders = OverflowProbe.HorizontalEscapees(view);
            Assert.True(offenders.Count == 0,
                $"ProcessingView at font {fontSize}, panel width 370: elements escape:\n  " +
                string.Join("\n  ", offenders));
        }
        finally
        {
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
            view.Measure(new Size(width, 2000));
            view.Arrange(new Rect(0, 0, width, 2000));
            view.UpdateLayout();

            var offenders = OverflowProbe.HorizontalEscapees(view);
            Assert.True(offenders.Count == 0,
                $"ReadyView at font {fontSize}, panel width {width}: elements escape:\n  " +
                string.Join("\n  ", offenders));
        }
        finally
        {
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
        try
        {
            var view = new DoneView();
            view.Measure(new Size(370, 2000));
            view.Arrange(new Rect(0, 0, 370, 2000));
            view.UpdateLayout();

            var offenders = OverflowProbe.HorizontalEscapees(view);
            Assert.True(offenders.Count == 0,
                $"DoneView at font {fontSize}, panel width 370: elements escape:\n  " +
                string.Join("\n  ", offenders));
        }
        finally
        {
            _fx.App.Resources["AppFontSize"] = defaultFont;
        }
    });

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
