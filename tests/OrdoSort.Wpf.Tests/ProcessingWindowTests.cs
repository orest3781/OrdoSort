using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using OrdoSort.Core;
using OrdoSort.Wpf.Services;
using OrdoSort.Wpf.ViewModels;
using OrdoSort.Wpf.Views;

namespace OrdoSort.Wpf.Tests;

/// <summary>Holds the next scheduled call until released — the same seam
/// ShutdownDuringCommitTests uses, to catch a commit mid-flight.</summary>
file sealed class HoldingScheduler : IWorkScheduler
{
    private readonly TaskWorkScheduler _inner = new();
    private readonly TaskCompletionSource _gate = new();
    private volatile bool _armed;

    public void ArmHold() => _armed = true;
    public void Release() => _gate.TrySetResult();

    public Task<T> Run<T>(Func<T> work)
    {
        if (_armed)
        {
            _armed = false;
            return _inner.Run(() => { _gate.Task.Wait(); return work(); });
        }
        return _inner.Run(work);
    }

    public Task Run(Action work) => _inner.Run(work);
}

/// <summary>A session in its own window (spec 2026-09-26), driven on the real
/// windows: the dashboard shown off-screen, the session's work area
/// off-screen, and a stand-in for the viewer's start so no real Edge
/// starts.</summary>
[Collection(HighlightContrastTests.Name)]
public class ProcessingWindowTests : UiTest
{
    private readonly HighlightContrastFixture _fx;
    public ProcessingWindowTests(HighlightContrastFixture fx) : base(fx) => _fx = fx;

    private static readonly Rect OffScreen = new(-20000, 0, 1600, 1000);

    private sealed class Bed : IDisposable
    {
        public string Dir { get; } = Path.Combine(Path.GetTempPath(), "ordo_pw_" + Guid.NewGuid());
        public string Inbox => Path.Combine(Dir, "inbox");
        public string RouteDir => Path.Combine(Dir, "routed");
        public Config Cfg { get; }
        public string CfgPath => Path.Combine(Dir, "config.json");
        public int ViewerStarts;

        public Bed(int files, string hotkey = "")
        {
            Directory.CreateDirectory(Inbox);
            Directory.CreateDirectory(Path.Combine(Dir, "deferred"));
            Directory.CreateDirectory(RouteDir);
            Cfg = new Config
            {
                Inbox = Inbox,
                Deferred = Path.Combine(Dir, "deferred"),
                HistoryDb = Path.Combine(Dir, "history.sqlite"),
                Sort = "filename_asc",
                Routes = { new Route { Label = "Filed", Path = RouteDir, Color = "#2e7d32", Hotkey = hotkey } },
            };
            for (var i = 0; i < files; i++)
                File.WriteAllText(Path.Combine(Inbox, $"20240115--11111{i}.pdf"), "pdf");
        }

        public void Dispose()
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            for (var i = 0; i < 10; i++)
            {
                try { Directory.Delete(Dir, true); break; } catch { Thread.Sleep(50); }
            }
        }
    }

    private MainWindow Open(Bed bed, bool viewerStarts = true, IDialogService? dialogs = null)
    {
        MainWindow window = null!;
        _fx.Invoke(() =>
        {
            window = new MainWindow(bed.Cfg, bed.CfgPath,
                () => { bed.ViewerStarts++; return Task.FromResult(viewerStarts); },
                () => OffScreen)
            {
                ShowActivated = false,
            };
            if (dialogs is not null) window.Dialogs = dialogs;
            window.Left = -20000;
            window.Top = 0;
            window.Show();
        });
        WaitFor(() => window.Shell.StartEnabled, "the dashboard should have scanned the inbox");
        return window;
    }

    private void Close(MainWindow window) => _fx.Invoke(() =>
    {
        typeof(MainWindow).GetField("_reallyExit", BindingFlags.NonPublic | BindingFlags.Instance)!
            .SetValue(window, true);
        window.Close();
    });

    private void WaitFor(Func<bool> condition, string because, int timeoutMs = 8000)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (true)
        {
            var hit = false;
            _fx.Invoke(() => hit = condition());
            if (hit) return;
            if (sw.ElapsedMilliseconds > timeoutMs)
                Assert.Fail($"condition never became true within {timeoutMs}ms: {because}");
            Thread.Sleep(10);
        }
    }

    private void Start(MainWindow window)
    {
        _fx.Invoke(() => window.Shell.StartProcessing());
        WaitFor(() => window.Shell.IsProcessing && window.Processing.IsVisible
                && !window.Shell.IsBusy && window.Shell.CurrentFilename.Length > 0,
            "the session should be running in its own window, its first document loaded");
    }

    private static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T hit) yield return hit;
            foreach (var deeper in Descendants<T>(child)) yield return deeper;
        }
    }

    [Fact]
    public void StartOpensTheProcessingWindowAndTheDashboardStaysOnTheReadyScreen()
    {
        using var bed = new Bed(files: 2);
        var window = Open(bed);
        try
        {
            Start(window);

            _fx.Invoke(() =>
            {
                Assert.True(window.IsVisible);
                var ready = Assert.Single(Descendants<ReadyView>(window));
                Assert.Equal(Visibility.Visible, ready.Visibility);
                Assert.Empty(Descendants<ProcessingView>(window));
                Assert.Single(Descendants<ProcessingView>(window.Processing));
            });
        }
        finally { Close(window); }
    }

    [Fact]
    public void XOnTheProcessingWindowMidSessionStopsTheSession()
    {
        using var bed = new Bed(files: 2);
        var window = Open(bed);
        try
        {
            Start(window);

            _fx.Invoke(() => window.Processing.Close());

            WaitFor(() => window.Shell.IsReady && !window.Processing.IsVisible,
                "X should stop the session and hide its window");
        }
        finally { Close(window); }
    }

    /// <summary>Review Focus 4: StopSession refuses mid-commit, so the window
    /// must stay up showing the session rather than hide over a half-finished
    /// move.</summary>
    [Fact]
    public void XWhileADocumentIsMidCommitKeepsTheWindowOpen()
    {
        using var bed = new Bed(files: 2);
        var window = Open(bed);
        var gate = new HoldingScheduler();
        try
        {
            _fx.Invoke(() =>
                typeof(ShellViewModel).GetField("_scheduler", BindingFlags.NonPublic | BindingFlags.Instance)!
                    .SetValue(window.Shell, gate));
            Start(window);
            _fx.Invoke(() =>
            {
                window.Shell.TypedName = "SMITH JOHN";
                gate.ArmHold();
                window.Shell.RouteCommand.Execute(0);
                Assert.True(window.Shell.IsBusy);

                window.Processing.Close();

                Assert.True(window.Processing.IsVisible);
                Assert.True(window.Shell.IsProcessing);
            });
        }
        finally
        {
            gate.Release();
            WaitFor(() => !window.Shell.IsBusy, "the held commit should land");
            Close(window);
        }
    }

    [Fact]
    public async Task ClosingTheDoneSummaryReturnsToReadyAndHidesTheWindow()
    {
        using var bed = new Bed(files: 1);
        var window = Open(bed);
        try
        {
            Start(window);
            Task skip = null!;
            _fx.Invoke(() => skip = window.Shell.OnSkipAsync());
            await skip;
            WaitFor(() => window.Shell.IsDone, "setting the only document aside should finish the session");

            _fx.Invoke(() => window.Processing.Close());

            WaitFor(() => window.Shell.IsReady && !window.Processing.IsVisible,
                "closing Done should return to Ready and hide the window");
        }
        finally { Close(window); }
    }

    [Fact]
    public void TheDashboardsButtonBringsTheSessionForward()
    {
        using var bed = new Bed(files: 2);
        var window = Open(bed);
        try
        {
            Start(window);
            _fx.Invoke(() =>
            {
                window.Processing.WindowState = WindowState.Minimized;

                window.Shell.StartCommand.Execute(null);

                Assert.Equal(WindowState.Normal, window.Processing.WindowState);
                Assert.True(window.Shell.IsProcessing);
            });
        }
        finally { Close(window); }
    }

    [Fact]
    public void RouteHotkeysLiveOnTheProcessingWindow()
    {
        using var bed = new Bed(files: 2, hotkey: "Ctrl+1");
        var window = Open(bed);
        try
        {
            Start(window);
            _fx.Invoke(() =>
            {
                Assert.Contains(window.Processing.InputBindings.OfType<KeyBinding>(), b => b.Key == Key.D1);
                Assert.DoesNotContain(window.InputBindings.OfType<KeyBinding>(), b => b.Key == Key.D1);
            });
        }
        finally { Close(window); }
    }

    /// <summary>Review Focus 1: the viewer starts at launch; a failure is
    /// reported there, once, and sessions still run.</summary>
    [Fact]
    public void AViewerThatFailsToStartWarnsAtLaunchOnceAndSessionsStillRun()
    {
        using var bed = new Bed(files: 2);
        var dialogs = new FakeDialogs();
        var window = Open(bed, viewerStarts: false, dialogs: dialogs);
        try
        {
            WaitFor(() => dialogs.Warnings.Count == 1, "a viewer that fails should be reported at launch");
            _fx.Invoke(() => Assert.False(window.Processing.IsVisible));

            Start(window);
            _fx.Invoke(() => window.Shell.StopSession());
            WaitFor(() => window.Shell.IsReady, "stop");
            Start(window);

            Assert.Single(dialogs.Warnings);
            Assert.Equal(1, bed.ViewerStarts);
        }
        finally { Close(window); }
    }

    /// <summary>Review Focus 6: Windows ending the session must never be
    /// cancelled by the session window.</summary>
    [Fact]
    public void WindowsShuttingDownClosesTheProcessingWindowForReal()
    {
        using var bed = new Bed(files: 2);
        var window = Open(bed);
        var closed = false;
        try
        {
            Start(window);
            _fx.Invoke(() =>
            {
                window.Processing.Closed += (_, _) => closed = true;

                window.OnWindowsSessionEnding();

                Assert.True(closed);
            });
        }
        finally { Close(window); }
    }

    /// <summary>Review Focus 7: flash only when the person is in neither
    /// window.</summary>
    [Theory]
    [InlineData(false, false, true)]
    [InlineData(true, false, false)]
    [InlineData(false, true, false)]
    public void TheTaskbarFlashesOnlyWhenNeitherWindowIsInFront(bool dashboard, bool session, bool flash) =>
        Assert.Equal(flash, MainWindow.FlashFor(dashboard, session));

    /// <summary>Review Focus 5: Refresh would end the session or wipe Done.</summary>
    [Fact]
    public async Task TheRefreshButtonIsHiddenWhileASessionIsOpen()
    {
        using var bed = new Bed(files: 1);
        var window = Open(bed);
        try
        {
            Button Refresh() => Descendants<Button>(window).Single(b => Equals(b.ToolTip, "Rescan the inbox now"));
            _fx.Invoke(() => Assert.Equal(Visibility.Visible, Refresh().Visibility));

            Start(window);
            _fx.Invoke(() => { window.UpdateLayout(); Assert.Equal(Visibility.Collapsed, Refresh().Visibility); });

            Task skip = null!;
            _fx.Invoke(() => skip = window.Shell.OnSkipAsync());
            await skip;
            WaitFor(() => window.Shell.IsDone, "the session should reach Done");
            _fx.Invoke(() => { window.UpdateLayout(); Assert.Equal(Visibility.Collapsed, Refresh().Visibility); });
        }
        finally { Close(window); }
    }

    /// <summary>Closing the app while the viewer is still starting (the
    /// dashboard closed within a second of launch) must not throw when that
    /// start finishes: the window it would hide is already gone. Found when
    /// the accessibility suite closed the dashboard mid-start and crashed
    /// the test host.</summary>
    [Fact]
    public void AViewerStartThatFinishesAfterTheAppClosedIsHarmless()
    {
        var init = new TaskCompletionSource<bool>();
        Task<bool> warm = null!;
        _fx.Invoke(() =>
        {
            var processing = new ProcessingWindow(() => OffScreen, () => init.Task) { ShowActivated = false };
            warm = processing.WarmUpAsync();
            processing.CloseForReal();
        });

        Exception? thrown = null;
        _fx.Invoke(() =>
        {
            init.SetResult(true);
            try
            {
                System.Windows.Threading.Dispatcher.CurrentDispatcher.Invoke(() => { },
                    System.Windows.Threading.DispatcherPriority.Background);
            }
            catch (Exception ex) { thrown = ex; }
        });
        WaitFor(() => warm.IsCompleted, "the warm-up should finish");

        Assert.Null(thrown);
        Assert.Null(warm.Exception);
        Assert.True(warm.Result);
    }
}
